using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassScribe.Tests;

/// <summary>
/// A recording of the class alone, as the record command and the recording loop drive it: one session recording what a
/// run's source plan names, made by the factory from that plan, and seen through a snapshot at each check. The devices
/// are simulated, and the clock is moved by hand.
/// </summary>
[TestFixture]
public class ClassRecordingTests
{
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);

    private TempFolder _folder = null!;
    private TestRuntime _runtime = null!;
    private FakeCaptureProvider _provider = null!;
    private ManualTimeProvider _time = null!;
    private RecordingLogger<RecordingSession> _sessionLog = null!;
    private RecordingLogger<CaptureChooser> _chooserLog = null!;
    private RecordingLogger<ClassRecording> _log = null!;
    private RecordingFiles _files = null!;

    /// <summary>Creates a temporary folder, a recording in it, ClassScribe's own folder in another, the simulated devices, a clock moved by hand and logs to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _runtime = new TestRuntime();
        _files = new RecordingFiles(_folder.Path, "Kalam_2026-10-15_23-00-02Z");
        _provider = new FakeCaptureProvider();
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero));
        _sessionLog = new RecordingLogger<RecordingSession>();
        _chooserLog = new RecordingLogger<CaptureChooser>();
        _log = new RecordingLogger<ClassRecording>();
    }

    /// <summary>Deletes the temporary folders. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown()
    {
        _runtime.Dispose();
        _folder.Dispose();
    }

    /// <summary>The factory under test: recordings of the simulated devices, on the test's clock, with the default settings.</summary>
    private ClassRecordingFactory Factory() =>
        new(_provider, TestOptions.Create(new RecordingOptions()), _time, _sessionLog, _chooserLog, _log);

    /// <summary>A plan for the class alone, from <paramref name="source"/>.</summary>
    private static SourcePlan ClassFrom(ClassSource source) => new(RecordingMode.Class, source, Microphone: null);

    /// <summary>A recording of the device the class plays on, not started yet.</summary>
    private IRecording Automatic() => Factory().Create(ClassFrom(new ClassSource.Automatic()));

    /// <summary>Mono 16-bit audio holding <paramref name="count"/> samples of one value.</summary>
    private static byte[] Samples(int count, short value) => TestAudio.Shorts(Enumerable.Repeat(value, count).ToArray());

    /// <summary>A capture of an app's sound, which like Windows' process loopback hands over audio all the time, silence included.</summary>
    private static FakeAudioCapture AppCapture(int processId) => new(Pcm16KMono, "chrome", $"process:{processId}") { DeliversContinuously = true };

    /// <summary>Moves the clock on a second and waits for the look at the devices that this sets off to begin.</summary>
    private async Task TickAsync()
    {
        var before = _provider.DeviceQueries;
        _time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => _provider.DeviceQueries > before);
        await Task.Delay(50);                                // lets the look finish before the test goes on
    }

    /// <summary>
    /// The snapshot shows the class track as its session sees it: what it records, how much this run has written, how long
    /// ago sound and audio last arrived, whether its capture hands over audio all the time, the level Windows' meter shows,
    /// and that nothing is wrong. The recording has no microphone track, and its position is what was written.
    /// </summary>
    [Test]
    public void The_snapshot_shows_the_class_track_as_its_session_sees_it()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.4f);
        using var recording = Automatic();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Samples(16000, 5000));             // a second of sound
        _time.StepWallClock(TimeSpan.FromHours(1));          // the time of day alone moves: the snapshot follows the steady clock
        var snapshot = recording.GetSnapshot();

        Assert.That(snapshot, Is.EqualTo(new RecordingSnapshot(
            Position: TimeSpan.FromSeconds(1),
            Paused: false,
            Main: new TrackSnapshot(
                Source: "Speakers", WaitingFor: null, Written: TimeSpan.FromSeconds(1), SinceLastSound: TimeSpan.Zero, SinceLastPacket: TimeSpan.Zero,
                DeliversContinuously: false, RecordedLevel: 0.4f, SoundWhilePaused: false, Capturing: true, Off: false, WriteFailing: false, OnlyZeros: false),
            Microphone: null)));
    }

    /// <summary>
    /// Time passes in the snapshot on the steady clock: how long ago sound last arrived, and audio of any kind, grow while
    /// nothing arrives, and an app's capture says it hands over audio all the time.
    /// </summary>
    [Test]
    public void The_snapshot_counts_how_long_ago_sound_and_audio_arrived()
    {
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        using var recording = Factory().Create(ClassFrom(new ClassSource.App("chrome")));

        recording.Start(_files, TimeSpan.Zero);
        chrome.Deliver(Samples(8000, 5000));                 // half a second of sound
        _time.Advance(TimeSpan.FromSeconds(3));
        chrome.Deliver(Samples(8000, 0));                    // then half a second of silence
        _time.Advance(TimeSpan.FromSeconds(2));
        var snapshot = recording.GetSnapshot();

        Assert.That(snapshot.Main.Source, Is.EqualTo("chrome"));
        Assert.That(snapshot.Main.Written, Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(snapshot.Main.SinceLastSound, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(snapshot.Main.SinceLastPacket, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(snapshot.Main.DeliversContinuously, Is.True);
        Assert.That(snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// While paused the snapshot says so, and that sound keeps arriving although nothing of it is written: the class may be
    /// playing unrecorded. The position stays where the pause began.
    /// </summary>
    [Test]
    public void While_paused_the_snapshot_says_so_and_whether_sound_keeps_arriving()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var recording = Automatic();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Samples(8000, 5000));
        recording.Pause();
        var paused = recording.GetSnapshot();
        speakers.Deliver(Samples(8000, 5000));               // the class plays on during the pause
        var playing = recording.GetSnapshot();
        recording.Resume();
        var resumed = recording.GetSnapshot();

        Assert.That(paused.Paused, Is.True);
        Assert.That(paused.Main.SoundWhilePaused, Is.False, "nothing has played since the pause began");
        Assert.That(playing.Main.SoundWhilePaused, Is.True);
        Assert.That(playing.Position, Is.EqualTo(TimeSpan.FromSeconds(0.5)), "nothing heard during the pause is recorded");
        Assert.That(resumed.Paused, Is.False);
        Assert.That(resumed.Main.SoundWhilePaused, Is.False);
    }

    /// <summary>While nothing can be recorded yet, the snapshot says what the track waits for, and that it captures nothing.</summary>
    [Test]
    public void While_waiting_the_snapshot_says_what_for()
    {
        using var recording = Factory().Create(ClassFrom(new ClassSource.Device("Pixel Buds")));   // not connected

        recording.Start(_files, TimeSpan.Zero);
        var snapshot = recording.GetSnapshot();

        Assert.That(snapshot.Main.WaitingFor, Is.EqualTo("the output device \"Pixel Buds\" to connect"));
        Assert.That(snapshot.Main.Capturing, Is.False);
        Assert.That(snapshot.Main.SinceLastPacket, Is.Null);
        Assert.That(snapshot.Main.Source, Is.Empty, "nothing has been captured yet");
        Assert.That(snapshot.Main.RecordedLevel, Is.Null);
        Assert.That(snapshot.Position, Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>The snapshot says while writing the recording's files fails, as on a full disk or a drive that has gone.</summary>
    [Test]
    public void The_snapshot_says_while_writing_fails()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var classFolder = Path.Combine(_folder.Path, "Kalam");
        using var recording = Automatic();

        recording.Start(new RecordingFiles(classFolder, "Kalam_2026-10-15_23-00-02Z"), TimeSpan.Zero);
        Directory.Delete(classFolder);                       // the drive goes away
        speakers.Deliver(TestAudio.Shorts(1, 2));

        Assert.That(recording.GetSnapshot().Main.WriteFailing, Is.True);
    }

    /// <summary>
    /// A recording continued as its next part starts at the length it already has: its position, and so every mark set in
    /// it, counts on from there, while what this run wrote counts from zero. Its audio goes into the next free part.
    /// </summary>
    [Test]
    public void A_continued_recording_counts_on_from_its_length()
    {
        TestAudio.WriteWav(_files.Segment(1), 16000, new short[16000]);   // the part recorded before
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var recording = Automatic();

        recording.Start(_files, TimeSpan.FromMinutes(47));
        speakers.Deliver(Samples(16000, 5000));
        var position = recording.Position;
        var snapshot = recording.GetSnapshot();
        var summary = recording.Stop();

        Assert.That(position, Is.EqualTo(TimeSpan.FromMinutes(47) + TimeSpan.FromSeconds(1)));
        Assert.That(snapshot.Position, Is.EqualTo(position));
        Assert.That(snapshot.Main.Written, Is.EqualTo(TimeSpan.FromSeconds(1)), "this run's audio alone");
        Assert.That(summary.Length, Is.EqualTo(TimeSpan.FromSeconds(1)), "the earlier part is not counted");
        Assert.That(summary.MainSegments, Is.EqualTo(new[] { _files.Segment(2) }), "the next free part");
    }

    /// <summary>
    /// Stopping says what this run recorded: its length, its files, no microphone files, and that sound was heard. Stopping
    /// again answers the same at once.
    /// </summary>
    [Test]
    public void Stopping_says_what_this_run_recorded()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var recording = Automatic();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Samples(8000, 5000));
        var summary = recording.Stop();
        var again = recording.Stop();

        Assert.That(summary.Length, Is.EqualTo(TimeSpan.FromSeconds(0.5)));
        Assert.That(summary.MainSegments, Is.EqualTo(new[] { _files.Segment(1) }));
        Assert.That(summary.MicrophoneSegments, Is.Empty);
        Assert.That(summary.HeardAnything, Is.True);
        Assert.That(speakers.Disposed, Is.True);
        Assert.That((again.Length, again.MainSegments, again.HeardAnything), Is.EqualTo((summary.Length, summary.MainSegments, summary.HeardAnything)));
    }

    /// <summary>
    /// A run that recorded only silence, or nothing at all, heard nothing: there is nothing to transcribe. The silence it
    /// wrote is still handed back.
    /// </summary>
    [Test]
    public void A_run_of_silence_heard_nothing()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var silent = Automatic();
        using var empty = Factory().Create(ClassFrom(new ClassSource.Device("Pixel Buds")));   // never connected

        silent.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Samples(8000, 0));
        var silence = silent.Stop();
        empty.Start(new RecordingFiles(Path.Combine(_folder.Path, "empty"), "Kalam_2026-10-15_23-00-02Z"), TimeSpan.Zero);
        var nothing = empty.Stop();

        Assert.That(silence.HeardAnything, Is.False);
        Assert.That(silence.MainSegments, Has.Count.EqualTo(1));
        Assert.That(nothing.HeardAnything, Is.False);
        Assert.That(nothing.MainSegments, Is.Empty);
    }

    /// <summary>
    /// A reopen of the class track replaces its capture at the next look. The recording has no microphone track, so a
    /// reopen of one changes nothing, which the log notes.
    /// </summary>
    [Test]
    public async Task A_reopen_replaces_the_class_capture_and_there_is_no_microphone_to_reopen()
    {
        var first = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var second = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;   // ready to open the app again
        using var recording = Factory().Create(ClassFrom(new ClassSource.App("chrome")));

        recording.Start(_files, TimeSpan.Zero);
        recording.Reopen(Track.Microphone, "no audio has arrived for 5 seconds");
        await TickAsync();
        var afterTheMicrophone = second.StartEntered;
        recording.Reopen(Track.Main, "no audio has arrived for 5 seconds");
        await TickAsync();
        await Wait.UntilAsync(() => second.Started);
        recording.Stop();

        Assert.That(afterTheMicrophone, Is.False, "there is no microphone to reopen, and the class is left alone");
        Assert.That(first.DisposeCalls, Is.EqualTo(1));
        Assert.That(_log.Messages, Has.Some.Contains("no microphone"));
        Assert.That(_sessionLog.Messages, Has.Some.Contains("no audio has arrived for 5 seconds"));
    }

    /// <summary>A recording of the class alone has no microphone to switch: V gets no answer from it.</summary>
    [Test]
    public void There_is_no_microphone_to_switch()
    {
        using var recording = Automatic();

        Assert.That(recording.SetMicrophone(false), Is.Null);
        Assert.That(recording.GetSnapshot().Microphone, Is.Null);
    }

    /// <summary>Flushing brings the file on disk up to date at once, as before the process ends.</summary>
    [Test]
    public void Flushing_brings_the_file_on_disk_up_to_date()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var recording = Automatic();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(TestAudio.Shorts(1, 2));            // the clock has not moved, so no save has been due yet
        var before = TestAudio.ReadWav(_files.Segment(1)).Samples;
        recording.Flush();
        var after = TestAudio.ReadWav(_files.Segment(1)).Samples;

        Assert.That(before, Is.Empty);
        Assert.That(after, Is.EqualTo(new short[] { 1, 2 }));
    }

    /// <summary>Disposing a running recording stops it: its capture is released, its file finished, and the device notifications let go.</summary>
    [Test]
    public void Disposing_a_running_recording_stops_it()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var recording = Automatic();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(TestAudio.Shorts(1, 2, 3));
        recording.Dispose();

        Assert.That(speakers.Disposed, Is.True);
        Assert.That(TestAudio.ReadWav(_files.Segment(1)).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
        Assert.That(_provider.HasListeners, Is.False);
        Assert.That(_time.ActiveTimers, Is.Zero, "the looks have ended");
    }

    /// <summary>A recording is started once; a second start is refused, and the first goes on.</summary>
    [Test]
    public void A_recording_is_started_once()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var recording = Automatic();

        recording.Start(_files, TimeSpan.FromMinutes(5));

        Assert.Throws<InvalidOperationException>(() => recording.Start(_files, TimeSpan.Zero));
        speakers.Deliver(Samples(16000, 5000));
        Assert.That(recording.Position, Is.EqualTo(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)), "the first start's offset stands");
    }

    /// <summary>The factory's recording of the device the class plays on records the device with sound, not the default one.</summary>
    [Test]
    public void The_factory_records_the_device_the_class_plays_on()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var headphones = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"), level: 0.2f);
        using var recording = Automatic();

        recording.Start(_files, TimeSpan.Zero);

        Assert.That(headphones.Started, Is.True);
        Assert.That(speakers.Started, Is.False);
        Assert.That(recording.GetSnapshot().Main.Source, Is.EqualTo("Headphones"));
    }

    /// <summary>The factory's recording of a device named in the plan records that device and no other, whatever plays elsewhere.</summary>
    [Test]
    public void The_factory_records_the_device_the_plan_names_and_no_other()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var headphones = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"), level: 0.9f);
        using var recording = Factory().Create(ClassFrom(new ClassSource.Device("speak")));

        recording.Start(_files, TimeSpan.Zero);

        Assert.That(speakers.Started, Is.True);
        Assert.That(headphones.Started, Is.False);
    }

    /// <summary>The factory's recording of an app named in the plan records the app's sound from its process, never a device.</summary>
    [Test]
    public void The_factory_records_the_app_the_plan_names()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.5f);
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        using var recording = Factory().Create(ClassFrom(new ClassSource.App("chrome")));

        recording.Start(_files, TimeSpan.Zero);

        Assert.That(chrome.Started, Is.True);
        Assert.That(speakers.Started, Is.False);
    }

    /// <summary>A plan for the class that names no class source records the device the class plays on, as when nothing is pinned.</summary>
    [Test]
    public void A_plan_without_a_class_source_records_the_device_the_class_plays_on()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var headphones = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"), level: 0.2f);
        using var recording = Factory().Create(new SourcePlan(RecordingMode.Class, Class: null, Microphone: null));

        recording.Start(_files, TimeSpan.Zero);

        Assert.That(headphones.Started, Is.True);
    }

    /// <summary>
    /// Each recording the factory makes has a session and a choice of its own: two recordings made one after the other do
    /// not share what either has chosen or written.
    /// </summary>
    [Test]
    public void Each_recording_has_a_session_of_its_own()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var factory = Factory();
        using var one = factory.Create(ClassFrom(new ClassSource.Automatic()));
        using var other = factory.Create(ClassFrom(new ClassSource.Automatic()));

        one.Start(_files, TimeSpan.Zero);
        other.Start(new RecordingFiles(Path.Combine(_folder.Path, "other"), "Fiqh_2026-10-15_23-00-02Z"), TimeSpan.Zero);
        first.Deliver(Samples(16000, 5000));

        Assert.That(second.Started, Is.True);
        Assert.That(one.Position, Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(other.Position, Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>
    /// The factory makes recordings of the class alone: a plan with your microphone, with the class or in person, is
    /// refused with a plain reason, which the log keeps too, before anything is looked at or opened.
    /// </summary>
    [TestCase(RecordingMode.ClassAndMicrophone)]
    [TestCase(RecordingMode.Microphone)]
    public void A_plan_with_the_microphone_is_refused(RecordingMode mode)
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var plan = new SourcePlan(mode, mode == RecordingMode.Microphone ? null : new ClassSource.Automatic(), new MicrophoneChoice(null));

        var refusal = Assert.Throws<NotSupportedException>(() => Factory().Create(plan))!;

        Assert.That(refusal.Message, Does.Contain("records the class alone").And.Contain("microphone"));
        Assert.That(_log.Problems, Has.Some.Contains($"Cannot record {mode}"));
        Assert.That(_provider.DeviceQueries, Is.Zero, "nothing was looked at");
        Assert.That(_provider.Created, Is.Empty, "and nothing opened");
    }

    /// <summary>
    /// The app's own services make recordings in a run's scope: the factory is the track-set factory, which makes the
    /// class alone a recording of the class as ClassScribe has always recorded it; the record command resolves beside it;
    /// and the recording the factory makes from what is chosen when the run begins, here an app picked in menu 6, records
    /// that app. Nothing is opened until the recording starts, and the stand-in devices replace Windows' own. (No session
    /// is registered by itself any more: the run makes its recording through the factory.)
    /// </summary>
    [Test]
    public void The_app_s_services_make_recordings_in_a_run_s_scope()
    {
        using var settings = new TempFolder();
        File.WriteAllText(settings.File("appsettings.json"), "{}");
        using var host = AppHost.Build(new SettingsPlaces(settings.Path, userFolder: null), apiKey: null, services => services
            .AddSingleton<ILoopbackCaptureProvider>(_provider)
            .AddSingleton(_runtime.Folder));   // never the real %LOCALAPPDATA%\ClassScribe
        var choice = host.Services.GetRequiredService<SourceChoice>();
        choice.App = "chrome";
        using var scope = host.Services.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IRecordingFactory>();
        var command = scope.ServiceProvider.GetRequiredService<RecordCommand>();
        using var recording = factory.Create(choice.Current);
        var nothingLookedAt = _provider.DeviceQueries == 0;
        recording.Start(_files, TimeSpan.Zero);
        var waitingFor = recording.GetSnapshot().Main.WaitingFor;
        recording.Stop();

        Assert.That(factory, Is.InstanceOf<TrackSetFactory>());
        Assert.That(recording, Is.InstanceOf<ClassRecording>(), "the class alone is recorded as it always was");
        Assert.That(command, Is.Not.Null);
        Assert.That(() => scope.ServiceProvider.GetService<RecordingSession>(), Is.Null, "no session is registered by itself");
        Assert.That(nothingLookedAt, Is.True);
        Assert.That(waitingFor, Is.EqualTo("\"chrome\" to play sound"), "the recording follows the app picked in menu 6");
    }

    /// <summary>
    /// A long stretch with nothing playing, which the session leaves out of a recording of the class alone, reaches the
    /// recording's <see cref="IRecording.NoteWorthy"/> at the recording's position: the length the run continued at plus
    /// where the session left it out, so that a continued recording's heading lands at the right place. The session gets
    /// the setting as the factory passes it, in its track spec.
    /// </summary>
    [Test]
    public void A_long_stretch_with_nothing_playing_is_noted_at_the_recordings_position()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var source = new LoopbackSource(_provider, new CaptureChooser(_time, new ClassSource.Automatic(), _chooserLog));
        var session = new RecordingSession(source, new TrackSpec(Track.Main, TrackPlacement.Contiguous, TimeSpan.FromSeconds(60)), null,
            TestOptions.Create(new RecordingOptions()), _time, _sessionLog);
        using var recording = new ClassRecording(session, _log);
        var notes = new List<TimelineNote>();
        recording.NoteWorthy += (_, note) => notes.Add(note);

        recording.Start(_files, offset: TimeSpan.FromMinutes(30));      // continuing a recording half an hour long
        capture.Deliver(Samples(8000, 1), Stamp(10));                   // half a second, ending at 10.5 s
        capture.Deliver(Samples(8000, 2), Stamp(201));                  // 190.5 s later: about 3 minutes left out
        var summary = recording.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(summary.Length, Is.EqualTo(TimeSpan.FromSeconds(1)), "nothing was written for the gap");
            Assert.That(notes, Is.EqualTo(new[] { new TimelineNote(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(0.5), "About 3 minutes with nothing playing were left out here") }));
        });
    }

    /// <summary>A packet's stamp <paramref name="seconds"/> after the test clock's zero, in its units of 100 nanoseconds.</summary>
    private static long Stamp(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
}
