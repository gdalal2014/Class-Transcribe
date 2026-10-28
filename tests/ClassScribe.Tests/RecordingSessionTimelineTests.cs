using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The recording session with its track on the recording clock, as the class and the microphone are recorded together
/// and as the microphone is recorded in person: each packet lands where its stamp puts it, the timer fills what nothing
/// arrives for, pauses and sleeps are left out, drift is corrected in small steps, V releases the capture and the track
/// keeps its place, the gate decides which microphone packets are written, and the stop pads the track to the end.
/// Packets are mono 16-bit at 48 kHz, so their samples go into the files as they are; stamps are in units of 100
/// nanoseconds on the test's clock, which starts ten seconds in.
/// </summary>
[TestFixture]
public sealed class RecordingSessionTimelineTests
{
    private const int Rate = 48_000;
    private static readonly AudioFormat Pcm48KMono = new(48000, 1, 16, false);
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private FakeCaptureProvider _provider = null!;
    private ManualTimeProvider _time = null!;
    private RecordingClock _clock = null!;
    private RecordingLogger<RecordingSession> _log = null!;
    private RecordingLogger<CaptureChooser> _chooserLog = null!;

    /// <summary>A temporary folder and a recording in it, the simulated devices, a clock moved by hand ten seconds in, the recording clock on it, and logs to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Class_2026-10-17_19-00-00Z");
        _provider = new FakeCaptureProvider();
        _time = new ManualTimeProvider(Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _clock = new RecordingClock(_time);
        _log = new RecordingLogger<RecordingSession>();
        _chooserLog = new RecordingLogger<CaptureChooser>();
    }

    /// <summary>Deletes the temporary folder, which fails if a test left a file open.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// A session whose track follows the recording clock, recording the device the class plays on from the simulated
    /// devices, on the test's clock unless <paramref name="time"/> gives another, and writing <paramref name="track"/>'s files.
    /// </summary>
    private RecordingSession CreateSession(RecordingOptions? options = null, Track track = Track.Main, TimeProvider? time = null, RecordingClock? clock = null)
    {
        var settings = TestOptions.Create(options ?? new RecordingOptions());
        time ??= _time;
        var source = new SourceChoice(settings).Current.Class!;
        return new RecordingSession(new LoopbackSource(_provider, new CaptureChooser(time, source, _chooserLog)),
            new TrackSpec(track, TrackPlacement.ClockAligned), clock ?? _clock, settings, time, _log);
    }

