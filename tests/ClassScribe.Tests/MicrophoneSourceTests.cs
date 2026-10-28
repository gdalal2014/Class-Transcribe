using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Settings;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The microphone as a recording session's source: it picks what the chooser picks from what the provider lists, opens
/// the capture with the processing asked for, looks every ten seconds and at once when Windows reports a change among
/// the microphones or the class app is found on another one, and words the session's messages for a microphone.
/// Nothing here opens a real microphone: the provider is a fake.
/// </summary>
[TestFixture]
public sealed class MicrophoneSourceTests
{
    private static readonly Microphone Array = LaptopMicrophones.Array;
    private static readonly Microphone Obsbot = LaptopMicrophones.Obsbot;

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private FakeMicrophoneProvider _provider = null!;
    private ManualTimeProvider _time = null!;
    private RecordingLogger<MicrophoneChooser> _chooserLog = null!;
    private RecordingLogger<RecordingSession> _log = null!;

    /// <summary>A temporary folder and a recording in it, the laptop's microphones, a clock moved by hand, and logs to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Class_2026-10-17_19-00-00Z");
        _provider = new FakeMicrophoneProvider().Connect(Array, Obsbot);
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 17, 19, 0, 0, TimeSpan.Zero));
        _chooserLog = new RecordingLogger<MicrophoneChooser>();
        _log = new RecordingLogger<RecordingSession>();
    }

    /// <summary>Deletes the temporary folder, which fails if a test left a file open.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>A source over the fake provider, choosing by <paramref name="namePart"/> or Windows' default, asking for <paramref name="processing"/>.</summary>
    private MicrophoneSource Source(string? namePart = null, MicrophoneProcessing processing = MicrophoneProcessing.Raw) =>
        new(_provider, new MicrophoneChooser(namePart, _time, _chooserLog), processing);

    /// <summary>A session recording the microphone track on the clock through <paramref name="source"/>.</summary>
    private RecordingSession Session(MicrophoneSource source) =>
        new(source, new WavTrackWriterFactory(), new TrackSpec(Track.Microphone, TrackPlacement.ClockAligned), new RecordingClock(_time), TestOptions.Create(new RecordingOptions()), _time, _log);

    /// <summary>The source's words name the microphone, it looks every ten seconds, and it has no level meter to read.</summary>
    [Test]
    public void Its_words_and_pace_are_those_of_a_microphone()
    {
        using var source = Source();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Wording, Is.EqualTo(new SourceWording("the microphone", "the microphone")));
            Assert.That(source.CheckInterval, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(source.RecordedLevel, Is.Null);
        }
    }

    /// <summary>A look picks what the chooser picks, Windows' default microphone here, and opening it asks the provider for that microphone with the processing wanted.</summary>
    [TestCase(MicrophoneProcessing.Raw)]
    [TestCase(MicrophoneProcessing.Default)]
    public void Picks_what_the_chooser_picks_and_opens_it_with_the_processing_asked_for(MicrophoneProcessing processing)
    {
        using var source = Source(processing: processing);

        var target = source.Choose(recordedId: null, sinceRecordedSound: null);
        var capture = source.Open(target!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target, Is.EqualTo(new CaptureTarget(Array.Id, Array.Name)));
            Assert.That(_provider.Opened, Is.EqualTo(new[] { (Array.Id, processing) }));
            Assert.That(capture.DeviceName, Is.EqualTo(Array.Name));
            Assert.That(_provider.Listings, Is.EqualTo(1));
        }
    }

    /// <summary>A name part picks the microphone it names.</summary>
    [Test]
    public void A_name_picks_the_microphone_it_names()
    {
        using var source = Source("obsbot");

        var target = source.Choose(null, null);

        Assert.That(target?.Id, Is.EqualTo(Obsbot.Id));
    }

    /// <summary>With no microphone listed, a look picks nothing and says what is waited for.</summary>
    [Test]
    public void Waits_when_there_is_no_microphone_to_record()
    {
        _provider.Disconnect(Array.Id);
        _provider.Disconnect(Obsbot.Id);
        using var source = Source("OBSBOT");

        var target = source.Choose(null, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target, Is.Null);
            Assert.That(source.WaitingFor, Is.EqualTo("the microphone \"OBSBOT\""));
        }
    }

    /// <summary>A microphone that no look has listed cannot be opened: the session only opens what the look before picked.</summary>
    [Test]
    public void Opening_a_microphone_no_look_has_listed_fails()
    {
        using var source = Source();

        Assert.That(() => source.Open(new CaptureTarget("{0.0.1.00000000}.{unknown}", "Unknown")), Throws.InvalidOperationException);
    }

    /// <summary>
    /// When the class app is found listening on another microphone, the source is told, says so to whoever listens so that
    /// the session looks at once, and the next look picks that microphone; the move is described by the chooser's reason.
    /// </summary>
    [Test]
    public void Follows_the_class_apps_microphone_at_the_next_look()
    {
        using var source = Source();
        var changes = 0;
        source.Changed += (_, _) => changes++;
        var first = source.Choose(null, null)!;

        source.FollowApp(Obsbot.Id);
        var second = source.Choose(first.Id, null)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(first.Id, Is.EqualTo(Array.Id));
            Assert.That(second.Id, Is.EqualTo(Obsbot.Id));
            Assert.That(source.DescribeMove(second, first.Name), Is.EqualTo($"Recording the microphone \"{Obsbot.Name}\", the microphone the class app uses, instead of \"{Array.Name}\""));
        }
    }

    /// <summary>When the chosen microphone has been gone for ten seconds, the stand-in the chooser picks is described as standing in until the chosen one returns.</summary>
    [Test]
    public void A_stand_in_is_described_as_such()
    {
        using var source = Source();
        var chosen = source.Choose(null, null)!;

        _provider.Disconnect(Array.Id);
        var waiting = source.Choose(chosen.Id, null);     // the first look that misses it starts the ten seconds
        _time.Advance(TimeSpan.FromSeconds(10));
        var standIn = source.Choose(null, null)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting, Is.Null, "the chosen microphone is waited for first");
            Assert.That(source.WaitingFor, Is.EqualTo($"the microphone \"{Array.Name}\""));
            Assert.That(standIn.Id, Is.EqualTo(Obsbot.Id));
            Assert.That(source.DescribeMove(standIn, Array.Name), Is.EqualTo($"Recording the microphone \"{Obsbot.Name}\" instead of \"{Array.Name}\" until it returns"));
        }
    }

    /// <summary>Telling the source the app's microphone is no longer known, or the one it already follows, raises nothing.</summary>
    [Test]
    public void Following_the_same_microphone_again_says_nothing()
    {
        using var source = Source();
        var changes = 0;
        source.Changed += (_, _) => changes++;

        source.FollowApp(Obsbot.Id);
        source.FollowApp(Obsbot.Id);
        source.FollowApp(null);
        source.FollowApp(null);

        Assert.That(changes, Is.EqualTo(2));
    }

    /// <summary>Windows' notifications about the microphones reach the session through the source, and are let go of when the last listener leaves or the source is disposed.</summary>
    [Test]
    public void Passes_on_the_providers_notifications_while_anyone_listens()
    {
        var source = Source();
        var changes = 0;
        EventHandler listener = (_, _) => changes++;

        source.Changed += listener;
        var listening = _provider.HasListeners;
        _provider.RaiseChanged();
        source.Changed -= listener;
        var afterLeaving = _provider.HasListeners;
        _provider.RaiseChanged();
        source.Changed += listener;
        source.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(listening, Is.True);
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(afterLeaving, Is.False);
            Assert.That(_provider.HasListeners, Is.False, "disposing the source lets go of the provider's notifications");
            Assert.That(_provider.Disposed, Is.False, "the provider belongs to the app, not to the source");
        }
    }

    /// <summary>Through a session, the microphone is listed once at the start and then every ten seconds, never every second.</summary>
    [Test]
    public async Task A_session_on_the_microphone_looks_every_ten_seconds()
    {
        using var session = Session(Source());

        session.Start(_files);
        var atStart = _provider.Listings;
        _time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(100);
        var afterNine = _provider.Listings;
        _time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => _provider.Listings > afterNine);
        session.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atStart, Is.EqualTo(1));
            Assert.That(afterNine, Is.EqualTo(1));
            Assert.That(_provider.Listings, Is.EqualTo(2));
        }
    }

    /// <summary>Through a session, a change among the microphones that Windows reports is looked at at once.</summary>
    [Test]
    public async Task A_session_on_the_microphone_looks_at_once_when_the_microphones_change()
    {
        using var session = Session(Source());

        session.Start(_files);
        _provider.RaiseChanged();
        await Wait.UntilAsync(() => _provider.Listings == 2);
        session.Stop();

        Assert.That(_provider.Listings, Is.EqualTo(2));
    }

    /// <summary>A session records the microphone the source picks, through a capture that hands over audio all the time, into the microphone's files.</summary>
    [Test]
    public void A_session_records_the_microphone_the_source_picks()
    {
        using var session = Session(Source());

        session.Start(_files);
        var status = session.GetStatus();
        _provider.Created[0].Deliver(TestAudio.Floats(0.25f, 0.25f), qpc100ns: _time.GetTimestamp());
        var result = session.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.DeviceName, Is.EqualTo(Array.Name));
            Assert.That(status.DeliversContinuously, Is.True);
            Assert.That(result.SegmentPaths, Is.EqualTo(new[] { _files.Segment(1, Track.Microphone) }));
            Assert.That(_log.Messages, Has.Some.Contains($"Capturing \"{Array.Name}\""));
        }
    }

    /// <summary>A microphone that cannot be opened yet is tried again at the next look, ten seconds on, with the session's usual words for the microphone.</summary>
    [Test]
    public async Task A_microphone_that_cannot_be_opened_is_tried_again_at_the_next_look()
    {
        _provider.CaptureFailure = LaptopMicrophones.InUse();
        using var session = Session(Source());

        session.Start(_files);
        _provider.CaptureFailure = null;
        _time.Advance(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => _provider.Created.Count == 1);
        session.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_log.Problems, Has.Some.Contains("Could not start capturing the microphone yet; trying again every 10 seconds"));
            Assert.That(_provider.Opened, Has.Count.EqualTo(2));
        }
    }
}
