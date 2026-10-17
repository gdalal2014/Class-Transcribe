using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace ClassScribe.Tests;

/// <summary>
/// What the microphone provider lists, how it reads a microphone's mute, how it tells of changes, and how it copes
/// when Windows fails it, with Windows' enumerator, endpoints, property stores and endpoint volumes played by fakes
/// (see TestDoubles.MicrophoneListing.cs). Making the provider still registers for Windows' device notifications, so
/// these run on Windows; they need no microphone, and none of them opens one. The last test lists this computer's
/// real microphones from their property stores, which opens nothing either.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsAudio")]
[SupportedOSPlatform("windows")]
public sealed class MicrophoneListingTests
{
    private static readonly AudioFormat Mono48k = new(48_000, 1, 32, IsFloat: true);

    private RecordingLogger<WasapiMicrophoneProvider> _log = null!;
    private RecordingCaptureFactory _captures = null!;

    /// <summary>Creates a log to inspect and a factory that keeps account of the captures asked for.</summary>
    [SetUp]
    public void SetUp()
    {
        _log = new RecordingLogger<WasapiMicrophoneProvider>();
        _captures = new RecordingCaptureFactory();
    }

    /// <summary>
    /// Every active microphone is listed from its property store with its name, bus and what the choice of microphone
    /// needs to know about it, and the one Windows names as the default for the Console role is the default.
    /// </summary>
    [Test]
    public void The_active_microphones_are_listed_from_their_property_stores_with_the_console_default()
    {
        using var provider = Provider(new FakeMicrophoneEnumerator(LaptopEndpoints.All()) { DefaultId = LaptopEndpoints.ArrayId });

        Assert.That(provider.GetMicrophones(), Is.EqualTo(new[]
        {
            new Microphone(LaptopEndpoints.ArrayId, LaptopEndpoints.ArrayName, IsDefault: true, IsBuiltInArray: true, IsBluetooth: false, IsLoopbackInput: false, RawSupported: true, "INTELAUDIO", null),
            new Microphone(LaptopEndpoints.PixelBudsId, "Headset (2- Pixel Buds Pro 2)", IsDefault: false, IsBuiltInArray: false, IsBluetooth: true, IsLoopbackInput: false, RawSupported: false, "BTHLE", "enumerator BTHLE"),
            new Microphone(LaptopEndpoints.SoundcoreId, "Headset (soundcore Liberty 5 Pro)", IsDefault: false, IsBuiltInArray: false, IsBluetooth: true, IsLoopbackInput: false, RawSupported: false, "INTELAUDIO",
                $"{LaptopEndpoints.IntelDriverKey} = {LaptopEndpoints.SoundcoreDriver}"),
            new Microphone(LaptopEndpoints.ObsbotId, "OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)", IsDefault: false, IsBuiltInArray: false, IsBluetooth: false, IsLoopbackInput: false, RawSupported: true, "USB", null),
            new Microphone(LaptopEndpoints.StereoMixId, "Stereo Mix (Realtek(R) Audio)", IsDefault: false, IsBuiltInArray: false, IsBluetooth: false, IsLoopbackInput: true, RawSupported: false, "INTELAUDIO", null)
        }));
    }

    /// <summary>Only active microphones are listed: not one that is disabled, unplugged or not present, as four of this laptop's five usually are.</summary>
    [Test]
    public void Only_active_microphones_are_listed()
    {
        var endpoints = new[]
        {
            LaptopEndpoints.Array(),
            new FakeCaptureEndpoint(LaptopEndpoints.PixelBudsId, "Headset (2- Pixel Buds Pro 2)", "BTHLE") { State = FakeCaptureEndpoint.Unplugged },
            new FakeCaptureEndpoint(LaptopEndpoints.ObsbotId, "OBSBOT Meet Flip Microphone", "USB") { State = FakeCaptureEndpoint.NotPresent },
            new FakeCaptureEndpoint(LaptopEndpoints.StereoMixId, "Stereo Mix (Realtek(R) Audio)") { State = FakeCaptureEndpoint.Disabled }
        };
        using var provider = Provider(new FakeMicrophoneEnumerator(endpoints) { DefaultId = LaptopEndpoints.ArrayId });

        Assert.That(provider.GetMicrophones().Select(microphone => microphone.Id), Is.EqualTo(new[] { LaptopEndpoints.ArrayId }));
    }

