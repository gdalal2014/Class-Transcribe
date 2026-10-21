using System.Runtime.InteropServices;
using ClassScribe.Audio.Microphones;

namespace ClassScribe.Tests;

/// <summary>
/// The watch for the class app on a Bluetooth microphone, over the fakes of Windows' two lists: which uses it finds, which
/// apps count, what it leaves out, that each use is reported once as it begins and once as it ends, how often it looks, and
/// what it does when a list cannot be read.
/// </summary>
[TestFixture]
public class BluetoothMicrophoneWatchTests
{
    private static readonly Microphone Array = LaptopMicrophones.Array;
    private static readonly Microphone Buds = LaptopMicrophones.PixelBuds;
    private static readonly IReadOnlyList<FollowRule.Named> AnyApp = [];
    private static readonly IReadOnlyList<FollowRule.Named> Chrome = [new FollowRule.Named("chrome")];

    private ManualTimeProvider _time = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private RecordingLogger<BluetoothMicrophoneWatch> _log = null!;
    private BluetoothMicrophoneWatch _watch = null!;

    /// <summary>The clock, the sessions reader with nothing on any microphone, the laptop's array and the Pixel Buds connected, and the watch over them.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _sessions = new FakeMicrophoneSessions();
        _microphones = new FakeMicrophoneProvider().Connect(Array, Buds);
        _log = new RecordingLogger<BluetoothMicrophoneWatch>();
        _watch = new BluetoothMicrophoneWatch(_sessions, _microphones, new FakeOwnProcesses(Laptop.OwnProcess), _time, _log);
    }

    /// <summary>Chrome's capture session on <paramref name="microphone"/>, active unless <paramref name="state"/> says otherwise.</summary>
    private static CaptureSession ChromeOn(Microphone microphone, CaptureSessionState state = CaptureSessionState.Active) =>
        Laptop.Session(microphone.Id, Laptop.ChromeUtility, state);

    /// <summary>The use the watch reports for chrome on the Pixel Buds.</summary>
    private static readonly BluetoothMicrophoneUse ChromeOnBuds = new("chrome", Buds.Name);

    /// <summary>An app taking sound from a Bluetooth microphone is found, named with the microphone, and the array's name comes along for the advice.</summary>
    [Test]
    public void An_app_taking_sound_from_a_Bluetooth_microphone_is_found()
    {
        _sessions.SetSessions(Buds.Id, ChromeOn(Buds));

        var look = _watch.LookNow(AnyApp);

        Assert.That(look.Began, Is.EqualTo([ChromeOnBuds]));
        Assert.That(look.Current, Is.EqualTo([ChromeOnBuds]));
        Assert.That(look.Ended, Is.Empty);
        Assert.That((look.BuiltInMicrophone, look.Problem), Is.EqualTo((Array.Name, (string?)null)));
    }

    /// <summary>A session that is not taking sound, or one on a microphone that is not Bluetooth, is nothing to warn about.</summary>
    [Test]
    public void A_session_not_active_or_not_on_a_Bluetooth_microphone_is_nothing()
    {
        _sessions.SetSessions(Buds.Id, ChromeOn(Buds, CaptureSessionState.Inactive));
        _sessions.SetSessions(Array.Id, ChromeOn(Array));

        var look = _watch.LookNow(AnyApp);

        Assert.That(look.Began, Is.Empty);
        Assert.That(look.Current, Is.Empty);
    }

    /// <summary>With apps named, only their sessions count, matched by part of the name in any case; with none named, any app's.</summary>
    [Test]
    public void Only_the_apps_named_count_when_some_are_named()
    {
        _sessions.SetSessions(Buds.Id, ChromeOn(Buds));

        var zoom = _watch.LookNow([new FollowRule.Named("zoom")]);
        var chrome = _watch.LookNow([new FollowRule.Named("CHROME")]);

        Assert.That(zoom.Current, Is.Empty);
        Assert.That(chrome.Current, Is.EqualTo([ChromeOnBuds]));
    }

    /// <summary>ClassScribe's own sessions and Windows' system sounds never count, and a session of no process neither.</summary>
    [Test]
    public void ClassScribes_own_sessions_and_Windows_sounds_never_count()
    {
        _sessions.SetSessions(Buds.Id, Laptop.Session(Buds.Id, Laptop.OwnProcess), Laptop.Session(Buds.Id, Laptop.OtherClassScribe), Laptop.SystemSounds(Buds.Id));

        Assert.That(_watch.LookNow(AnyApp).Current, Is.Empty);
    }

    /// <summary>A process that could not be named is named by its number, and counts for any app but never for a named one.</summary>
    [Test]
    public void A_process_that_could_not_be_named_counts_for_any_app_only()
    {
        _sessions.SetSessions(Buds.Id, Laptop.Session(Buds.Id, 999));

        var any = _watch.LookNow(AnyApp);
        var named = new BluetoothMicrophoneWatch(_sessions, _microphones, new FakeOwnProcesses(), _time, _log).LookNow(Chrome);

        Assert.That(any.Current, Is.EqualTo([new BluetoothMicrophoneUse("process 999", Buds.Name)]));
        Assert.That(named.Current, Is.Empty);
    }

    /// <summary>Each use is reported once as it begins and once as it ends; between the two it is only current.</summary>
    [Test]
    public void Each_use_is_reported_once_as_it_begins_and_once_as_it_ends()
    {
        _sessions.SetSessions(Buds.Id, ChromeOn(Buds));
        var first = _watch.LookNow(AnyApp);
        var second = _watch.LookNow(AnyApp);
        _sessions.SetSessions(Buds.Id);
        var third = _watch.LookNow(AnyApp);

        Assert.That((first.Began, first.Ended), Is.EqualTo((new[] { ChromeOnBuds }, System.Array.Empty<BluetoothMicrophoneUse>())));
        Assert.That((second.Began, second.Ended, second.Current), Is.EqualTo((System.Array.Empty<BluetoothMicrophoneUse>(), System.Array.Empty<BluetoothMicrophoneUse>(), new[] { ChromeOnBuds })));
        Assert.That((third.Began, third.Ended, third.Current), Is.EqualTo((System.Array.Empty<BluetoothMicrophoneUse>(), new[] { ChromeOnBuds }, System.Array.Empty<BluetoothMicrophoneUse>())));
    }

    /// <summary>It looks at the first call and then every 10 seconds, as often as the class microphone watch reads every microphone, and not between.</summary>
    [Test]
    public void It_looks_at_the_first_call_and_then_every_10_seconds()
    {
        var first = _watch.Look(AnyApp, _time.GetTimestamp());
        _time.Advance(TimeSpan.FromSeconds(9));
        var tooSoon = _watch.Look(AnyApp, _time.GetTimestamp());
        _time.Advance(TimeSpan.FromSeconds(1));
        var due = _watch.Look(AnyApp, _time.GetTimestamp());

        Assert.That((first is not null, tooSoon is null, due is not null), Is.EqualTo((true, true, true)));
        Assert.That((_sessions.ReadAlls, _microphones.Listings), Is.EqualTo((2, 2)));
        Assert.That(BluetoothMicrophoneWatch.Interval, Is.EqualTo(ClassMicrophoneWatch.AllMicrophonesInterval));
    }

    /// <summary>
    /// Lists that cannot be read now give a look that says why and keeps the uses of the look before, so that nothing is said
    /// to have ended; the failure is noted at Debug once per stretch, and the next look that reads goes on from where it was.
    /// </summary>
    [Test]
    public void Lists_that_cannot_be_read_keep_the_last_uses_and_say_why()
    {
        _sessions.SetSessions(Buds.Id, ChromeOn(Buds));
        _watch.LookNow(AnyApp);
        _sessions.ListingProblem = "The audio service is not running (0x88890010)";

        var failed = _watch.LookNow(AnyApp);
        var failedAgain = _watch.LookNow(AnyApp);
        _sessions.ListingProblem = null;
        _sessions.SetSessions(Buds.Id);
        var recovered = _watch.LookNow(AnyApp);

        Assert.That((failed.Problem, failed.Current, failed.Ended), Is.EqualTo(("The audio service is not running (0x88890010)", new[] { ChromeOnBuds }, System.Array.Empty<BluetoothMicrophoneUse>())));
        Assert.That(failedAgain.Problem, Is.Not.Null);
        Assert.That((recovered.Problem, recovered.Ended), Is.EqualTo(((string?)null, new[] { ChromeOnBuds })));
        Assert.That(_log.Messages.Count(message => message.Contains("Could not read Windows' microphone lists")), Is.EqualTo(1), "once per stretch");
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A microphone listing that fails, as a stale enumerator makes it, is a problem of the same kind, never thrown.</summary>
    [Test]
    public void A_microphone_listing_that_fails_is_a_problem_too()
    {
        _microphones.ListingFailure = new COMException("The audio service is not running.", unchecked((int)0x88890010));

        var look = _watch.LookNow(AnyApp);

        Assert.That(look.Problem, Does.Contain("The audio service is not running"));
        Assert.That(look.Current, Is.Empty);
    }

    /// <summary>
    /// A listing that fails with any exception a failed result can reach the watch as is a problem of the same kind, never
    /// thrown: here E_INVALIDARG as .NET's own mapping gives it, an ArgumentException, which on 18 October would have ended
    /// the second's checks before the heartbeat and the state reached the window. The next look reads again.
    /// </summary>
    [Test]
    public void A_listing_that_fails_with_an_ArgumentException_is_a_problem_too()
    {
        _sessions.SetSessions(Buds.Id, ChromeOn(Buds));
        _microphones.ListingFailure = new ArgumentException("Value does not fall within the expected range.");

        var failed = _watch.LookNow(AnyApp);
        _microphones.ListingFailure = null;
        var read = _watch.LookNow(AnyApp);

        Assert.That(failed.Problem, Is.EqualTo("Value does not fall within the expected range."));
        Assert.That(failed.Current, Is.Empty);
        Assert.That((read.Problem, read.Began), Is.EqualTo(((string?)null, new[] { ChromeOnBuds })));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A microphone that is Bluetooth by a property alone, as the soundcore headset is on the INTELAUDIO bus, counts the same.</summary>
    [Test]
    public void A_Bluetooth_microphone_known_by_a_property_counts()
    {
        _microphones.Connect(LaptopMicrophones.Soundcore);
        _sessions.SetSessions(LaptopMicrophones.Soundcore.Id, ChromeOn(LaptopMicrophones.Soundcore));

        Assert.That(_watch.LookNow(Chrome).Current, Is.EqualTo([new BluetoothMicrophoneUse("chrome", LaptopMicrophones.Soundcore.Name)]));
    }
}