    /// <summary>A moment <paramref name="seconds"/> after the test clock's zero, in its units: a packet's stamp.</summary>
    private static long Stamp(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    /// <summary>A length of <paramref name="seconds"/>, rounded to the unit.</summary>
    private static TimeSpan Seconds(double seconds) => new(Stamp(seconds));

    /// <summary>Mono 16-bit audio of one sample value lasting <paramref name="milliseconds"/> at 48 kHz: a packet.</summary>
    private static byte[] Packet(short value, int milliseconds = 10) => TestAudio.Shorts(Enumerable.Repeat(value, Rate * milliseconds / 1000).ToArray());

    /// <summary>How long the files hold together, from what is on disk.</summary>
    private static TimeSpan LengthOf(IReadOnlyList<string> parts) =>
        parts.Aggregate(TimeSpan.Zero, (total, part) =>
        {
            var wav = TestAudio.ReadWav(part);
            return total + new TimeSpan(MediaTime.FromFrames(wav.Samples.Length, wav.SampleRate).Ticks);
        });

    /// <summary>The samples of the files joined in order; every part at 48 kHz.</summary>
    private static short[] SamplesOf(IReadOnlyList<string> parts) =>
        parts.SelectMany(part =>
        {
            var wav = TestAudio.ReadWav(part);
            Assert.That(wav.SampleRate, Is.EqualTo(Rate), $"{Path.GetFileName(part)} is not at 48 kHz");
            return wav.Samples;
        }).ToArray();

    /// <summary>The sample index <paramref name="position"/> lands on at 48 kHz.</summary>
    private static int At(TimeSpan position) => (int)MediaTime.ToFrames(position, Rate);

    /// <summary>Moves the clock on a second at a time, so that the padding timer ticks as it does in a run.</summary>
    private void TickSeconds(int count)
    {
        for (var tick = 0; tick < count; tick++) _time.Advance(TimeSpan.FromSeconds(1));
    }

    /// <summary>Moves the clock on a second at a time, device check by device check, until the condition holds.</summary>
    private async Task TickUntilAsync(Func<bool> condition, int maxTicks = 8)
    {
        for (var tick = 0; tick < maxTicks && !condition(); tick++)
        {
            var before = _provider.DeviceQueries;
            _time.Advance(TimeSpan.FromSeconds(1));
            await Wait.UntilAsync(() => _provider.DeviceQueries > before);
            await Task.Delay(50);   // lets the check finish before the clock moves again
        }

        await Wait.UntilAsync(condition);
    }

    /// <summary>
    /// Makes the disk look full to the file being written: another handle locks everything past what is on disk now, so
    /// every write beyond it fails, as it would on a full disk. Disposing the returned stream frees the space again.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileStream FillDisk(string path)
    {
        var other = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        other.Lock(other.Length, int.MaxValue);
        return other;
    }

    /// <summary>A session that follows the clock needs the clock: without one it cannot be made.</summary>
    [Test]
    public void A_clock_aligned_session_needs_the_clock()
    {
        var settings = TestOptions.Create(new RecordingOptions());
        var source = new LoopbackSource(_provider, new CaptureChooser(_time, new ClassSource.Automatic(), _chooserLog));

        Assert.That(() => new RecordingSession(source, new TrackSpec(Track.Main, TrackPlacement.ClockAligned), null, settings, _time, _log),
            Throws.ArgumentException);
    }

    /// <summary>The first stamped packet sets the clock's origin and starts the track at zero, as it is.</summary>
    [Test]
    public void The_first_packet_sets_the_origin_and_starts_the_track()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(700), Stamp(10));
        var result = session.Stop(Seconds(0.01));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.Origin, Is.EqualTo(Stamp(10)));
            Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
            Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(Enumerable.Repeat((short)700, 480)));
            Assert.That(result.HeardSound, Is.True, "700 is above the sound threshold");
        }
    }

    /// <summary>The microphone's track writes the .mic.wav files beside the class's, as its track spec says.</summary>
    [Test]
    public void The_microphone_track_writes_its_own_files()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession(track: Track.Microphone);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        var result = session.Stop(Seconds(0.01));

        Assert.That(result.SegmentPaths, Is.EqualTo(new[] { _files.Segment(1, Track.Microphone) }));
        Assert.That(File.Exists(_files.Segment(1)), Is.False, "nothing went to the class's files");
    }

    /// <summary>A packet three seconds after the one before lands three seconds in: the time between is silence in the file, not left out.</summary>
    [Test]
    public void A_gap_of_three_seconds_is_filled_with_silence()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        TickSeconds(3);                                   // the timer keeps the track current meanwhile
        capture.Deliver(Packet(9), Stamp(13.2));
        var result = session.Stop(Seconds(3.21));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(3.21))));
            Assert.That(samples[..480], Is.All.EqualTo(7));
            Assert.That(samples[480..At(Seconds(3.2))], Is.All.Zero);
            Assert.That(samples[At(Seconds(3.2))..], Is.All.EqualTo(9));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>
    /// While nothing arrives, the timer keeps the track about a second and a half behind the clock, a second at a time, so
    /// that a packet that comes at last lands within the tolerance and is never written after minutes of silence inline.
    /// </summary>
    [Test]
    public void A_long_silence_is_kept_current_by_the_timer_a_second_at_a_time()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        var written = new List<TimeSpan>();
        for (var tick = 0; tick < 10; tick++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            written.Add(session.GetStatus().Captured);
        }

        capture.Deliver(Packet(9), Stamp(20));
        var result = session.Stop(Seconds(10.01));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written[0], Is.EqualTo(Seconds(0.01)), "at 11 s the target, 9.5 s, is before the origin: nothing to pad");
            Assert.That(written[1], Is.EqualTo(Seconds(0.5)), "at 12 s the track is padded to 10.5 s on the clock");
            for (var tick = 2; tick < written.Count; tick++)
                Assert.That(written[tick] - written[tick - 1], Is.EqualTo(TimeSpan.FromSeconds(1)), $"tick {tick} padded one second");
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(Seconds(10.01)));
            Assert.That(SamplesOf(result.SegmentPaths)[At(Seconds(10))..], Is.All.EqualTo(9));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>
    /// When the clock jumps by minutes and no freeze is found, the timer pads the whole stretch, but in chunks of a second
    /// through the normal write path: with small segments, every part rolls over at its size plus one chunk, never holding
    /// minutes written in one go.
    /// </summary>
    [Test]
    public void A_jump_of_minutes_without_a_freeze_is_padded_in_chunks_of_a_second()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var looks = 0;
        using var session = CreateSession(new RecordingOptions { MaxSegmentMegabytes = 1 });
        session.SetFreezeLook(() =>
        {
            looks++;
            return null;
        });

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        _time.Advance(TimeSpan.FromSeconds(120));         // one tick, as a process that did not run sees it
        var result = session.Stop(Seconds(118.5));

        var limit = 1024L * 1024 + Rate * 2;             // the segment size, plus the one chunk that takes it over
        using (Assert.EnterMultipleScope())
        {
            Assert.That(looks, Is.EqualTo(1), "a gap beyond the inline limit asks for one look for a freeze first");
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(Seconds(118.5)), "padded to a second and a half behind the clock");
            Assert.That(result.SegmentPaths, Has.Count.GreaterThan(5));
            foreach (var part in result.SegmentPaths)
                Assert.That(new FileInfo(part).Length - WavSegmentWriter.HeaderSize, Is.LessThanOrEqualTo(limit), $"{Path.GetFileName(part)} holds more than one chunk past the segment size");
        }
    }

    /// <summary>
    /// A packet far ahead of the track on a capture thread, with no freeze found, gets its gap written first in chunks of
    /// a second through the normal write path, up to a minute; the parts roll over as they fill.
    /// </summary>
    [Test]
    public void A_packet_far_ahead_without_a_freeze_gets_its_gap_in_chunks()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession(new RecordingOptions { MaxSegmentMegabytes = 1 });

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        capture.Deliver(Packet(9), Stamp(40));            // the clock has not moved: the timer never ran
        var result = session.Stop(Seconds(30.01));

        var limit = 1024L * 1024 + Rate * 2;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(Seconds(30.01)));
            Assert.That(result.SegmentPaths, Has.Count.GreaterThan(2));
            foreach (var part in result.SegmentPaths)
                Assert.That(new FileInfo(part).Length - WavSegmentWriter.HeaderSize, Is.LessThanOrEqualTo(limit));
            Assert.That(TestAudio.ReadWav(result.SegmentPaths[^1]).Samples[^480..], Is.All.EqualTo(9));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>A packet more than a minute ahead, with no freeze found, is appended with a warning rather than padded for: that is not expected to happen.</summary>
    [Test]
    public void A_packet_more_than_a_minute_ahead_is_appended_with_a_warning()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        capture.Deliver(Packet(9), Stamp(100));
        var result = session.Stop(Seconds(0.02));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(Seconds(0.02)), "no silence was written for the gap");
            Assert.That(_log.Problems, Has.Exactly(1).Contains("ahead"));
        }
    }

    /// <summary>A device whose clock runs slow is corrected with a little silence now and then, and the track stays within the tolerance of the clock.</summary>
    [Test]
    public void A_slow_device_is_corrected_with_a_little_silence()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        for (var packet = 0; packet < 120; packet++)
            capture.Deliver(Packet(7, 100), Stamp(10 + packet * 0.101));   // 100 ms of audio every 101 ms: 1 % slow
        var result = session.Stop();

        var expected = Seconds(119 * 0.101 + 0.1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(expected).Within(TrackPlacer.Tolerance));
            Assert.That(LengthOf(result.SegmentPaths), Is.GreaterThan(Seconds(12)), "silence was added: the audio alone is 12 s");
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>A device whose clock runs fast is corrected by dropping a little audio now and then, and the track stays within the tolerance of the clock.</summary>
    [Test]
    public void A_fast_device_is_corrected_by_dropping_a_little_audio()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        for (var packet = 0; packet < 120; packet++)
            capture.Deliver(Packet(7, 100), Stamp(10 + packet * 0.099));   // 100 ms of audio every 99 ms: 1 % fast
        var result = session.Stop();

        var expected = Seconds(119 * 0.099 + 0.1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(expected).Within(TrackPlacer.Tolerance));
            Assert.That(LengthOf(result.SegmentPaths), Is.LessThan(Seconds(12)), "audio was dropped: the audio alone is 12 s");
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>
    /// A pause leaves no silence in the track: padding stops at the pause, a packet stamped inside it is dropped, a packet
    /// stamped before it but delivered during it still lands before it, and the first packet after it carries straight on.
    /// </summary>
    [Test]
    public void A_pause_leaves_no_silence_in_the_track()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        TickSeconds(1);                                   // 11 s
        _clock.Pause();
        session.Pause();
        capture.Deliver(Packet(8), Stamp(10.9));          // stamped before the pause, delivered during it
        TickSeconds(60);                                  // 71 s
        capture.Deliver(Packet(5), Stamp(40));            // stamped inside the pause
        _clock.Resume();
        session.Resume();
        capture.Deliver(Packet(9), Stamp(71));
        var result = session.Stop(Seconds(1.01));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(1.01))), "the minute paused is not in the file");
            Assert.That(samples[At(Seconds(0.9))..At(Seconds(0.91))], Is.All.EqualTo(8), "the packet from before the pause is at its place");
            Assert.That(samples[At(Seconds(1))..], Is.All.EqualTo(9), "the first packet after the pause carries on from it");
            Assert.That(samples, Has.None.EqualTo(5), "the packet stamped inside the pause was dropped");
            Assert.That(session.GetStatus().Paused, Is.False);
        }
    }

    /// <summary>Sound that arrives during a pause is still measured, as the status says, though the clock drops it.</summary>
    [Test]
    public void Sound_during_a_pause_is_measured_but_not_written()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        TickSeconds(1);
        _clock.Pause();
        session.Pause();
        capture.Deliver(Packet(9000), Stamp(11.5));
        var status = session.GetStatus();
        _clock.Resume();
        session.Resume();
        var result = session.Stop(Seconds(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.SoundWhilePaused, Is.True);
            Assert.That(SamplesOf(result.SegmentPaths), Has.None.EqualTo(9000));
        }
    }

    /// <summary>
    /// When the laptop sleeps, the padding timer's first tick on waking finds the track far behind, asks the freeze watch
    /// for a look, and the sleep is cut from the clock before anything is padded: the file gets the margins at the edges,
    /// never the sleep itself, and the first packet on waking lands right after.
    /// </summary>
    [Test]
    public void A_sleep_is_cut_from_the_clock_and_not_padded()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var times = new FakeSystemTimes(_time);
        var watch = new FreezeWatch(times, _time, NullLogger<FreezeWatch>.Instance);
        watch.Frozen += (_, freeze) => _clock.Cut(freeze);
        using var session = CreateSession();
        session.SetFreezeLook(watch.CheckNow);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        watch.Tick();
        TickSeconds(1);
        watch.Tick();                                     // 11 s: the last tick before the sleep
        times.Sleep(TimeSpan.FromSeconds(18));            // 29 s: the padding timer fires once on waking
        var padded = session.GetStatus().Captured;
        capture.Deliver(Packet(9), Stamp(29));
        var result = session.Stop(Seconds(4.01));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.FromSeconds(15)), "from 12.5 s to 27.5 s: the sleep less the margins");
            Assert.That(padded, Is.EqualTo(Seconds(2.5)), "padded to a second and a half behind the clock, with the sleep cut out");
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(Seconds(4.01)));
            Assert.That(SamplesOf(result.SegmentPaths)[At(Seconds(4))..], Is.All.EqualTo(9));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>
    /// A capture thread can see a sleep before any tick does: its packet lands far ahead of the track, which asks the watch
    /// for a look, and the freeze is cut before the packet is placed again, after the margins and never after the sleep.
    /// </summary>
    [Test]
    public void A_packet_after_a_freeze_asks_for_a_look_and_lands_after_the_cut()
    {
        var jumping = new JumpingClock(_time);
        var clock = new RecordingClock(jumping);
        var watch = new FreezeWatch(new FakeSystemTimes(jumping), jumping, NullLogger<FreezeWatch>.Instance);
        watch.Frozen += (_, freeze) => clock.Cut(freeze);
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession(time: jumping, clock: clock);
        session.SetFreezeLook(watch.CheckNow);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        watch.Tick();                                     // the last tick before the freeze, at 10 s
        jumping.Jump(TimeSpan.FromSeconds(18));           // 28 s, and no timer has run
        capture.Deliver(Packet(9), Stamp(28));
        var result = session.Stop(Seconds(3.01));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.TimeCut, Is.EqualTo(TimeSpan.FromSeconds(15)), "from 11.5 s to 26.5 s");
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(Seconds(3.01)));
            Assert.That(SamplesOf(result.SegmentPaths)[At(Seconds(3))..], Is.All.EqualTo(9));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>A capture that fails is replaced into a new part, and the new capture's packets land at their clock positions across the parts.</summary>
    [Test]
    public async Task A_restart_keeps_the_track_on_the_clock_across_parts()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono, "Speakers"));
        using var session = CreateSession();

        session.Start(_files);
        first.Deliver(Packet(7), Stamp(10));
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono, "Speakers"));
        first.Fail(new IOException("The device went away."));
        await TickUntilAsync(() => second.Started);        // a few ticks, which the timer pads meanwhile
        second.Deliver(Packet(9), Stamp(13));
        var result = session.Stop(Seconds(3.01));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SegmentPaths, Has.Count.GreaterThanOrEqualTo(2), "the new capture starts a new part");
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(3.01))));
            Assert.That(samples[..480], Is.All.EqualTo(7));
            Assert.That(samples[480..At(Seconds(3))], Is.All.Zero);
            Assert.That(samples[At(Seconds(3))..], Is.All.EqualTo(9));
        }
    }

    /// <summary>
    /// A write that fails costs its packet, but not the track's place: the file is closed with what reached the disk, the
    /// next packet opens a new part, and it lands at its clock position across the two, so later marks stay right.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_failed_write_keeps_the_track_on_the_clock()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        using (FillDisk(_files.Segment(1)))
        {
            capture.Deliver(Packet(8), Stamp(10.5));      // lost
        }

        capture.Deliver(Packet(9), Stamp(11));
        var result = session.Stop(Seconds(1.01));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(1.01))));
            Assert.That(samples[..480], Is.All.EqualTo(7));
            Assert.That(samples[480..At(Seconds(1))], Is.All.Zero, "the lost packet's time is silence at its place");
            Assert.That(samples[At(Seconds(1))..], Is.All.EqualTo(9));
            Assert.That(_log.Problems, Has.Exactly(1).Contains("a moment of audio is lost"));
        }
    }

    /// <summary>
    /// A disk that keeps failing is given its pause, during which nothing is written, padding included; the first packet
    /// after it finds the track far behind, and with no freeze found, its gap is written in chunks before it, so that the
    /// pause stays as silence at its true place.
    /// </summary>
    [Test]
    public void A_disk_pause_stays_as_silence_at_its_place()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var classFolder = Path.Combine(_folder.Path, "class");
        var files = new RecordingFiles(classFolder, "Class_2026-10-17_19-00-00Z");
        using var session = CreateSession();

        session.Start(files);
        _clock.Begin(Stamp(10));                          // the other track's first packet
        Directory.Delete(classFolder);
        TickSeconds(3);                                   // 13 s: the padding fails at 12 s and again at 13 s, which starts the pause
        Directory.CreateDirectory(classFolder);
        TickSeconds(25);                                  // 38 s: inside the pause, nothing is written
        var duringPause = session.GetStatus().Captured;
        TickSeconds(7);                                   // 45 s: at 43 s the pause is over and the timer pads again
        capture.Deliver(Packet(9), Stamp(45));
        var result = session.Stop(Seconds(35.01));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(duringPause, Is.EqualTo(TimeSpan.Zero), "nothing is written while the disk is given its pause");
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(35.01))));
            Assert.That(samples[..At(Seconds(35))], Is.All.Zero);
            Assert.That(samples[At(Seconds(35))..], Is.All.EqualTo(9));
            Assert.That(_log.Problems, Has.Some.Contains("Trying again in 30 seconds"));
        }
    }

    /// <summary>
    /// V off releases the capture and opens no other, while the timer keeps the track on the clock in the same part; V on
    /// opens a capture again at once, and its packets land at their clock positions.
    /// </summary>
    [Test]
    public async Task V_releases_the_capture_and_the_track_keeps_its_place()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono, "Speakers"));
        using var session = CreateSession();

        session.Start(_files);
        first.Deliver(Packet(7), Stamp(10));
        session.SetEnabled(false);
        await Wait.UntilAsync(() => first.Disposed);
        var off = session.GetStatus();
        TickSeconds(2);                                   // 12 s: padded to 10.5 s
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono, "Speakers"));
        session.SetEnabled(true);
        await Wait.UntilAsync(() => second.Started);
        var on = session.GetStatus();
        second.Deliver(Packet(9), Stamp(12));
        var result = session.Stop(Seconds(2.01));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(off.Off, Is.True);
            Assert.That(off.WaitingFor, Is.Null, "nothing is waited for while switched off");
            Assert.That(off.SinceLastPacket, Is.Null);
            Assert.That(on.Off, Is.False);
            Assert.That(result.SegmentPaths, Has.Count.EqualTo(1), "the open part stays");
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(2.01))));
            Assert.That(samples[480..At(Seconds(2))], Is.All.Zero);
            Assert.That(samples[At(Seconds(2))..], Is.All.EqualTo(9));
            Assert.That(_provider.Created, Has.Count.EqualTo(2), "no capture was opened while off");
            Assert.That(_log.Messages, Has.Some.Contains("off (V)"));
            Assert.That(_log.Messages, Has.Some.Contains("on (V)"));
        }
    }

    /// <summary>
    /// A track switched off before the start, as the microphone is while the class app's mute cannot be seen, starts
    /// without a capture, says so, and waits for nothing; V on then opens its capture, whose packets land at their clock
    /// positions, in the one part, as the track had not been padded before.
    /// </summary>
    [Test]
    public async Task A_track_switched_off_before_the_start_starts_without_a_capture()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.SetEnabled(false);
        session.Start(_files);
        var off = session.GetStatus();
        var openedWhileOff = _provider.Created.Count;
        var saidAtTheStart = _log.Messages;
        _clock.Begin(Stamp(10));                          // the class track's first packet
        session.SetEnabled(true);
        await Wait.UntilAsync(() => capture.Started);
        capture.Deliver(Packet(9), Stamp(10.5));
        var result = session.Stop(Seconds(0.51));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(off.Off, Is.True);
            Assert.That(off.WaitingFor, Is.Null, "nothing is waited for while switched off");
            Assert.That(openedWhileOff, Is.Zero, "no capture is opened while off");
            Assert.That(saidAtTheStart, Has.Some.Contains("starts switched off"));
            Assert.That(saidAtTheStart, Has.None.Contains("(V)"), "nothing was switched: the state was set before the start");
            Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(0.51))));
            Assert.That(samples[..At(Seconds(0.5))], Is.All.Zero);
            Assert.That(samples[At(Seconds(0.5))..], Is.All.EqualTo(9));
        }
    }

    /// <summary>
    /// V before the start only sets the state the start reads: it opens nothing, says nothing, and the start then opens
    /// the one capture, so that no capture is ever opened and then forgotten by the start.
    /// </summary>
    [Test]
    public async Task V_before_the_start_only_sets_the_state()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.SetEnabled(false);
        session.SetEnabled(true);
        await Task.Delay(100);                            // time enough for a look queued by mistake
        var openedBeforeTheStart = _provider.Created.Count;
        session.Start(_files);
        session.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(openedBeforeTheStart, Is.Zero, "nothing is opened before the start");
            Assert.That(_provider.Created, Has.Count.EqualTo(1), "the start opened the one capture");
            Assert.That(capture.Started, Is.True);
            Assert.That(_log.Messages, Has.None.Contains("(V)"));
        }
    }

    /// <summary>Switching the microphone off while it is off, or on while it is on, changes nothing and opens nothing.</summary>
    [Test]
    public async Task Switching_to_the_state_already_held_changes_nothing()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        session.SetEnabled(true);
        await Task.Delay(100);
        session.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capture.Disposed, Is.True);
            Assert.That(_provider.Created, Has.Count.EqualTo(1));
            Assert.That(_log.Messages, Has.None.Contains("(V)"));
        }
    }

    /// <summary>The stop pads the track to the end position it is given, so that both tracks end together, and to the position now when it is given none.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public void The_stop_pads_the_track_to_the_end(bool endGiven)
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        TickSeconds(2);                                   // 12 s: padded to 10.5 s
        var result = endGiven ? session.Stop(TimeSpan.FromSeconds(3)) : session.Stop();

        Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(TimeSpan.FromSeconds(endGiven ? 3 : 2)));
    }

    /// <summary>A stop pads nothing when the track has already reached the end, and a track that never got a packet has no file to pad.</summary>
    [Test]
    public void A_stop_before_any_packet_leaves_no_file()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        TickSeconds(5);
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Is.Empty);
        Assert.That(Directory.GetFiles(_folder.Path), Is.Empty);
    }

    /// <summary>
    /// A track whose capture cannot be opened yet is still kept on the clock once the other track has set the origin: the
    /// timer pads it into a part of its own at 16 kHz, and when its capture opens at last, its audio goes to a new part at
    /// the capture's own rate, so that nothing is resampled and the positions carry across.
    /// </summary>
    [Test]
    public async Task A_track_without_a_capture_is_padded_at_16_kHz_into_a_part_of_its_own()
    {
        using var session = CreateSession(new RecordingOptions { Device = "Headset" });   // not connected yet

        session.Start(_files);
        _clock.Begin(Stamp(10));                          // the other track's first packet
        TickSeconds(3);                                   // 13 s: padded to 11.5 s
        var padded = session.GetStatus();
        var headset = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono, "Headset"));
        await TickUntilAsync(() => headset.Started);
        headset.Deliver(Packet(9), Stamp(16));
        var result = session.Stop(Seconds(6.01));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(padded.Captured, Is.EqualTo(Seconds(1.5)));
            Assert.That(padded.WaitingFor, Is.Not.Null);
            Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
            var first = TestAudio.ReadWav(result.SegmentPaths[0]);
            var second = TestAudio.ReadWav(result.SegmentPaths[1]);
            Assert.That(first.SampleRate, Is.EqualTo(16000));
            Assert.That(first.Samples, Is.All.Zero);
            Assert.That(second.SampleRate, Is.EqualTo(48000));
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(Seconds(6.01)));
            Assert.That(second.Samples[^480..], Is.All.EqualTo(9));
            Assert.That(result.HeardSound, Is.False, "9 is below the sound threshold");
        }
    }

    /// <summary>Without an origin nothing is padded: before the first packet of any track there is no file.</summary>
    [Test]
    public void Nothing_is_padded_before_the_first_packet_of_any_track()
    {
        using var session = CreateSession(new RecordingOptions { Device = "Headset" });

        session.Start(_files);
        TickSeconds(5);
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Is.Empty);
    }

    /// <summary>
    /// When a recording is continued, a track shorter on disk than the recording's length is padded up to that length by
    /// the first tick, before any packet, and the first packet then lands at the offset.
    /// </summary>
    [Test]
    public void Continuing_pads_a_shorter_track_up_to_the_offset()
    {
        _clock.SetOffset(TimeSpan.FromSeconds(10));
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files, TimeSpan.FromSeconds(8));
        TickSeconds(1);
        var padded = session.GetStatus().Captured;
        capture.Deliver(Packet(9), Stamp(11));
        var result = session.Stop(Seconds(10.01));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(padded, Is.EqualTo(TimeSpan.FromSeconds(2)), "this run wrote the two seconds the track was short of");
            Assert.That(LengthOf(result.SegmentPaths), Is.EqualTo(Seconds(2.01)));
            Assert.That(SamplesOf(result.SegmentPaths)[At(Seconds(2))..], Is.All.EqualTo(9), "the first packet lands at the offset");
        }
    }

    /// <summary>A packet whose stamp Windows flagged as wrong is placed by the time it arrived, and still lands within the wider tolerance.</summary>
    [Test]
    public void A_packet_without_a_stamp_is_placed_by_its_arrival()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        TickSeconds(2);                                   // 12 s: padded to 10.5 s
        capture.Deliver(Packet(9), Stamp(12), PacketFlags.TimestampError);   // taken to have ended now: 11.99 to 12.00
        var result = session.Stop(Seconds(2));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(2))));
            Assert.That(samples[At(Seconds(1.99))..], Is.All.EqualTo(9));
        }
    }

    /// <summary>The count of corrections for drift is in the result, for the end-of-class summary.</summary>
    [Test]
    public void The_result_counts_the_corrections_for_drift()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        capture.Deliver(Packet(7), Stamp(10.1));          // 90 ms ahead: padded
        capture.Deliver(Packet(7), Stamp(10.04));         // 70 ms behind: trimmed away
        var result = session.Stop();

        Assert.That(result.Corrections, Is.EqualTo(2));
    }

    /// <summary>
    /// A stretch with nothing playing is no drift: an output device's loopback hands over nothing meanwhile, the timer pads
    /// the track to a second and a half behind the clock, and the next packet lands well ahead of it. It is padded to its
    /// place all the same, but not counted, so the end-of-class line counts the drift and not the class's quiet stretches.
    /// </summary>
    [Test]
    public void A_stretch_with_nothing_playing_is_padded_but_not_counted_as_a_correction()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        TickSeconds(3);                                   // 13 s: padded to 11.5 s
        capture.Deliver(Packet(9), Stamp(12.99));         // 1.49 s ahead of the track: the class's sound came back
        var result = session.Stop(Seconds(3));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Corrections, Is.Zero);
            Assert.That(samples[At(Seconds(2.99))..At(Seconds(3))], Is.All.EqualTo(9), "placed where its stamp puts it all the same");
            Assert.That(samples[At(Seconds(1.5))..At(Seconds(2.99))], Is.All.Zero);
        }
    }

    /// <summary>
    /// A packet without a stamp that the gate held is placed by the time it arrived, as it would have been had the gate
    /// written it at once, with the wider tolerance of a packet without a stamp: the hold does not move it to where the
    /// gate's decision came, so it is appended where it was, and not counted as a correction.
    /// </summary>
    [Test]
    public void A_held_packet_without_a_stamp_is_placed_by_its_arrival()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        _time.Advance(TimeSpan.FromMilliseconds(110));    // 10.11 s: the track has reached 10.01
        gate.Answer(GateVerdict.Undecided);
        capture.Deliver(Packet(9), null, PacketFlags.TimestampError);   // taken to have ended now: 10.10 to 10.11, 90 ms ahead, within the wider tolerance
        _time.Advance(TimeSpan.FromMilliseconds(500));    // 10.61 s: the gate decides late
        gate.Answer(GateVerdict.Write);
        gate.RaiseChanged();
        var result = session.Stop(Seconds(0.02));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples[480..960], Is.All.EqualTo(9), "appended where it arrived, not padded to where the gate's decision came");
            Assert.That(result.Corrections, Is.Zero);
        }
    }

    /// <summary>With a gate, a packet the gate writes lands at its place as without one.</summary>
    [Test]
    public void A_packet_the_gate_writes_lands_at_its_place()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        capture.Deliver(Packet(9), Stamp(10.01));
        var result = session.Stop(Seconds(0.02));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.Judgements, Is.EqualTo(2));
            Assert.That(samples[..480], Is.All.EqualTo(7));
            Assert.That(samples[480..], Is.All.EqualTo(9));
        }
    }

    /// <summary>A packet the gate drops is not written; its time is silence at its place, so the positions of what follows do not move.</summary>
    [Test]
    public void A_packet_the_gate_drops_leaves_silence_at_its_place()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        gate.Answer(stamp => stamp == Stamp(10.5) ? GateVerdict.Drop : GateVerdict.Write);
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        capture.Deliver(Packet(8), Stamp(10.5));
        capture.Deliver(Packet(9), Stamp(11));
        var result = session.Stop(Seconds(1.01));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(1.01))));
            Assert.That(samples, Has.None.EqualTo(8));
            Assert.That(samples[At(Seconds(0.5))..At(Seconds(0.51))], Is.All.Zero);
            Assert.That(samples[At(Seconds(1))..], Is.All.EqualTo(9));
        }
    }

    /// <summary>A packet the gate cannot decide yet is held, and written as soon as the gate says it has changed and then writes it: the hold never outlasts the next look.</summary>
    [Test]
    public void A_held_packet_is_written_when_the_gate_decides_it()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        gate.Answer(GateVerdict.Undecided);
        capture.Deliver(Packet(9), Stamp(10.01));
        var held = session.GetStatus().Captured;
        gate.Answer(GateVerdict.Write);
        gate.RaiseChanged();
        var decided = session.GetStatus().Captured;
        var result = session.Stop(Seconds(0.02));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.EqualTo(Seconds(0.01)), "the second packet waits");
            Assert.That(decided, Is.EqualTo(Seconds(0.02)), "and is written as soon as the gate changes");
            Assert.That(SamplesOf(result.SegmentPaths)[480..], Is.All.EqualTo(9));
        }
    }

    /// <summary>
    /// Held packets are judged again in their order: a packet written after a dropped one finds the track short of its
    /// place and gets the dropped one's time as silence before it, as any gap wider than the tolerance does.
    /// </summary>
    [Test]
    public void Held_packets_are_judged_in_order()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        gate.Answer(GateVerdict.Undecided);
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7, 100), Stamp(10));
        capture.Deliver(Packet(8, 100), Stamp(10.1));
        capture.Deliver(Packet(9, 100), Stamp(10.2));
        gate.Answer(stamp => stamp == Stamp(10.1) ? GateVerdict.Drop : GateVerdict.Write);
        gate.RaiseChanged();
        var result = session.Stop(Seconds(0.3));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(0.3))));
            Assert.That(samples[..4800], Is.All.EqualTo(7));
            Assert.That(samples[4800..9600], Is.All.Zero, "the dropped packet's time is silence");
            Assert.That(samples[9600..], Is.All.EqualTo(9));
        }
    }

    /// <summary>
    /// A packet the gate never decides is dropped once it has waited longer than <see cref="RecordingSession.LongestHold"/>,
    /// whatever the gate does, so the queue stays small and the track is padded past it: a look that comes later finds
    /// nothing left to judge, and the stop counts the packet as dropped by the gate.
    /// </summary>
    [Test]
    public void A_packet_held_too_long_is_dropped()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        gate.Answer(GateVerdict.Undecided);
        capture.Deliver(Packet(9), Stamp(10.01));
        TickSeconds(4);                                   // 14 s: well past the longest hold
        var judgedByTheTicks = gate.Judgements;
        gate.Answer(GateVerdict.Write);
        gate.RaiseChanged();
        var result = session.Stop(Seconds(2.5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SamplesOf(result.SegmentPaths), Has.None.EqualTo(9));
            Assert.That(gate.Judgements, Is.EqualTo(judgedByTheTicks), "a packet dropped for waiting too long is judged no more");
            Assert.That(_log.Messages, Has.Some.Contains("The gate dropped 1 packet"));
        }
    }

    /// <summary>
    /// The held packets are judged again by the timer every second, so that a gate which changes its mind without saying
    /// so (as one does when it decides a packet by the time passed) never keeps a packet waiting beyond the next tick.
    /// </summary>
    [Test]
    public void The_timer_judges_the_held_packets_without_a_look()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        gate.Answer(GateVerdict.Undecided);
        capture.Deliver(Packet(9), Stamp(10.01));
        gate.Answer(GateVerdict.Write);                   // the gate changes its mind, but says nothing
        TickSeconds(1);                                   // 11 s
        var afterTheTick = session.GetStatus().Captured;
        var result = session.Stop(Seconds(0.02));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterTheTick, Is.EqualTo(Seconds(0.02)), "the tick judged the held packet and wrote it");
            Assert.That(SamplesOf(result.SegmentPaths)[480..], Is.All.EqualTo(9));
        }
    }

    /// <summary>A packet that arrives has the held ones judged before it, so that one the gate now writes keeps its place in front of the newcomer.</summary>
    [Test]
    public void A_new_packet_has_the_held_ones_judged_first()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        gate.Answer(GateVerdict.Undecided);
        capture.Deliver(Packet(8), Stamp(10.01));
        gate.Answer(GateVerdict.Write);                   // no look and no tick between
        capture.Deliver(Packet(9), Stamp(10.02));
        var written = session.GetStatus().Captured;
        var result = session.Stop(Seconds(0.03));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(Seconds(0.03)));
            Assert.That(samples[480..960], Is.All.EqualTo(8), "the held packet went first, at its place");
            Assert.That(samples[960..], Is.All.EqualTo(9));
        }
    }

    /// <summary>
    /// A new packet never overtakes one the gate still holds, even when a look lands between their judgements, as the
    /// watch's thread records its looks without the session's lock: the newcomer waits behind the held packet, and both are
    /// written in order when they are judged again. Judged on its own, the newcomer was written first, with silence before
    /// it, and the held packet then trimmed away behind it; that is how the four-hour soak lost stretches of the microphone
    /// and shifted it past a mute when the computer was busy.
    /// </summary>
    [Test]
    public void A_new_packet_never_overtakes_a_held_one_when_a_look_lands_between_their_judgements()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7, 100), Stamp(10));
        gate.Answer(GateVerdict.Undecided);
        capture.Deliver(Packet(8, 100), Stamp(10.1));
        gate.Answer(stamp =>
        {
            // The held packet is judged again as the newcomer arrives, still undecided; a look lands right after, on the
            // watch's thread, and decides every packet up to it.
            if (stamp == Stamp(10.1)) gate.Answer(GateVerdict.Write);
            return GateVerdict.Undecided;
        });
        capture.Deliver(Packet(9, 100), Stamp(10.2));
        gate.RaiseChanged();                              // the look's own call, once the capture thread has let go of the session
        var result = session.Stop(Seconds(0.3));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(0.3))));
            Assert.That(samples[..4800], Is.All.EqualTo(7));
            Assert.That(samples[4800..9600], Is.All.EqualTo(8), "the held packet is written at its place, not trimmed away behind the newcomer");
            Assert.That(samples[9600..], Is.All.EqualTo(9));
        }
    }

    /// <summary>
    /// The recording starts at the first packet of any track whether or not the gate writes it: a microphone packet the
    /// gate drops still sets the origin, so what the microphone heard before the class's first packet is not trimmed away
    /// once the class sets a later one, and the time until the first written packet is silence at its place.
    /// </summary>
    [Test]
    public void The_origin_is_set_by_the_first_packet_even_when_the_gate_drops_it()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        gate.Answer(stamp => stamp == Stamp(10) ? GateVerdict.Drop : GateVerdict.Write);
        using var session = CreateSession(track: Track.Microphone);
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        var origin = _clock.Origin;
        capture.Deliver(Packet(9), Stamp(10.5));
        var result = session.Stop(Seconds(0.51));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(origin, Is.EqualTo(Stamp(10)), "the dropped packet still started the recording");
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(0.51))));
            Assert.That(samples[..At(Seconds(0.5))], Is.All.Zero, "the half second before the written packet is silence, not left out");
            Assert.That(samples[At(Seconds(0.5))..], Is.All.EqualTo(9));
        }
    }

    /// <summary>Only a track on the clock can have a gate: setting one on a contiguous track is a wiring mistake, refused at once.</summary>
    [Test]
    public void Only_a_track_on_the_clock_can_have_a_gate()
    {
        var settings = TestOptions.Create(new RecordingOptions());
        var source = new LoopbackSource(_provider, new CaptureChooser(_time, new ClassSource.Automatic(), _chooserLog));
        using var contiguous = new RecordingSession(source, settings, _time, _log);

        Assert.That(() => contiguous.SetGate(new FakePacketGate()), Throws.InvalidOperationException);
    }

    /// <summary>Disposing the session lets go of the gate, so that a gate that lives on, as the track set's does, does not keep the session alive.</summary>
    [Test]
    public void Disposing_the_session_lets_go_of_the_gate()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        var session = CreateSession();
        session.SetGate(gate);
        var listenedTo = gate.HasListeners;

        session.Start(_files);
        session.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(listenedTo, Is.True, "the session listens for the gate's changes while it has the gate");
            Assert.That(gate.HasListeners, Is.False, "and lets go when it is disposed");
        }
    }

    /// <summary>At the stop, the held packets are judged once more: those the gate now writes are written, and those still undecided are dropped.</summary>
    [Test]
    public void The_stop_judges_the_held_packets_once_more()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(7), Stamp(10));
        gate.Answer(GateVerdict.Undecided);
        capture.Deliver(Packet(8), Stamp(10.01));
        capture.Deliver(Packet(9), Stamp(10.02));
        gate.Answer(stamp => stamp == Stamp(10.01) ? GateVerdict.Write : GateVerdict.Undecided);   // as a last look decides the earlier one
        var result = session.Stop(Seconds(0.03));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(0.03))));
            Assert.That(samples[480..960], Is.All.EqualTo(8));
            Assert.That(samples[960..], Is.All.Zero, "the packet still undecided at the stop is dropped");
        }
    }

    /// <summary>Taking the gate away writes what it still held, as a session without a gate writes at once.</summary>
    [Test]
    public void Taking_the_gate_away_writes_what_it_held()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new FakePacketGate();
        gate.Answer(GateVerdict.Undecided);
        using var session = CreateSession();
        session.SetGate(gate);

        session.Start(_files);
        capture.Deliver(Packet(9), Stamp(10));
        session.SetGate(null);
        var written = session.GetStatus().Captured;
        gate.RaiseChanged();                              // no longer listened to
        var result = session.Stop(Seconds(0.01));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(Seconds(0.01)));
            Assert.That(gate.Judgements, Is.EqualTo(1));
            Assert.That(SamplesOf(result.SegmentPaths), Is.All.EqualTo(9));
        }
    }

    /// <summary>
    /// With the microphone gate itself: a packet between two looks that found the class app listening is written, one
    /// followed by a look that found it not listening is dropped, and the gate's own change tells the session to judge.
    /// </summary>
    [Test]
    public void The_microphone_gate_decides_the_packets_by_the_looks_around_them()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm48KMono));
        var gate = new MicrophoneGate(ClassAppMute.Visible, new FollowRule.Named("chrome"), _time, NullLogger<MicrophoneGate>.Instance);
        using var session = CreateSession(track: Track.Microphone);
        session.SetGate(gate);

        session.Start(_files);
        _clock.Begin(Stamp(10));                          // the class track's first packet
        gate.Record(Look(10, AppListening.Yes));
        capture.Deliver(Packet(7), Stamp(10.1));
        var held = session.GetStatus().Captured;
        _time.Advance(TimeSpan.FromMilliseconds(250));
        gate.Record(Look(10.25, AppListening.Yes));
        var written = session.GetStatus().Captured;
        capture.Deliver(Packet(9), Stamp(10.3));
        _time.Advance(TimeSpan.FromMilliseconds(250));
        gate.Record(Look(10.5, AppListening.No));
        var result = session.Stop(Seconds(0.5));

        var samples = SamplesOf(result.SegmentPaths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.EqualTo(TimeSpan.Zero), "the packet waits for the look after it");
            Assert.That(written, Is.EqualTo(Seconds(0.11)), "written at 0.1 s once the look after it found chrome listening");
            Assert.That(samples[At(Seconds(0.1))..At(Seconds(0.11))], Is.All.EqualTo(7));
            Assert.That(samples, Has.None.EqualTo(9), "the packet before the look that found chrome not listening is dropped");
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(0.5))));
        }
    }

    /// <summary>A look of the watch at <paramref name="seconds"/> on the test clock that found the class app listening or not, the microphone not muted.</summary>
    private static WatchLook Look(double seconds, AppListening listening) =>
        new(Stamp(seconds), listening, MutedInWindows: false, listening == AppListening.Yes ? ["chrome"] : [], OtherMicrophone: null, Problem: null, Holds: true);
}