    /// <summary>With no default microphone, which is Windows' answer when none is set, the microphones are still listed, none as the default.</summary>
    [Test]
    public void Without_a_default_microphone_none_is_the_default()
    {
        using var provider = Provider(new FakeMicrophoneEnumerator(LaptopEndpoints.Array(), LaptopEndpoints.Obsbot()));

        var microphones = provider.GetMicrophones();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(microphones, Has.Count.EqualTo(2));
            Assert.That(microphones.Select(microphone => microphone.IsDefault), Is.All.False);
            Assert.That(_log.Entries.Where(entry => entry.Level == LogLevel.Debug).Select(entry => entry.Message), Does.Contain("Windows reports no default microphone"));
        }
    }

    /// <summary>
    /// A listing asks Windows for the active capture endpoints and the Console role's default, reads property stores, and
    /// asks nothing else of any endpoint: no endpoint volume, no session manager, and never an audio client, which is
    /// what would open a microphone.
    /// </summary>
    [Test]
    public void A_listing_reads_property_stores_and_asks_nothing_else_of_an_endpoint()
    {
        var endpoints = LaptopEndpoints.All();
        var enumerator = new FakeMicrophoneEnumerator(endpoints) { DefaultId = LaptopEndpoints.ArrayId };
        using var provider = Provider(enumerator);

        provider.GetMicrophones();
        provider.GetMicrophones();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumerator.Listings, Is.EqualTo(new[] { (CoreAudio.Capture, CoreAudio.Active), (CoreAudio.Capture, CoreAudio.Active) }));
            Assert.That(enumerator.DefaultsAsked, Is.EqualTo(new[] { (CoreAudio.Capture, CoreAudio.Console), (CoreAudio.Capture, CoreAudio.Console) }));
            Assert.That(endpoints.SelectMany(endpoint => endpoint.Activated), Is.Empty, "interfaces activated on an endpoint");
        }
    }

    /// <summary>
    /// A microphone whose properties Windows does not give, as one unplugged during the look, is left out with a Debug
    /// line, and the others are still listed: one such microphone must not fail the whole look.
    /// </summary>
    [Test]
    public void A_microphone_whose_properties_cannot_be_read_is_left_out_and_the_others_are_listed()
    {
        var gone = LaptopEndpoints.Obsbot();
        gone.Store.Failure = FakeResults.DeviceInvalidated;
        using var provider = Provider(new FakeMicrophoneEnumerator(LaptopEndpoints.Array(), gone, LaptopEndpoints.StereoMix()) { DefaultId = LaptopEndpoints.ArrayId });

        var microphones = provider.GetMicrophones();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(microphones.Select(microphone => microphone.Id), Is.EqualTo(new[] { LaptopEndpoints.ArrayId, LaptopEndpoints.StereoMixId }));
            Assert.That(_log.Entries.Where(entry => entry.Level == LogLevel.Debug).Select(entry => entry.Message),
                Does.Contain("Left out a microphone whose properties Windows did not give (0x88890004)"));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>
    /// Whether a microphone is muted in Windows is read from its endpoint volume, the one object asked of it, and only
    /// read: the fake fails the test if anything is set or any notification registered.
    /// </summary>
    [Test]
    public void The_mute_is_read_from_the_endpoint_volume_and_nothing_else_is_asked_of_it()
    {
        var array = LaptopEndpoints.Array();
        var obsbot = LaptopEndpoints.Obsbot();
        obsbot.Muted = true;
        using var provider = Provider(new FakeMicrophoneEnumerator(array, obsbot) { DefaultId = LaptopEndpoints.ArrayId });

        var arrayMuted = provider.IsMuted(LaptopEndpoints.ArrayId);
        var obsbotMuted = provider.IsMuted(LaptopEndpoints.ObsbotId);
        array.Muted = true;
        var arrayMutedNow = provider.IsMuted(LaptopEndpoints.ArrayId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(arrayMuted, Is.False);
            Assert.That(obsbotMuted, Is.True);
            Assert.That(arrayMutedNow, Is.True, "read afresh each time");
            Assert.That(array.Activated, Is.EqualTo(new[] { typeof(IAudioEndpointVolumeRaw).GUID, typeof(IAudioEndpointVolumeRaw).GUID }));
            Assert.That(obsbot.Activated, Is.EqualTo(new[] { typeof(IAudioEndpointVolumeRaw).GUID }));
        }
    }

    /// <summary>
    /// The mute is not known for a microphone Windows no longer lists, for one whose endpoint volume cannot be made, for
    /// one whose endpoint volume will not answer, and for one that offers no endpoint volume at all (E_NOINTERFACE, which
    /// reaches the provider as an InvalidCastException rather than a COMException): each reads as null, noted at Debug,
    /// never as unmuted.
    /// </summary>
    [Test]
    public void The_mute_is_unknown_for_a_microphone_that_is_gone_or_will_not_answer()
    {
        var noVolume = new FakeCaptureEndpoint("{no-volume}", "Microphone (USB)", "USB") { VolumeFailure = FakeResults.DeviceInvalidated, Muted = true };
        var noAnswer = new FakeCaptureEndpoint("{no-answer}", "Microphone (USB)", "USB") { MuteFailure = FakeResults.DeviceInvalidated, Muted = true };
        var noInterface = new FakeCaptureEndpoint("{no-interface}", "Microphone (USB)", "USB") { VolumeFailure = FakeResults.NoInterface, Muted = true };
        using var provider = Provider(new FakeMicrophoneEnumerator(noVolume, noAnswer, noInterface));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.IsMuted("{gone}"), Is.Null, "a microphone Windows no longer lists");
            Assert.That(provider.IsMuted("{no-volume}"), Is.Null, "no endpoint volume");
            Assert.That(provider.IsMuted("{no-answer}"), Is.Null, "no answer from the endpoint volume");
            Assert.That(provider.IsMuted("{no-interface}"), Is.Null, "an endpoint that offers no endpoint volume");
            Assert.That(_log.Entries.Where(entry => entry.Level == LogLevel.Debug).Select(entry => entry.Message), Is.EqualTo(new[]
            {
                "Windows no longer lists the microphone {gone}, so whether it is muted is not known",
                "Could not read whether the microphone {no-volume} is muted (0x88890004)",
                "Could not read whether the microphone {no-answer} is muted (0x88890004)",
                "Could not read whether the microphone {no-interface} is muted (0x80004002)"
            }));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>
    /// A look that fails as a whole, as every look does while Windows' audio service restarts, is noted at Debug and
    /// reaches the caller: the listing by its exception, since the caller looks again, and the mute as not known. The
    /// enumerator is kept through the failures and used again once Windows answers, because Windows keeps one per process.
    /// </summary>
    [Test]
    public void A_failed_look_is_noted_at_debug_and_the_enumerator_is_kept_until_Windows_answers()
    {
        var enumerator = new FakeMicrophoneEnumerator(LaptopEndpoints.Array()) { DefaultId = LaptopEndpoints.ArrayId, Failure = FakeResults.ServiceNotRunning };
        var made = 0;
        using var provider = new WasapiMicrophoneProvider(_captures, _log, () =>
        {
            made++;
            return enumerator;
        }, FakeCaptureEndpoint.OpenStore);

        for (var i = 0; i < 5; i++)
        {
            var failure = Assert.Throws<COMException>(() => provider.GetMicrophones());
            Assert.That(failure!.HResult, Is.EqualTo(FakeResults.ServiceNotRunning));
            Assert.That(provider.IsMuted(LaptopEndpoints.ArrayId), Is.Null);
        }

        enumerator.Failure = FakeResults.Ok;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.GetMicrophones().Select(microphone => microphone.Name), Is.EqualTo(new[] { LaptopEndpoints.ArrayName }), "listed once Windows answers again");
            Assert.That(provider.IsMuted(LaptopEndpoints.ArrayId), Is.False);
            Assert.That(made, Is.EqualTo(1), "enumerators made");
            Assert.That(_log.Entries.Where(entry => entry.Level == LogLevel.Debug).Select(entry => entry.Message).Take(2), Is.EqualTo(new[]
            {
                "Could not look at the microphones (0x88890010)",
                "Could not read whether the microphone {0.0.1.00000000}.{array} is muted (0x88890010)"
            }));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>An enumerator Windows will not make yet, as while its audio service starts, is asked for again at the next look.</summary>
    [Test]
    public void An_enumerator_that_cannot_be_made_is_asked_for_again_at_the_next_look()
    {
        var made = 0;
        using var provider = new WasapiMicrophoneProvider(_captures, _log, () =>
        {
            if (++made <= 2) throw new COMException("Windows' audio service is not running.", FakeResults.ServiceNotRunning);
            return new FakeMicrophoneEnumerator(LaptopEndpoints.Array()) { DefaultId = LaptopEndpoints.ArrayId };
        }, FakeCaptureEndpoint.OpenStore);

        Assert.Throws<COMException>(() => provider.GetMicrophones());
        Assert.That(provider.IsMuted(LaptopEndpoints.ArrayId), Is.Null);

        Assert.That(provider.GetMicrophones().Select(microphone => microphone.Name), Is.EqualTo(new[] { LaptopEndpoints.ArrayName }));
        Assert.That(made, Is.EqualTo(3));
    }

    /// <summary>
    /// Every object Windows hands the looks is released once, as soon as it has been used: the default microphone, the
    /// listing, each endpoint and its property store, and for the mute the endpoint and its endpoint volume, also when
    /// a store fails or the endpoint volume cannot be made. Only the enumerator is kept between looks, until Dispose.
    /// </summary>
    [Test]
    public void Every_object_the_looks_get_from_Windows_is_released_once()
    {
        var ledger = new FakeComLedger();
        var endpoints = LaptopEndpoints.All(ledger);
        endpoints[3].Store.Failure = FakeResults.DeviceInvalidated;
        var noVolume = new FakeCaptureEndpoint("{no-volume}", "Microphone (USB)", "USB") { Ledger = ledger, VolumeFailure = FakeResults.DeviceInvalidated };
        var provider = new WasapiMicrophoneProvider(_captures, _log,
            () => ledger.HandOut(new FakeMicrophoneEnumerator([.. endpoints, noVolume]) { DefaultId = LaptopEndpoints.ArrayId, Ledger = ledger }),
            FakeCaptureEndpoint.OpenStore, ledger.Release);

        provider.GetMicrophones();
        provider.IsMuted(LaptopEndpoints.ArrayId);
        provider.IsMuted("{no-volume}");
        var heldBetweenLooks = ledger.Unreleased;
        provider.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ledger.HandedOut, Is.EqualTo(18), "objects handed out: the enumerator, 14 for the listing (the default, the collection, six endpoints and their stores), 3 for the mutes");
            Assert.That(heldBetweenLooks, Is.EqualTo(new[] { "FakeMicrophoneEnumerator x1" }), "held between looks");
            Assert.That(ledger.Unreleased, Is.Empty, "held after Dispose");
            Assert.That(ledger.OverReleased, Is.Empty, "released without being held");
        }
    }

    /// <summary>
    /// Disposing while Windows holds up a look, as it can while its audio service restarts, does not wait for the look:
    /// the app exits at once, and the look releases the enumerator itself as it ends, so nothing is held afterwards and
    /// nothing is released twice. The same promise the device check's provider makes, pinned here for the microphones.
    /// </summary>
    [Test]
    public void Disposing_during_a_look_Windows_holds_up_does_not_wait_and_the_look_releases_the_enumerator()
    {
        var ledger = new FakeComLedger();
        using var reached = new ManualResetEventSlim();
        using var holdUp = new ManualResetEventSlim();
        var array = LaptopEndpoints.Array();
        array.Ledger = ledger;
        var provider = new WasapiMicrophoneProvider(_captures, _log,
            () => ledger.HandOut(new FakeMicrophoneEnumerator(array) { DefaultId = LaptopEndpoints.ArrayId, Ledger = ledger }),
            device =>
            {
                reached.Set();
                holdUp.Wait();
                return FakeCaptureEndpoint.OpenStore(device);
            },
            ledger.Release);

        var look = Task.Run(provider.GetMicrophones);
        var disposing = Task.CompletedTask;
        var disposedPromptly = false;
        IReadOnlyList<string> heldWhileLooking = [];
        try
        {
            Assert.That(reached.Wait(TimeSpan.FromSeconds(5)), Is.True, "the look reached Windows");
            disposing = Task.Run(provider.Dispose);
            disposedPromptly = disposing.Wait(TimeSpan.FromSeconds(5));
            heldWhileLooking = ledger.Unreleased;
        }
        finally
        {
            holdUp.Set();
        }

        var bothEnded = Task.WaitAll([look, disposing], TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disposedPromptly, Is.True, "Dispose returned while the look was held up");
            Assert.That(heldWhileLooking, Does.Contain("FakeMicrophoneEnumerator x1"), "held while the look ran");
            Assert.That(bothEnded, Is.True, "the look and Dispose ended");
            Assert.That(ledger.Unreleased, Is.Empty, "held after the look ended");
            Assert.That(ledger.OverReleased, Is.Empty, "released twice");
        }
    }

    /// <summary>
    /// A change among microphones is told as Windows tells it: a new default microphone, a microphone added, removed or
    /// changing state. A new default output device is not a change among microphones, and the other roles' defaults
    /// follow the Console role's, which is the one the listing reports; a property changing is nothing to tell.
    /// </summary>
    [Test]
    public void A_change_among_microphones_raises_the_event()
    {
        using var provider = Provider(new FakeMicrophoneEnumerator(LaptopEndpoints.Array()));
        var raised = 0;
        provider.MicrophonesChanged += (_, _) => raised++;

        provider.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, LaptopEndpoints.ArrayId);
        var afterNewDefault = raised;
        provider.OnDefaultDeviceChanged(DataFlow.Capture, Role.Communications, LaptopEndpoints.ArrayId);
        provider.OnDefaultDeviceChanged(DataFlow.Capture, Role.Multimedia, LaptopEndpoints.ArrayId);
        provider.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{speakers}");
        provider.OnPropertyValueChanged(LaptopEndpoints.ArrayId, default);
        var afterNothingToTell = raised;
        provider.OnDeviceStateChanged(LaptopEndpoints.ObsbotId, DeviceState.Active);
        provider.OnDeviceAdded(LaptopEndpoints.ObsbotId);
        provider.OnDeviceRemoved(LaptopEndpoints.ObsbotId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterNewDefault, Is.EqualTo(1), "a new default microphone");
            Assert.That(afterNothingToTell, Is.EqualTo(1), "other roles, output devices and properties");
            Assert.That(raised, Is.EqualTo(4), "a state change, an addition and a removal");
        }
    }

    /// <summary>A capture of a microphone is opened through the capture factory, by the microphone's identifier and name, with the processing asked for.</summary>
    [Test]
    public void A_capture_is_opened_through_the_factory()
    {
        using var provider = Provider(new FakeMicrophoneEnumerator(LaptopEndpoints.Array()) { DefaultId = LaptopEndpoints.ArrayId });
        var array = provider.GetMicrophones().Single();

        using var capture = provider.CreateCapture(array, MicrophoneProcessing.Raw);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capture, Is.SameAs(_captures.Opened.Single().Capture));
            Assert.That(_captures.Opened.Single().EndpointId, Is.EqualTo(LaptopEndpoints.ArrayId));
            Assert.That(_captures.Opened.Single().Name, Is.EqualTo(LaptopEndpoints.ArrayName));
            Assert.That(_captures.Opened.Single().Processing, Is.EqualTo(MicrophoneProcessing.Raw));
        }
    }

    /// <summary>Once disposed, the provider lists nothing, knows no mute, and asks Windows for nothing.</summary>
    [Test]
    public void Once_disposed_nothing_is_listed_and_no_mute_is_known()
    {
        var made = 0;
        var provider = new WasapiMicrophoneProvider(_captures, _log, () =>
        {
            made++;
            return new FakeMicrophoneEnumerator(LaptopEndpoints.Array()) { DefaultId = LaptopEndpoints.ArrayId };
        }, FakeCaptureEndpoint.OpenStore);

        provider.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.GetMicrophones(), Is.Empty);
            Assert.That(provider.IsMuted(LaptopEndpoints.ArrayId), Is.Null);
            Assert.That(made, Is.Zero);
        }
    }

    /// <summary>
    /// Windows keeps only a plain pointer to the provider for its device notifications, so the provider keeps itself
    /// alive while it is registered, and lets go once it is disposed.
    /// </summary>
    [Test]
    public void The_provider_stays_alive_while_windows_may_call_it_and_is_let_go_after()
    {
        var provider = CreateUnreferenced();
        Collect();
        var aliveWhileRegistered = provider.IsAlive;
        DisposeTarget(provider);
        Collect();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aliveWhileRegistered, Is.True, "collected while Windows could still call it");
            Assert.That(provider.IsAlive, Is.False, "still held after it was disposed");
        }
    }

    /// <summary>
    /// This computer's microphones are listed from their property stores alone: each with an identifier, a name and a bus,
    /// at most one the default, the built-in array recognised when there is one, and the default's mute read. Afterwards
    /// this process owns no capture session on any microphone, which is the proof that nothing was opened: an audio client
    /// would have made one. Nothing is played or recorded.
    /// </summary>
    [Test]
    public void The_real_microphones_are_listed_from_property_stores_alone_and_the_array_is_recognised()
    {
        using var provider = new WasapiMicrophoneProvider(_captures, _log);

        var microphones = provider.GetMicrophones();
        Assume.That(microphones, Is.Not.Empty, "this computer has no active microphone");
        var mutes = microphones.ToDictionary(microphone => microphone.Id, microphone => provider.IsMuted(microphone.Id));
        foreach (var microphone in microphones)
            TestContext.Out.WriteLine($"{microphone} muted: {mutes[microphone.Id]?.ToString() ?? "unknown"}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(microphones, Has.All.Matches<Microphone>(microphone => microphone.Id.Length > 0 && microphone.Name.Length > 0 && microphone.Enumerator.Length > 0));
            Assert.That(microphones.Count(microphone => microphone.IsDefault), Is.LessThanOrEqualTo(1), "defaults");
            Assert.That(microphones.Where(microphone => microphone.Name.Contains("Microphone Array", StringComparison.OrdinalIgnoreCase)).Select(microphone => microphone.IsBuiltInArray),
                Is.All.True, "an array named as one is recognised by its jack subtype");
            Assert.That(microphones.Where(microphone => microphone.IsDefault).Select(microphone => mutes[microphone.Id]), Is.All.Not.Null, "the default's mute is read");
            Assert.That(CaptureSessionsOfThisProcess(), Is.Empty, "capture sessions this process owns after listing");
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>A provider whose looks use <paramref name="enumerator"/>, opening the fakes' stores, with the test's capture factory and log.</summary>
    private WasapiMicrophoneProvider Provider(FakeMicrophoneEnumerator enumerator) =>
        new(_captures, _log, () => enumerator, FakeCaptureEndpoint.OpenStore);

    /// <summary>Creates a provider that only a weak reference points to. Not inlined, so no hidden local keeps it alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference CreateUnreferenced() => new(new WasapiMicrophoneProvider(_captures, _log));

    /// <summary>Disposes the provider behind a weak reference, if it is still there. Not inlined, for the same reason.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeTarget(WeakReference reference)
    {
        if (reference.Target is IDisposable target) target.Dispose();
    }

    /// <summary>Runs the garbage collector until everything collectable is gone.</summary>
    private static void Collect()
    {
        for (var round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// The identifiers of the active microphones on which this process has an audio session, read through ClassScribe's
    /// own declarations, every object released. An app that opened a microphone has a session on it; one that only read
    /// property stores and the endpoint volume has none.
    /// </summary>
    private static List<string> CaptureSessionsOfThisProcess()
    {
        var owned = new List<string>();
        var enumerator = CoreAudio.CreateEnumerator();
        try
        {
            CoreAudio.Check(enumerator.EnumAudioEndpoints(CoreAudio.Capture, CoreAudio.Active, out var collection));
            try
            {
                CoreAudio.Check(collection.GetCount(out var count));
                for (var i = 0; i < count; i++)
                {
                    CoreAudio.Check(collection.Item(i, out var device));
                    try
                    {
                        CoreAudio.Check(device.GetId(out var id));
                        if (HasSessionOfThisProcess(device)) owned.Add(id);
                    }
                    finally
                    {
                        CoreAudio.Release(device);
                    }
                }
            }
            finally
            {
                CoreAudio.Release(collection);
            }
        }
        finally
        {
            CoreAudio.Release(enumerator);
        }

        return owned;
    }

    /// <summary>Whether any audio session on <paramref name="device"/> belongs to this process.</summary>
    private static bool HasSessionOfThisProcess(IMMDeviceRaw device)
    {
        var managerId = typeof(IAudioSessionManager2Raw).GUID;
        CoreAudio.Check(device.Activate(ref managerId, CoreAudio.AllContexts, IntPtr.Zero, out var manager));
        try
        {
            CoreAudio.Check(((IAudioSessionManager2Raw)manager).GetSessionEnumerator(out var sessions));
            try
            {
                CoreAudio.Check(sessions.GetCount(out var count));
                for (var i = 0; i < count; i++)
                {
                    CoreAudio.Check(sessions.GetSession(i, out var session));
                    try
                    {
                        var control = (IAudioSessionControl2Raw)session;
                        if (control.GetProcessId(out var processId) >= 0 && processId == (uint)Environment.ProcessId) return true;
                    }
                    finally
                    {
                        CoreAudio.Release(session);
                    }
                }

                return false;
            }
            finally
            {
                CoreAudio.Release(sessions);
            }
        }
        finally
        {
            CoreAudio.Release(manager);
        }
    }

    /// <summary>A capture factory that keeps account of what it was asked to open, and hands out a fake capture each time.</summary>
    private sealed class RecordingCaptureFactory : IMicrophoneCaptureFactory
    {
        /// <summary>The captures opened, each with what was asked for and the capture handed out.</summary>
        public List<(string EndpointId, string Name, MicrophoneProcessing Processing, FakeAudioCapture Capture)> Opened { get; } = [];

        /// <inheritdoc />
        public IAudioCapture Open(string endpointId, string name, MicrophoneProcessing processing)
        {
            var capture = new FakeAudioCapture(Mono48k, name, endpointId);
            Opened.Add((endpointId, name, processing, capture));
            return capture;
        }
    }
}
