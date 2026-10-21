using System.Diagnostics;
using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using ClassScribe.Desktop.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The reader of which apps have a microphone open, driven through fakes of Windows' audio objects: what it reads,
/// that it releases every object, what it does when a read fails, and that it never asks for anything that could open
/// a microphone.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class MicrophoneSessionsTests
{
    private FakeComLedger _ledger = null!;
    private FakeMicrophoneEndpoint _array = null!;
    private FakeMicrophoneEndpoint _obsbot = null!;
    private FakeCaptureEnumerator _enumerator = null!;
    private FakeProcessNames _names = null!;
    private RecordingLogger<WasapiMicrophoneSessions> _log = null!;
    private int _enumeratorsMade;
    private WasapiMicrophoneSessions _reader = null!;

    /// <summary>This laptop's two microphones as fakes, with Chrome's utility process and Windows' sounds on the array, behind a reader that keeps account of what it releases.</summary>
    [SetUp]
    public void SetUp()
    {
        _ledger = new FakeComLedger();
        _array = new FakeMicrophoneEndpoint(Laptop.ArrayId, Laptop.ArrayName,
            new FakeCaptureSessionControl("array|chrome", Laptop.ChromeUtility),
            new FakeCaptureSessionControl("array|system", 0, systemSounds: true))
        { Ledger = _ledger };
        _obsbot = new FakeMicrophoneEndpoint(Laptop.ObsbotId, Laptop.ObsbotName) { Ledger = _ledger, Muted = true, Volume = 0.5f };
        _enumerator = new FakeCaptureEnumerator(_array, _obsbot) { Ledger = _ledger };
        _enumerator.Defaults[CoreAudio.Console] = Laptop.ArrayId;
        _enumerator.Defaults[CoreAudio.Multimedia] = Laptop.ArrayId;
        _enumerator.Defaults[CoreAudio.Communications] = Laptop.ObsbotId;
        _names = new FakeProcessNames();
        _log = new RecordingLogger<WasapiMicrophoneSessions>();
        _enumeratorsMade = 0;
        _reader = new WasapiMicrophoneSessions(_names, _log, MakeEnumerator, device => ((FakeMicrophoneEndpoint)device).Name, _ledger.Release);
    }

    /// <summary>Disposes the reader.</summary>
    [TearDown]
    public void TearDown() => _reader.Dispose();

    /// <summary>Hands out the fake enumerator, counting the times.</summary>
    private FakeCaptureEnumerator MakeEnumerator()
    {
        _enumeratorsMade++;
        return _ledger.HandOut(_enumerator);
    }

    /// <summary>One microphone is read with its name, the roles it is default for, its mute and volume, and every session with its process, name, state, kind and mute.</summary>
    [Test]
    public void A_microphone_is_read_with_its_sessions()
    {
        var listing = _reader.Read(Laptop.ArrayId);

        Assert.Multiple(() =>
        {
            Assert.That(listing.EndpointId, Is.EqualTo(Laptop.ArrayId));
            Assert.That(listing.Name, Is.EqualTo(Laptop.ArrayName));
            Assert.That(listing.DefaultFor, Is.EqualTo(MicrophoneRoles.Console | MicrophoneRoles.Multimedia));
            Assert.That(listing.MutedInWindows, Is.False);
            Assert.That(listing.VolumePercent, Is.EqualTo(80));
            Assert.That(listing.Complete, Is.True);
            Assert.That(listing.Problem, Is.Null);
            Assert.That(listing.Sessions, Is.EqualTo(new[]
            {
                new CaptureSession("array|chrome", Laptop.ArrayId, Laptop.ChromeUtility, "chrome", SystemSounds: false, CaptureSessionState.Active, SessionMuted: false),
                new CaptureSession("array|system", Laptop.ArrayId, 0, null, SystemSounds: true, CaptureSessionState.Active, SessionMuted: false)
            }));
        });
    }

    /// <summary>Every active microphone is read, each with its own defaults, mute and volume.</summary>
    [Test]
    public void Every_microphone_is_read()
    {
        var (listings, problem) = _reader.ReadAll();
        Assert.That(problem, Is.Null);

        Assert.Multiple(() =>
        {
            Assert.That(listings.Select(listing => listing.EndpointId), Is.EqualTo(new[] { Laptop.ArrayId, Laptop.ObsbotId }));
            Assert.That(listings[1].Name, Is.EqualTo(Laptop.ObsbotName));
            Assert.That(listings[1].DefaultFor, Is.EqualTo(MicrophoneRoles.Communications));
            Assert.That(listings[1].MutedInWindows, Is.True);
            Assert.That(listings[1].VolumePercent, Is.EqualTo(50));
            Assert.That(listings[1].Sessions, Is.Empty);
            Assert.That(_enumerator.DataFlowsAsked, Is.All.EqualTo(CoreAudio.Capture), "microphones only, never output devices");
        });
    }

    /// <summary>Every object Windows hands over is released once it has been read, and none is released twice.</summary>
    [Test]
    public void Every_object_is_released()
    {
        _reader.Read(Laptop.ArrayId);
        _reader.ReadAll();

        Assert.Multiple(() =>
        {
            Assert.That(_ledger.HandedOut, Is.GreaterThan(10));
            Assert.That(_ledger.Unreleased, Is.EqualTo(new[] { "FakeCaptureEnumerator x1" }), "only the enumerator is kept between reads");
            Assert.That(_ledger.OverReleased, Is.Empty);
        });

        _reader.Dispose();

        Assert.That(_ledger.Unreleased, Is.Empty);
    }

    /// <summary>
    /// Reading asks the microphone for its endpoint volume and its session manager and nothing else: never for an audio
    /// client, the object through which a program opens a microphone. The fakes also fail the test should the reader ask
    /// the session manager for a session of its own, or register for any notification.
    /// </summary>
    [Test]
    public void Reading_asks_for_nothing_that_could_open_the_microphone()
    {
        _reader.ReadAll();

        Assert.Multiple(() =>
        {
            Assert.That(_array.Activated, Is.EquivalentTo(new[] { typeof(IAudioEndpointVolumeRaw).GUID, typeof(IAudioSessionManager2Raw).GUID }));
            Assert.That(_array.Activated, Has.None.EqualTo(FakeMicrophoneEndpoint.AudioClientId));
        });
    }

    /// <summary>Each process is looked up once per read, however many sessions it has, and not kept from one read to the next, because Windows reuses process ids.</summary>
    [Test]
    public void A_process_is_looked_up_once_per_read()
    {
        var array = new FakeMicrophoneEndpoint(Laptop.ArrayId, Laptop.ArrayName,
            new FakeCaptureSessionControl("array|chrome1", Laptop.ChromeUtility),
            new FakeCaptureSessionControl("array|chrome2", Laptop.ChromeUtility));
        _reader = new WasapiMicrophoneSessions(_names, _log, () => new FakeCaptureEnumerator(array), device => ((FakeMicrophoneEndpoint)device).Name);

        _reader.Read(Laptop.ArrayId);
        Assert.That(_names.Lookups[Laptop.ChromeUtility], Is.EqualTo(1));

        _reader.Read(Laptop.ArrayId);
        Assert.That(_names.Lookups[Laptop.ChromeUtility], Is.EqualTo(2));
    }

    /// <summary>A session that cannot be read leaves the listing incomplete, with the reason, and the other sessions are read all the same.</summary>
    [Test]
    public void A_session_that_cannot_be_read_leaves_the_listing_incomplete()
    {
        var array = new FakeMicrophoneEndpoint(Laptop.ArrayId, Laptop.ArrayName,
            new FakeCaptureSessionControl("array|chrome", Laptop.ChromeUtility),
            new FakeCaptureSessionControl("array|vibe", Laptop.Vibe) { StateFailure = FakeResults.DeviceInvalidated });
        _reader = new WasapiMicrophoneSessions(_names, _log, () => new FakeCaptureEnumerator(array), device => ((FakeMicrophoneEndpoint)device).Name);

        var listing = _reader.Read(Laptop.ArrayId);

        Assert.Multiple(() =>
        {
            Assert.That(listing.Complete, Is.False);
            Assert.That(listing.Problem, Does.Contain("session 2 of 2 not readable").And.Contain("0x88890004"));
            Assert.That(listing.Sessions.Select(session => session.ProcessId), Is.EqualTo(new[] { Laptop.ChromeUtility }));
        });
    }

    /// <summary>
    /// As with the Zoom app in a meeting on 18 October, a session that answers E_INVALIDARG to any call the reader makes
    /// of it, or to the listing's own call that hands it out, leaves the listing incomplete, with the result in its
    /// reason, and the other sessions are read, by the read of one microphone and by the read of all. It used to reach the
    /// microphone watch as an ArgumentException, which no catch here expected, and stopped the watch for the rest of the
    /// run. Every object handed out is released, and the enumerator is kept, since the reader itself did not fail.
    /// </summary>
    [TestCase("GetSessionInstanceIdentifier")]
    [TestCase("GetState")]
    [TestCase("GetProcessId")]
    [TestCase("GetSession")]
    public void A_session_that_answers_E_INVALIDARG_leaves_the_listing_incomplete_and_the_others_are_read(string call)
    {
        var zoom = call switch
        {
            "GetSessionInstanceIdentifier" => new FakeCaptureSessionControl("array|zoom", Laptop.Zoom) { InstanceIdFailure = FakeResults.InvalidArgument },
            "GetState" => new FakeCaptureSessionControl("array|zoom", Laptop.Zoom) { StateFailure = FakeResults.InvalidArgument },
            "GetProcessId" => new FakeCaptureSessionControl("array|zoom", Laptop.Zoom) { ProcessIdFailure = FakeResults.InvalidArgument },
            _ => new FakeCaptureSessionControl("array|zoom", Laptop.Zoom) { HandOutFailure = FakeResults.InvalidArgument }
        };
        var array = new FakeMicrophoneEndpoint(Laptop.ArrayId, Laptop.ArrayName,
            new FakeCaptureSessionControl("array|chrome", Laptop.ChromeUtility), zoom, new FakeCaptureSessionControl("array|system", 0, systemSounds: true))
        { Ledger = _ledger };
        _enumerator = new FakeCaptureEnumerator(array, _obsbot) { Ledger = _ledger };

        var one = _reader.Read(Laptop.ArrayId);
        var all = _reader.ReadAll();
        var heldBetweenReads = _ledger.Unreleased;

        Assert.Multiple(() =>
        {
            Assert.That(one.Complete, Is.False);
            Assert.That(one.Problem, Does.Contain("session 2 of 3 not readable").And.Contain("0x80070057"));
            Assert.That(one.Sessions.Select(session => session.Key), Is.EqualTo(new[] { "array|chrome", "array|system" }));
            Assert.That(all.Problem, Is.Null, "the other microphone was read too");
            Assert.That(all.Listings.Select(listing => (listing.EndpointId, listing.Complete)), Is.EqualTo(new[] { (Laptop.ArrayId, false), (Laptop.ObsbotId, true) }));
            Assert.That(all.Listings[0].Sessions.Select(session => session.Key), Is.EqualTo(new[] { "array|chrome", "array|system" }));
            Assert.That(heldBetweenReads, Is.EqualTo(new[] { "FakeCaptureEnumerator x1" }), "only the enumerator is kept between reads");
            Assert.That(_ledger.OverReleased, Is.Empty);
            Assert.That(_enumeratorsMade, Is.EqualTo(1), "the reader did not fail, so its enumerator was not made again");
            Assert.That(_log.Problems, Is.Empty);
        });
    }

    /// <summary>
    /// A microphone that answers E_INVALIDARG when asked for its endpoint volume reads with its mute unknown and its sessions
    /// read; one that answers it when asked for its sessions reads incomplete with its mute read. Either way the read of every
    /// microphone lists it beside the others.
    /// </summary>
    [TestCase("endpoint volume")]
    [TestCase("session manager")]
    public void A_microphone_that_answers_E_INVALIDARG_when_activated_is_still_listed(string what)
    {
        var array = what == "endpoint volume"
            ? new FakeMicrophoneEndpoint(Laptop.ArrayId, Laptop.ArrayName, new FakeCaptureSessionControl("array|chrome", Laptop.ChromeUtility)) { VolumeFailure = FakeResults.InvalidArgument }
            : new FakeMicrophoneEndpoint(Laptop.ArrayId, Laptop.ArrayName, new FakeCaptureSessionControl("array|chrome", Laptop.ChromeUtility)) { SessionManagerFailure = FakeResults.InvalidArgument, Muted = true };
        _enumerator = new FakeCaptureEnumerator(array, _obsbot);

        var all = _reader.ReadAll();

        Assert.That(all.Problem, Is.Null);
        Assert.That(all.Listings.Select(listing => listing.EndpointId), Is.EqualTo(new[] { Laptop.ArrayId, Laptop.ObsbotId }));
        var listing = all.Listings[0];
        Assert.Multiple(() =>
        {
            Assert.That(listing.Problem, Does.Contain("0x80070057"));
            if (what == "endpoint volume")
            {
                Assert.That(listing.MutedInWindows, Is.Null);
                Assert.That((listing.Complete, listing.Sessions.Count), Is.EqualTo((true, 1)), "the sessions were read");
            }
            else
            {
                Assert.That(listing.MutedInWindows, Is.True, "the mute was read");
                Assert.That(listing.Complete, Is.False);
            }
        });
    }

    /// <summary>When the sessions cannot be listed at all, the listing is incomplete with the reason, and the microphone's mute is still read.</summary>
    [Test]
    public void The_session_manager_failing_leaves_the_listing_incomplete()
    {
        var array = new FakeMicrophoneEndpoint(Laptop.ArrayId, Laptop.ArrayName) { SessionManagerFailure = FakeResults.ServiceNotRunning, Muted = true };
        _reader = new WasapiMicrophoneSessions(_names, _log, () => new FakeCaptureEnumerator(array), device => ((FakeMicrophoneEndpoint)device).Name);

        var listing = _reader.Read(Laptop.ArrayId);

        Assert.Multiple(() =>
        {
            Assert.That(listing.Complete, Is.False);
            Assert.That(listing.Problem, Does.Contain("sessions not readable").And.Contain("0x88890010"));
            Assert.That(listing.MutedInWindows, Is.True);
        });
    }

    /// <summary>When the microphone's volume cannot be read, its mute and volume are unknown, with the reason, and its sessions are read all the same.</summary>
    [Test]
    public void The_endpoint_volume_failing_leaves_the_mute_unknown()
    {
        var array = new FakeMicrophoneEndpoint(Laptop.ArrayId, Laptop.ArrayName, new FakeCaptureSessionControl("array|chrome", Laptop.ChromeUtility)) { VolumeFailure = FakeResults.NoInterface };
        _reader = new WasapiMicrophoneSessions(_names, _log, () => new FakeCaptureEnumerator(array), device => ((FakeMicrophoneEndpoint)device).Name);

        var listing = _reader.Read(Laptop.ArrayId);

        Assert.Multiple(() =>
        {
            Assert.That(listing.MutedInWindows, Is.Null);
            Assert.That(listing.VolumePercent, Is.Null);
            Assert.That(listing.Problem, Does.Contain("endpoint volume not available").And.Contain("0x80004002"));
            Assert.That(listing.Complete, Is.True, "the sessions were all read");
            Assert.That(listing.Sessions, Has.Count.EqualTo(1));
        });
    }

    /// <summary>A microphone that is not connected gives a listing that says so, with no sessions and nothing known, without an exception.</summary>
    [Test]
    public void A_microphone_that_is_not_connected_gives_a_problem()
    {
        var listing = _reader.Read("{0.0.1.00000000}.{unplugged}");

        Assert.Multiple(() =>
        {
            Assert.That(listing.EndpointId, Is.EqualTo("{0.0.1.00000000}.{unplugged}"));
            Assert.That(listing.Name, Is.EqualTo("{0.0.1.00000000}.{unplugged}"));
            Assert.That(listing.Complete, Is.False);
            Assert.That(listing.Problem, Is.EqualTo("the microphone is not connected"));
            Assert.That(listing.MutedInWindows, Is.Null);
            Assert.That(listing.Sessions, Is.Empty);
        });
    }

    /// <summary>When the name cannot be read, the identifier stands in for it.</summary>
    [Test]
    public void The_identifier_stands_in_for_a_name_that_cannot_be_read()
    {
        _reader = new WasapiMicrophoneSessions(_names, _log, () => _enumerator, _ => null);

        Assert.That(_reader.Read(Laptop.ArrayId).Name, Is.EqualTo(Laptop.ArrayId));
    }

    /// <summary>
    /// A read that fails as a whole, as while Windows' audio service restarts, gives a listing with the reason and is logged
    /// at Debug; the enumerator is released and made again at the next read, because a restart leaves the old one stale.
    /// </summary>
    [Test]
    public void A_read_that_fails_as_a_whole_gives_a_problem_and_the_enumerator_is_made_again()
    {
        _reader.Read(Laptop.ArrayId);
        _enumerator.Failure = FakeResults.ServiceNotRunning;

        var listing = _reader.Read(Laptop.ArrayId);
        var all = _reader.ReadAll();

        Assert.Multiple(() =>
        {
            Assert.That(listing.Complete, Is.False);
            Assert.That(listing.Problem, Does.Contain("0x88890010"));
            Assert.That(all.Listings, Is.Empty);
            Assert.That(all.Problem, Does.Contain("0x88890010"), "a list that could not be read says why, so that it is not taken for every microphone gone");
            Assert.That(_ledger.Unreleased, Is.Empty, "the enumerator was released with the failure");
            Assert.That(_log.Problems, Is.Empty, "a failing look is noted at Debug, not warned about four times a second");
            Assert.That(_log.Messages, Has.Some.Contains("0x88890010"));
        });

        _enumerator.Failure = 0;
        var again = _reader.Read(Laptop.ArrayId);

        Assert.Multiple(() =>
        {
            Assert.That(again.Complete, Is.True);
            Assert.That(_enumeratorsMade, Is.EqualTo(3), "made at the first read, again for the read of every microphone after the failure, and again for the read after that one failed");
        });
    }

    /// <summary>A reader that was never used disposes without making an enumerator.</summary>
    [Test]
    public void Disposing_an_unused_reader_makes_no_enumerator()
    {
        _reader.Dispose();

        Assert.That(_enumeratorsMade, Is.Zero);
    }
}

