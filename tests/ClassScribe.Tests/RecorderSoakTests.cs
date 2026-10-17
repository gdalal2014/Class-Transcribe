using System.Diagnostics;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// The accelerated soak of the microphone modes: a long class with your microphone, simulated on a clock moved by hand
/// through the real track set, its two sessions, the gate that follows the class app and the watch that feeds it, with
/// the devices, the microphones, the apps' microphone sessions and the computer's clocks played by fakes. The class is
/// a steady level with a tone at each mark; the microphone the same, on a device whose clock drifts. Through the run:
/// chrome lets go of the microphone for 30 seconds every 90 seconds in the first half (the gate closes and opens through
/// the real watch), V switches the microphone off for a while, the class moves to another output device, P pauses, the
/// recording is stopped and continued as its next part, and the laptop sleeps for 18 seconds. Afterwards: the tracks end
/// together at the clock's position, which left out the pause and the sleep; a tone at each mark is found at the mark's
/// position in its track and in the upload the real preparer mixes; nothing from the microphone is written while chrome
/// is not listening or V is off, and your voice is written while it is; a held packet is never held longer than the
/// design allows; the microphone's drift is corrected; and the heap, the handles and the threads are flat from quarter
/// to quarter. No microphone is opened, nothing is played and nothing leaves the process. Twelve simulated minutes run
/// with the suite; the four hours of the design run on request.
/// </summary>
[TestFixture]
public sealed class RecorderSoakTests
{
    private const int Rate = 16_000;
    private static readonly AudioFormat Pcm16KMono = new(Rate, 1, 16, false);
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);
    private static readonly Microphone TheArray = LaptopMicrophones.Array;

    /// <summary>One step of the simulation: the watch's interval, so that every step ends with a look.</summary>
    private static readonly TimeSpan Step = ClassMicrophoneWatch.Interval;

    /// <summary>A packet, as Windows hands them over.</summary>
    private static readonly TimeSpan PacketLength = TimeSpan.FromMilliseconds(10);

    /// <summary>The class's steady level, and the microphone's: what the files must hold where each was written.</summary>
    private const short ClassLevel = 300;
    private const short MicrophoneLevel = 500;

    /// <summary>The tone at a mark: 1 kHz for 50 ms at half scale, added to the track's level.</summary>
    private static readonly short[] MarkTone = TestAudio.Tone(Rate, seconds: 0.05, frequency: 1000, amplitude: 0.5);

    /// <summary>How long the laptop sleeps, and how much of it the clock cuts: the sleep less the freeze watch's margins.</summary>
    private static readonly TimeSpan Sleep = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan Cut = Sleep - 2 * FreezeWatch.Margin;

    /// <summary>The gate's edge: a look's interval plus the time a packet may be held.</summary>
    private static readonly TimeSpan Edge = TimeSpan.FromMilliseconds(300);

    /// <summary>How long after a change the microphone's track is surely being written again.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest a packet may wait for the look that decides it, as the design has it.</summary>
    private static readonly TimeSpan LongestHold = TimeSpan.FromMilliseconds(300);

    /// <summary>The class app's cycle in the first half: listening for a minute, then gone for 30 seconds.</summary>
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Listening = TimeSpan.FromSeconds(60);

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private FakeCaptureProvider _class = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private FakeMicrophoneUsageRecord _record = null!;
    private FakeProcessNames _names = null!;
    private ManualTimeProvider _time = null!;
    private FakeSystemTimes _times = null!;
    private FreezeWatch _freezes = null!;

    /// <summary>A temporary folder and a recording in it, the simulated devices, microphones, sessions and clocks, ten seconds in.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Kalam_2026-10-17_19-00-00Z");
        _class = new FakeCaptureProvider();
        _microphones = new FakeMicrophoneProvider().Connect(TheArray);
        _sessions = new FakeMicrophoneSessions();
        _record = new FakeMicrophoneUsageRecord();
        _names = new FakeProcessNames();
        _time = new ManualTimeProvider(Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _times = new FakeSystemTimes(_time);
        _freezes = new FreezeWatch(_times, _time, NullLogger<FreezeWatch>.Instance);
    }

    /// <summary>Deletes the temporary folder, which fails if the run left a file open.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// The soak itself, for <paramref name="hours"/> of simulated class, with the microphone's clock off by
    /// <paramref name="driftPartsPerMillion"/>: 500 for the short run, so that a correction comes every 100 seconds of
    /// open microphone and the run sees a few; the vendor's 100 for the four hours, a correction every eight minutes.
    /// </summary>
    [TestCase(0.2, 500, TestName = "Twelve_simulated_minutes_of_class_with_the_microphone")]
    [TestCase(4.0, 100, Explicit = true, Reason = "Four simulated hours write about a gigabyte of audio and take minutes; run it before a path moves onto the clock", TestName = "Four_simulated_hours_of_class_with_the_microphone")]
    public async Task A_long_class_with_the_microphone_stays_on_the_clock_follows_the_app_and_leaks_nothing(double hours, int driftPartsPerMillion)
    {
        var run = new Soak(this, TimeSpan.FromHours(hours), driftPartsPerMillion);
        await run.RunAsync();
        run.Check();
    }

    /// <summary>The track-set factory over the simulated devices and microphones, following chrome, whose mute Windows can see, so the microphone starts on.</summary>
    private TrackSetFactory Factory() =>
        new(_class, _microphones, _sessions, _record, _names, new FakeOwnProcesses(Laptop.OwnProcess), _freezes,
            TestOptions.Create(new RecordingOptions { MicrophoneFollows = "chrome", ClassAppMute = ClassAppMute.Visible }),
            TestOptions.Create(new TranscriptOptions()), _time, NullLoggerFactory.Instance);

    /// <summary>The sample index <paramref name="position"/> lands on.</summary>
    private static long At(TimeSpan position) => MediaTime.ToFrames(position, Rate);

    /// <summary>How long the files hold together, from what is on disk.</summary>
    private static TimeSpan LengthOf(IReadOnlyList<string> parts) => parts.Aggregate(TimeSpan.Zero, (total, part) => total + ClassFolders.MeasureSegment(part));

    /// <summary>What the process holds: the managed heap after a full collection, the handles and the threads.</summary>
    private static (long Heap, int Handles, int Threads) Resources()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return (GC.GetTotalMemory(forceFullCollection: true), process.HandleCount, process.Threads.Count);
    }

    /// <summary>A stretch of positions on the clock, from one to the other.</summary>
    private sealed record Stretch(TimeSpan From, TimeSpan To);

    /// <summary>One run of the soak: the schedule, the simulation, what it noted on the way, and the checks on what it left on disk.</summary>
    private sealed class Soak(RecorderSoakTests fixture, TimeSpan length, int driftPartsPerMillion)
    {
        /// <summary>A mark: where the clock stood when the tone began, and on which track.</summary>
        private sealed record Mark(TimeSpan Position, Track Track);

        /// <summary>A change of a state at a position on the clock.</summary>
        private sealed record Change(TimeSpan Position, bool On);

        private readonly TimeSpan _microphonePacket = PacketLength * (1 + driftPartsPerMillion / 1_000_000.0);
        private readonly List<Change> _listening = [];
        private readonly List<Change> _microphoneOn = [];
        private readonly List<TimeSpan> _freezeTicks = [];
        private readonly List<TimeSpan> _continues = [];
        private readonly List<TimeSpan> _switches = [];
        private readonly List<Mark> _marks = [];
        private readonly List<TimeSpan> _markTimes = [];
        private readonly List<string> _holdViolations = [];
        private readonly List<(long Heap, int Handles, int Threads)> _quarters = [];
        private readonly List<string> _classParts = [];
        private readonly List<string> _microphoneParts = [];
        private readonly SortedList<TimeSpan, Action> _events = [];

        private TrackSet _recording = null!;
        private RecordingClock _clock = null!;
        private FakeAudioCapture? _classCapture;
        private FakeAudioCapture? _microphoneCapture;
        private FakeAudioCapture? _awaitedCapture;
        private long _origin;
        private long _classNext;
        private long _microphoneNext;
        private long _pauseStart;
        private TimeSpan _microphoneOffset;
        private TimeSpan _lastEvent = TimeSpan.MinValue;
        private TimeSpan _pauseLength;
        private TimeSpan _elapsed;
        private TimeSpan _finalPosition;
        private Track? _pendingMark;
        private int _toneLeft;
        private Track _toneTrack;
        private bool _chromeListening = true;
        private bool _paused;

        /// <summary>Now on the test's clock, in the units packets are stamped in.</summary>
        private long Now => fixture._time.GetTimestamp();

        /// <summary>How far the simulated class has come, by the test's clock.</summary>
        private TimeSpan Elapsed => TimeSpan.FromTicks(Now - _origin);

        /// <summary>The simulation: the schedule laid out, then the run step by step from the start to the end, through a continue.</summary>
        public async Task RunAsync()
        {
            Schedule();
            await StartRecordingAsync(offset: TimeSpan.Zero);
            _origin = Now;
            var ticks = 0;
            while (Elapsed < length)
            {
                fixture._time.Advance(Step);
                if (++ticks % 4 == 0) fixture._freezes.Tick();
                DeliverPackets();
                _recording.Watch!.LookNow();
                CheckHold();
                await RunDueEventsAsync();
                if (_awaitedCapture is { } awaited)
                {
                    if (awaited.Started) ClassMovedTo(awaited);
                    else await Task.Delay(2);
                }
            }

            _elapsed = Elapsed;
            _quarters.Add(Resources());
            _finalPosition = _recording.Position;
            var summary = _recording.Stop();
            _recording.Dispose();
            _classParts.AddRange(summary.MainSegments);
            _microphoneParts.AddRange(summary.MicrophoneSegments);
        }

        /// <summary>What the run must have left on disk, and what it must have kept flat.</summary>
        public void Check()
        {
            var classLength = LengthOf(_classParts);
            var microphoneLength = LengthOf(_microphoneParts);
            using var classSamples = new JoinedSamples(_classParts);
            using var microphoneSamples = new JoinedSamples(_microphoneParts);
            var upload = fixture._folder.File("upload.wav");
            new AudioPreparer(NullLogger<AudioPreparer>.Instance).Prepare(new UploadTracks(_classParts, _microphoneParts), upload, CancellationToken.None);
            using var uploadSamples = new JoinedSamples([upload]);
            TestContext.Out.WriteLine($"Simulated {_elapsed} with {_pauseLength} paused: the class in {_classParts.Count} parts ({classLength}), the microphone in {_microphoneParts.Count} ({microphoneLength}), " +
                $"{_marks.Count} marks, the upload {MediaTime.FromFrames(uploadSamples.Length, Rate)}; heap, handles and threads per quarter: {string.Join(", ", _quarters.Select(quarter => $"{quarter.Heap / 1024 / 1024} MB / {quarter.Handles} / {quarter.Threads}"))}");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_holdViolations, Is.Empty, "a packet was held longer than the design allows");
                Assert.That(_finalPosition, Is.EqualTo(_elapsed - _pauseLength - Cut).Within(TimeSpan.FromMilliseconds(50)), "the clock left out the pause and the sleep");
                Assert.That(classLength, Is.EqualTo(_finalPosition).Within(TimeSpan.FromMilliseconds(50)), "the class track ends at the stop's position");
                Assert.That((classLength - microphoneLength).Duration(), Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(50)), "the tracks end together");
                Assert.That(_classParts, Has.Count.GreaterThanOrEqualTo(3), "a part per run, and one more for the device the class moved to");
                Assert.That(_microphoneParts, Has.Count.EqualTo(2), "a part per run: V keeps the open part, and the padding keeps the track on the clock");
                Assert.That(_marks, Has.Count.EqualTo(10), "every mark was set, outside the pause, the sleep and V off");
            }

            CheckMarks(classSamples, microphoneSamples, uploadSamples);
            CheckStretches(classSamples, microphoneSamples);
            CheckResources();
        }

        /// <summary>
        /// The schedule, as fractions of the run: the class app's cycle through the first half; V off for a 24th of the run
        /// at a fifth of it; the class moving to another device at three tenths; a pause of a 16th of the run at four
        /// tenths; the continue at the half; the sleep at six tenths; ten marks spread over the run, on the class and the
        /// microphone in turn, each clear of everything above and, in the first half, in the middle of a stretch where
        /// chrome listens; and the resources measured each quarter.
        /// </summary>
        private void Schedule()
        {
            for (var cycle = TimeSpan.Zero; cycle + Cycle <= length / 2; cycle += Cycle)
            {
                Add(cycle + Listening, () => ChromeListens(false));
                Add(cycle + Cycle, () => ChromeListens(true));
            }

            var microphoneOff = length / 5;
            var microphoneOn = microphoneOff + length / 24;
            var move = length * 0.3;
            var pause = length * 0.4;
            var resume = pause + length / 16;
            var half = length / 2;
            var sleep = length * 0.6;
            Add(microphoneOff, () => SwitchMicrophone(false));
            Add(microphoneOn, () => SwitchMicrophone(true));
            Add(move, MoveTheClass);
            Add(pause, () => Pause(true));
            Add(resume, () => Pause(false));
            Add(half, () => ContinueAsync().GetAwaiter().GetResult());
            Add(sleep, SleepTheLaptop);
            Add(length / 4, () => _quarters.Add(Resources()));
            Add(half - Step, () => _quarters.Add(Resources()));
            Add(length * 0.75, () => _quarters.Add(Resources()));

            var two = TimeSpan.FromSeconds(2);
            Stretch[] busy =
            [
                new(microphoneOff - two, microphoneOn + two),
                new(move - TimeSpan.FromSeconds(1), move + TimeSpan.FromSeconds(20)),
                new(pause - two, resume + two),
                new(half - TimeSpan.FromSeconds(5), half + TimeSpan.FromSeconds(5)),
                new(sleep - TimeSpan.FromSeconds(3), sleep + TimeSpan.FromSeconds(25))
            ];
            var track = Track.Main;
            foreach (var fraction in new[] { 0.10, 0.15, 0.26, 0.35, 0.48, 0.55, 0.66, 0.75, 0.85, 0.95 })
            {
                var at = MarkTime(length * fraction, busy);
                var marked = track;
                _markTimes.Add(at);
                Add(at, () => _pendingMark = marked);
                track = track == Track.Main ? Track.Microphone : Track.Main;
            }
        }

        /// <summary>Adds <paramref name="action"/> at <paramref name="at"/>, a step later when another is already there.</summary>
        private void Add(TimeSpan at, Action action)
        {
            while (_events.ContainsKey(at)) at += Step;
            _events.Add(at, action);
        }

        /// <summary>
        /// A time for a mark wanted at <paramref name="wanted"/>: in the first half, 30 seconds into the stretch where chrome
        /// listens, so the gate is well open; moved on by a cycle, or by ten seconds in the second half, while it lands in
        /// a <paramref name="busy"/> stretch or within two seconds of another mark.
        /// </summary>
        private TimeSpan MarkTime(TimeSpan wanted, Stretch[] busy)
        {
            var at = wanted < length / 2 ? MidListening(wanted) : wanted;
            while (busy.Any(stretch => at >= stretch.From && at <= stretch.To) || _markTimes.Any(other => (other - at).Duration() < TimeSpan.FromSeconds(2)))
                at = at < length / 2 ? MidListening(at + Cycle) : at + TimeSpan.FromSeconds(10);
            return at;
        }

        /// <summary>The moment at or after <paramref name="at"/> that falls 30 seconds into a stretch where chrome listens.</summary>
        private static TimeSpan MidListening(TimeSpan at)
        {
            var phase = TimeSpan.FromTicks(at.Ticks % Cycle.Ticks);
            var target = Listening / 2;
            return at + (phase <= target ? target - phase : Cycle - phase + target);
        }

        /// <summary>Runs every event due by now, in order.</summary>
        private async Task RunDueEventsAsync()
        {
            while (_events.Count > 0 && _events.Keys[0] <= Elapsed)
            {
                var action = _events.Values[0];
                _events.RemoveAt(0);
                _lastEvent = Elapsed;
                action();
                await Task.Yield();
            }
        }

        /// <summary>Makes the recording on a clock of its own, queues a capture for the class's device and one for the microphone, starts it at <paramref name="offset"/> and waits for both captures to start.</summary>
        private async Task StartRecordingAsync(TimeSpan offset)
        {
            _clock = new RecordingClock(fixture._time);
            _recording = (TrackSet)fixture.Factory().Create(
                new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null, new FollowRule.Named("chrome"))), _clock);
            var moved = _switches.Count > 0;
            var theClass = fixture._class.Enqueue(new FakeAudioCapture(Pcm16KMono, moved ? "Headphones" : "Speakers"), level: moved ? 0.5f : 0f);
            var microphone = MicrophoneCapture();
            SetChromeSessions();
            _recording.Start(fixture._files, offset);
            await Wait.UntilAsync(() => theClass.Started && microphone.Started, 20);
            _classCapture = theClass;
            _microphoneCapture = microphone;
            _classNext = Now;
            _microphoneNext = Now;
        }

        /// <summary>Queues the next capture of the microphone, 16 kHz mono, which hands over audio all the time as a microphone does.</summary>
        private FakeAudioCapture MicrophoneCapture() =>
            fixture._microphones.Prepare(new FakeAudioCapture(Pcm16KMono, TheArray.Name, TheArray.Id) { DeliversContinuously = true });

        /// <summary>
        /// Hands over every packet stamped up to now: the class's on an exact 10 ms grid, the microphone's on its own
        /// drifting grid while it is on. A mark's tone begins with the next packet of its track, and the clock's position
        /// of that packet's stamp is the mark.
        /// </summary>
        private void DeliverPackets()
        {
            var now = Now;
            if (_classCapture is { Started: true, Disposed: false } theClass)
            {
                while (_classNext + PacketLength.Ticks <= now)
                {
                    theClass.Deliver(Packet(Track.Main, _classNext), _classNext);
                    _classNext += PacketLength.Ticks;
                }
            }

            if (_microphoneCapture is { Started: true, Disposed: false } microphone)
            {
                while (_microphoneNext + _microphonePacket.Ticks <= now)
                {
                    microphone.Deliver(Packet(Track.Microphone, _microphoneNext), _microphoneNext);
                    _microphoneNext += _microphonePacket.Ticks;
                }
            }
        }

        /// <summary>A packet of <paramref name="track"/> stamped at <paramref name="stamp"/>: its level, with the mark's tone on top where one is due.</summary>
        private byte[] Packet(Track track, long stamp)
        {
            var level = track == Track.Main ? ClassLevel : MicrophoneLevel;
            var samples = new short[(int)At(PacketLength)];
            System.Array.Fill(samples, level);
            if (_pendingMark == track)
            {
                _pendingMark = null;
                _toneLeft = MarkTone.Length;
                _toneTrack = track;
                _marks.Add(new Mark(_clock.Position(stamp), track));
            }

            if (_toneLeft > 0 && _toneTrack == track)
            {
                var from = MarkTone.Length - _toneLeft;
                var count = Math.Min(samples.Length, _toneLeft);
                for (var index = 0; index < count; index++) samples[index] = (short)(level + MarkTone[from + index]);
                _toneLeft -= count;
            }

            return TestAudio.Shorts(samples);
        }

        /// <summary>Chrome's session on the array as the run has it now: active while chrome listens, gone otherwise.</summary>
        private void SetChromeSessions()
        {
            if (_chromeListening) fixture._sessions.SetSessions(TheArray.Id, Laptop.Session(TheArray.Id, Laptop.ChromeUtility));
            else fixture._sessions.SetSessions(TheArray.Id);
        }

        /// <summary>Chrome listens, or lets go of the microphone: noted at the clock's position, then set for the watch's next look.</summary>
        private void ChromeListens(bool listening)
        {
            _chromeListening = listening;
            _listening.Add(new Change(_clock.Now(), listening));
            SetChromeSessions();
        }

        /// <summary>V: off releases the capture, which the packets stop for; on queues a new capture, switches on and waits for it.</summary>
        private void SwitchMicrophone(bool on)
        {
            _microphoneOn.Add(new Change(_clock.Now(), on));
            if (!on)
            {
                var released = _microphoneCapture!;
                _recording.SetMicrophone(false);
                Wait.UntilAsync(() => released.Disposed, 20).GetAwaiter().GetResult();
                _microphoneCapture = null;
                return;
            }

            var microphone = MicrophoneCapture();
            _recording.SetMicrophone(true);
            Wait.UntilAsync(() => microphone.Started, 20).GetAwaiter().GetResult();
            _microphoneCapture = microphone;
            _microphoneNext = Now;
        }

        /// <summary>The class goes quiet on the speakers and plays on the headphones, which become the default: the recording moves there within a few seconds.</summary>
        private void MoveTheClass()
        {
            _switches.Add(_clock.Now());
            _classCapture = null;
            fixture._class.SetLevel("Speakers", 0);
            _awaitedCapture = fixture._class.SwitchDefaultDevice(new FakeAudioCapture(Pcm16KMono, "Headphones"), level: 0.5f);
        }

        /// <summary>The class is recorded from <paramref name="capture"/> from now on, its packets on the grid from now.</summary>
        private void ClassMovedTo(FakeAudioCapture capture)
        {
            _awaitedCapture = null;
            _classCapture = capture;
            _classNext = Now;
            _lastEvent = Elapsed;
        }

        /// <summary>P: pauses or resumes, counting the time paused.</summary>
        private void Pause(bool on)
        {
            _paused = on;
            if (on)
            {
                _pauseStart = Now;
                _recording.Pause();
            }
            else
            {
                _pauseLength += TimeSpan.FromTicks(Now - _pauseStart);
                _recording.Resume();
            }
        }

        /// <summary>Stops the recording, as Q twice does, and continues it as its next part from its length on disk, each track from its own.</summary>
        private async Task ContinueAsync()
        {
            var summary = _recording.Stop();
            _recording.Dispose();
            _classParts.AddRange(summary.MainSegments);
            _microphoneParts.AddRange(summary.MicrophoneSegments);
            var classLength = LengthOf(fixture._files.FindSegments(Track.Main));
            var microphoneLength = LengthOf(fixture._files.FindSegments(Track.Microphone));
            var offset = classLength > microphoneLength ? classLength : microphoneLength;
            _microphoneOffset = microphoneLength;
            _continues.Add(offset);
            await StartRecordingAsync(offset);
        }

        /// <summary>The laptop sleeps for 18 seconds right after a tick of the freeze watch: the packets start again on waking, and the clock cuts the sleep less the margins.</summary>
        private void SleepTheLaptop()
        {
            fixture._freezes.Tick();
            _freezeTicks.Add(_clock.Now());
            fixture._times.Sleep(Sleep);
            _classNext = Now;
            _microphoneNext = Now;

            // The first look after waking is 18 seconds after the last, so the gate drops the first step's packets by its gap
            // rule, which is no hold: the hold check waits for the looks to be a quarter second apart again.
            _lastEvent = Elapsed;
        }

        /// <summary>
        /// After a look, while the microphone is surely being written (chrome listening and V on, not paused, the class not
        /// moving, nothing changed for two seconds), the track must have caught up with the last packet to within the longest
        /// hold, plus the 50 ms a track may differ from the clock.
        /// </summary>
        private void CheckHold()
        {
            if (!_chromeListening || _microphoneCapture is null || _paused || _awaitedCapture is not null) return;
            if (_lastEvent != TimeSpan.MinValue && Elapsed - _lastEvent < TimeSpan.FromSeconds(2)) return;

            var written = _microphoneOffset + (_recording.GetSnapshot().Microphone?.Written ?? TimeSpan.Zero);
            var lastPacketEnd = _clock.Position(_microphoneNext - _microphonePacket.Ticks) + PacketLength;
            if (written < lastPacketEnd - LongestHold - TimeSpan.FromMilliseconds(50))
                _holdViolations.Add($"at {_clock.Now()}: written {written}, the last packet ended at {lastPacketEnd}");
        }

        /// <summary>Every mark's tone is found at the mark's position: within 1 ms on the class, whose device keeps the clock; within the placer's 50 ms on the drifting microphone; and in the upload the same.</summary>
        private void CheckMarks(JoinedSamples theClass, JoinedSamples microphone, JoinedSamples upload)
        {
            using (Assert.EnterMultipleScope())
            {
                foreach (var mark in _marks)
                {
                    var tolerance = mark.Track == Track.Main ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromMilliseconds(51);
                    var track = mark.Track == Track.Main ? theClass : microphone;
                    var level = mark.Track == Track.Main ? ClassLevel : MicrophoneLevel;
                    var inTrack = ToneStart(track, mark.Position, tolerance);
                    var inUpload = ToneStart(upload, mark.Position, tolerance);
                    Assert.That(inTrack, Is.Not.Null, $"the {mark.Track} mark at {mark.Position} is not in its track within {tolerance.TotalMilliseconds} ms");
                    Assert.That(inUpload, Is.Not.Null, $"the {mark.Track} mark at {mark.Position} is not in the upload within {tolerance.TotalMilliseconds} ms");
                    if (inTrack is { } found) Assert.That(track.Read(found - 1, 1)[0], Is.EqualTo(level), $"the level before the {mark.Track} mark at {mark.Position}");
                }
            }
        }

        /// <summary>The sample index where a tone begins within <paramref name="tolerance"/> of <paramref name="position"/>, or null when none does: the first jump of more than 3000 between neighbours, less one.</summary>
        private static long? ToneStart(JoinedSamples samples, TimeSpan position, TimeSpan tolerance)
        {
            var first = Math.Max(0, At(position - tolerance) - 2);
            var window = samples.Read(first, (int)(At(2 * tolerance) + 6));
            for (var index = 1; index < window.Length; index++)
            {
                if (Math.Abs(window[index] - window[index - 1]) > 3000) return first + index - 1;
            }

            return null;
        }

        /// <summary>
        /// Where the run says the microphone was surely written, the files hold its level (99 percent of the samples: the
        /// rest are the drift corrections' silences and the tones); where it says the microphone was surely not written,
        /// the files hold exact zeros; the class holds its level wherever it was surely written; and the drift was
        /// corrected in the second half, which shows as stretches of silence of a correction's length and no longer.
        /// </summary>
        private void CheckStretches(JoinedSamples theClass, JoinedSamples microphone)
        {
            var classLength = MediaTime.FromFrames(theClass.Length, Rate);
            var microphoneLength = MediaTime.FromFrames(microphone.Length, Rate);
            var settled = Subtract([new Stretch(TimeSpan.Zero, microphoneLength)], TrackInterruptions());
            var written = Subtract(Intersect(
                Trim(Where(_listening, on: true, microphoneLength), Settled, Settled),
                Trim(Where(_microphoneOn, on: true, microphoneLength), Settled, Settled)), TrackInterruptions());
            var zero = Trim(Where(_listening, on: false, microphoneLength), TimeSpan.FromMilliseconds(10), Edge)
                .Concat(Trim(Where(_microphoneOn, on: false, microphoneLength), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)))
                .ToList();
            var classWritten = Subtract(Subtract([new Stretch(TimeSpan.Zero, classLength)], TrackInterruptions()),
                [.. _switches.Select(moved => new Stretch(moved - TimeSpan.FromMilliseconds(500), moved + TimeSpan.FromSeconds(20)))]);
            var secondHalf = _continues.Count > 0 ? _continues[0] + TimeSpan.FromSeconds(5) : microphoneLength;

            var (microphoneSamples, microphoneAtLevel) = Count(microphone, written, sample => sample != 0);
            var (classSamples, classAtLevel) = Count(theClass, classWritten, sample => sample != 0);
            var (zeroSamples, zeroViolations) = Count(microphone, zero, sample => sample != 0);
            var zeroRuns = ZeroRuns(microphone, [.. written.Where(stretch => stretch.From >= secondHalf)]);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(settled, Is.Not.Empty);
                Assert.That(zeroViolations, Is.Zero, $"{zeroViolations} of {zeroSamples} samples hold something from the microphone while chrome was not listening or V was off");
                Assert.That(microphoneSamples, Is.GreaterThan(At(length / 4)), "most of the run has the microphone written");
                Assert.That((double)microphoneAtLevel / microphoneSamples, Is.GreaterThanOrEqualTo(0.99), "your voice is written while chrome listens and V is on");
                Assert.That((double)classAtLevel / classSamples, Is.GreaterThanOrEqualTo(0.99), "the class is written throughout");
                Assert.That(zeroRuns, Is.Not.Empty, "the microphone's drift was corrected with a stretch of silence in the second half");
                Assert.That(zeroRuns, Has.All.InRange(At(TimeSpan.FromMilliseconds(45)), At(TimeSpan.FromMilliseconds(120))), "each correction is one tolerance's worth of silence");
            }
        }

        /// <summary>The heap, the handles and the threads at the last quarter are where they were at the first.</summary>
        private void CheckResources()
        {
            Assert.That(_quarters, Has.Count.EqualTo(4));
            var first = _quarters[0];
            var last = _quarters[^1];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(last.Heap - first.Heap, Is.LessThan(24L * 1024 * 1024), $"the heap grew from {first.Heap} to {last.Heap} bytes");
                Assert.That(last.Handles - first.Handles, Is.LessThan(64), $"the handles grew from {first.Handles} to {last.Handles}");
                Assert.That(last.Threads - first.Threads, Is.LessThan(8), $"the threads grew from {first.Threads} to {last.Threads}");
            }
        }

        /// <summary>The stretches where neither track was surely written: around the sleep, and the start of a continued run.</summary>
        private List<Stretch> TrackInterruptions() =>
        [
            .. _freezeTicks.Select(tick => new Stretch(tick - TimeSpan.FromSeconds(1.5), tick + TimeSpan.FromSeconds(5))),
            .. _continues.Select(offset => new Stretch(offset - TimeSpan.FromMilliseconds(100), offset + TimeSpan.FromSeconds(3)))
        ];

        /// <summary>Counts the samples of <paramref name="stretches"/> in <paramref name="samples"/>, and those among them that <paramref name="test"/> accepts.</summary>
        private static (long Total, long Accepted) Count(JoinedSamples samples, List<Stretch> stretches, Func<short, bool> test)
        {
            long total = 0, accepted = 0;
            foreach (var stretch in stretches)
            {
                var from = Math.Max(0, At(stretch.From));
                var to = Math.Min(samples.Length, At(stretch.To));
                for (var index = from; index < to; index += 16_000)
                {
                    var block = samples.Read(index, (int)Math.Min(16_000, to - index));
                    total += block.Length;
                    accepted += block.Count(test);
                }
            }

            return (total, accepted);
        }

        /// <summary>The lengths of the runs of exact zeros, of at least 20 ms, inside <paramref name="stretches"/> of <paramref name="samples"/>.</summary>
        private static List<long> ZeroRuns(JoinedSamples samples, List<Stretch> stretches)
        {
            var runs = new List<long>();
            foreach (var stretch in stretches)
            {
                long run = 0;
                var from = Math.Max(0, At(stretch.From));
                var to = Math.Min(samples.Length, At(stretch.To));
                for (var index = from; index < to; index += 16_000)
                {
                    foreach (var sample in samples.Read(index, (int)Math.Min(16_000, to - index)))
                    {
                        if (sample == 0)
                        {
                            run++;
                            continue;
                        }

                        if (run >= At(TimeSpan.FromMilliseconds(20))) runs.Add(run);
                        run = 0;
                    }
                }

                if (run >= At(TimeSpan.FromMilliseconds(20))) runs.Add(run);
            }

            return runs;
        }

        /// <summary>The stretches up to <paramref name="total"/> where <paramref name="changes"/> say <paramref name="on"/>; on before the first change.</summary>
        private static List<Stretch> Where(List<Change> changes, bool on, TimeSpan total)
        {
            var stretches = new List<Stretch>();
            var state = true;
            var from = TimeSpan.Zero;
            foreach (var change in changes.OrderBy(change => change.Position))
            {
                if (change.On == state) continue;
                if (state == on) stretches.Add(new Stretch(from, change.Position));
                state = change.On;
                from = change.Position;
            }

            if (state == on) stretches.Add(new Stretch(from, total));
            return stretches;
        }

        /// <summary>Each stretch shortened by <paramref name="atStart"/> and <paramref name="atEnd"/>; those with nothing left are dropped.</summary>
        private static List<Stretch> Trim(List<Stretch> stretches, TimeSpan atStart, TimeSpan atEnd) =>
            [.. stretches.Select(stretch => new Stretch(stretch.From + atStart, stretch.To - atEnd)).Where(stretch => stretch.To > stretch.From)];

        /// <summary>The stretches inside both <paramref name="first"/> and <paramref name="second"/>.</summary>
        private static List<Stretch> Intersect(List<Stretch> first, List<Stretch> second) =>
        [
            .. first.SelectMany(one => second.Select(other => new Stretch(one.From > other.From ? one.From : other.From, one.To < other.To ? one.To : other.To)))
                .Where(stretch => stretch.To > stretch.From)
        ];

        /// <summary>The stretches with <paramref name="holes"/> cut out of them.</summary>
        private static List<Stretch> Subtract(List<Stretch> stretches, List<Stretch> holes)
        {
            var result = stretches;
            foreach (var hole in holes)
            {
                result =
                [
                    .. result.SelectMany(stretch => new[] { new Stretch(stretch.From, hole.From < stretch.To ? hole.From : stretch.To), new Stretch(hole.To > stretch.From ? hole.To : stretch.From, stretch.To) })
                        .Where(stretch => stretch.To > stretch.From)
                ];
            }

            return result;
        }
    }

    /// <summary>The 16-bit mono samples of several files read as one stream, by position, without holding them all in memory.</summary>
    private sealed class JoinedSamples : IDisposable
    {
        private readonly List<(WaveFileReader Reader, long First, long Count)> _parts = [];

        /// <summary>Opens <paramref name="paths"/> in order.</summary>
        public JoinedSamples(IReadOnlyList<string> paths)
        {
            long first = 0;
            foreach (var path in paths)
            {
                var reader = new WaveFileReader(path);
                var count = reader.Length / 2;
                _parts.Add((reader, first, count));
                first += count;
            }

            Length = first;
        }

        /// <summary>How many samples the files hold together.</summary>
        public long Length { get; }

        /// <summary>The samples from <paramref name="start"/>, <paramref name="count"/> of them or as many as there are.</summary>
        public short[] Read(long start, int count)
        {
            var result = new List<short>(Math.Max(0, count));
            var position = Math.Max(0, start);
            while (result.Count < count && position < Length)
            {
                var (reader, first, partCount) = _parts.First(part => position >= part.First && position < part.First + part.Count);
                var inPart = position - first;
                var take = (int)Math.Min(count - result.Count, partCount - inPart);
                var bytes = new byte[take * 2];
                reader.Position = inPart * 2;
                var read = reader.Read(bytes, 0, bytes.Length);
                for (var index = 0; index + 1 < read; index += 2) result.Add(BitConverter.ToInt16(bytes, index));
                position += take;
            }

            return [.. result];
        }

        /// <summary>Closes the files.</summary>
        public void Dispose()
        {
            foreach (var (reader, _, _) in _parts) reader.Dispose();
        }
    }
}