/// <summary>
/// The reader against the real Windows audio stack: it lists the microphones and their sessions, owns no session itself,
/// and leaves no handles behind. Nothing is opened, played or recorded.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsAudio")]
[SupportedOSPlatform("windows")]
public sealed class MicrophoneSessionsWindowsTests
{
    /// <summary>The most handles a run of reads may gain: a few for the runtime's own work, none per read.</summary>
    private const int HandleAllowance = 20;

    /// <summary>Every active microphone is listed from Windows, with its name and its sessions, and none of the sessions is this process's: reading opens nothing.</summary>
    [Test]
    public void The_microphones_are_listed_and_this_process_owns_no_session()
    {
        using var reader = new WasapiMicrophoneSessions(new ProcessNames(), NullLogger<WasapiMicrophoneSessions>.Instance);

        var listings = reader.ReadAll().Listings;
        Assume.That(listings, Is.Not.Empty, "This computer has no active microphone.");

        Assert.Multiple(() =>
        {
            foreach (var listing in listings)
            {
                Assert.That(listing.Problem, Is.Null, listing.EndpointId);
                Assert.That(listing.Complete, Is.True, listing.EndpointId);
                Assert.That(listing.Name, Is.Not.Empty.And.Not.EqualTo(listing.EndpointId), "the name is read from the property store");
                Assert.That(listing.Sessions.Select(session => session.ProcessId), Has.None.EqualTo(Environment.ProcessId), "this process owns no session");
            }

            var first = reader.Read(listings[0].EndpointId);
            Assert.That(first.Name, Is.EqualTo(listings[0].Name));
            Assert.That(first.DefaultFor, Is.EqualTo(listings[0].DefaultFor));
        });
    }

    /// <summary>A microphone that does not exist gives a listing with the reason, without an exception.</summary>
    [Test]
    public void A_microphone_that_does_not_exist_gives_a_problem()
    {
        using var reader = new WasapiMicrophoneSessions(new ProcessNames(), NullLogger<WasapiMicrophoneSessions>.Instance);

        var listing = reader.Read("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}");

        Assert.Multiple(() =>
        {
            Assert.That(listing.Complete, Is.False);
            Assert.That(listing.Problem, Is.Not.Null);
        });
    }

    /// <summary>Four hundred reads of every microphone (100 seconds of a class) leave the handle count where it was.</summary>
    [Test]
    public void Reading_leaves_no_handles_behind()
    {
        using var reader = new WasapiMicrophoneSessions(new ProcessNames(), NullLogger<WasapiMicrophoneSessions>.Instance);
        Assume.That(reader.ReadAll().Listings, Is.Not.Empty, "This computer has no active microphone.");
        for (var i = 0; i < 10; i++) reader.ReadAll();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.HandleCount;

        for (var i = 0; i < 400; i++) reader.ReadAll();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();

        Assert.That(process.HandleCount - before, Is.LessThan(HandleAllowance), "handles left open by 400 reads");
    }
}
