using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// Runs the recorder's once-a-second device check many times against the real Windows audio stack and checks
/// that it leaves nothing behind for the garbage collector: no registry key handles, no native memory. It only
/// reads Windows' device list, level meters and audio sessions; nothing is played or recorded. The loops run in
/// a no-GC region, so a collection that happens to run cannot hide what a check leaves behind.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsAudio")]
[SupportedOSPlatform("windows")]
public sealed class DeviceCheckResourceTests
{
    /// <summary>The most handles a run of checks may gain: a few for the runtime's own work, none per check.</summary>
    private const int HandleAllowance = 20;

    /// <summary>The most native memory, in megabytes, a run of checks may gain.</summary>
    private const double NativeMegabyteAllowance = 5;

    /// <summary>
    /// A thousand looks at the output devices (17 minutes of recording) leave the handle count and the
    /// native memory where they were.
    /// </summary>
    [Test]
    public void Listing_output_devices_leaves_no_handles_or_native_memory_behind()
    {
        using var provider = new WasapiLoopbackCaptureProvider(NullLogger<WasapiLoopbackCaptureProvider>.Instance);
        Assume.That(provider.GetOutputDevices(), Is.Not.Empty, "This computer has no active output device.");

        var (handles, nativeBytes) = Measure(() => provider.GetOutputDevices(), times: 1000);

        Assert.Multiple(() =>
        {
            Assert.That(handles, Is.LessThan(HandleAllowance), "handles left open by 1000 device listings");
            Assert.That(nativeBytes / 1048576.0, Is.LessThan(NativeMegabyteAllowance), "MB of native memory left by 1000 device listings");
        });
    }

    /// <summary>
    /// Three hundred looks at the apps playing sound (5 minutes of recording an app) leave the handle count
    /// and the native memory where they were.
    /// </summary>
    [Test]
    public void Listing_playing_apps_leaves_no_handles_or_native_memory_behind()
    {
        using var provider = new WasapiLoopbackCaptureProvider(NullLogger<WasapiLoopbackCaptureProvider>.Instance);
        Assume.That(provider.GetOutputDevices(), Is.Not.Empty, "This computer has no active output device.");
        provider.GetPlayingApps();

        var (handles, nativeBytes) = Measure(() => provider.GetPlayingApps(), times: 300);

        Assert.Multiple(() =>
        {
            Assert.That(handles, Is.LessThan(HandleAllowance), "handles left open by 300 app listings");
            Assert.That(nativeBytes / 1048576.0, Is.LessThan(NativeMegabyteAllowance), "MB of native memory left by 300 app listings");
        });
    }

    /// <summary>
    /// Runs <paramref name="check"/> the given number of times with garbage collection held off, and returns how
    /// many handles the process gained and how much its private memory grew. The no-GC region reserves the
    /// managed heap's room before the first reading, so the growth is memory outside the managed heap. The first
    /// checks pay for caches of COM and the C runtime, whose size moves with what else the machine is doing (a
    /// full test run beside other builds), so <paramref name="warmUp"/> checks are made before the baseline is
    /// read, and the growth is measured over the checks after them.
    /// </summary>
    private static (int Handles, long NativeBytes) Measure(Action check, int times, int warmUp = 100)
    {
        for (var i = 0; i < warmUp; i++) check();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assume.That(GC.TryStartNoGCRegion(200L * 1024 * 1024), Is.True, "The garbage collector would not hold off.");
        try
        {
            var handlesBefore = HandleCount();
            var privateBefore = PrivateBytes();
            for (var i = 0; i < times; i++) check();
            var (handles, nativeBytes) = (HandleCount() - handlesBefore, PrivateBytes() - privateBefore);

            TestContext.Out.WriteLine($"{times} checks: {handles:+0;-0;0} handles, {nativeBytes / 1048576.0:+0.00;-0.00;0.00} MB of private memory");
            return (handles, nativeBytes);
        }
        finally
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
        }
    }

    /// <summary>The number of handles this process has open.</summary>
    private static int HandleCount()
    {
        if (!GetProcessHandleCount(GetCurrentProcess(), out var count))
            throw new InvalidOperationException($"Windows did not give the handle count (error {Marshal.GetLastPInvokeError()}).");

        return (int)count;
    }

    /// <summary>The private bytes of this process.</summary>
    private static long PrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /// <summary>A pseudo handle for this process, which needs no closing.</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Reads how many handles the process has open.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
}

/// <summary>
/// What the device check lists, and how it copes when Windows fails it, with Windows' enumerator, devices and
/// audio sessions played by fakes (see TestDoubles.CoreAudio.cs). Making the provider still registers for
/// Windows' device notifications, so these run on Windows; they need no output device.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsAudio")]
[SupportedOSPlatform("windows")]
public sealed class DeviceCheckListingTests
{
    /// <summary>The fake speakers' identifier.</summary>
    private const string SpeakersId = "{speakers}";

    /// <summary>The fake headphones' identifier.</summary>
    private const string HeadphonesId = "{headphones}";

    private LevelLogger _log = null!;

    /// <summary>Creates a log to inspect.</summary>
    [SetUp]
    public void SetUp() => _log = new LevelLogger();

    /// <summary>Each active output device is listed with its name, whether it is the default, and its level.</summary>
    [Test]
    public void The_output_devices_are_listed_with_their_names_the_default_and_their_levels()
    {
        using var provider = Provider(() => new FakeDeviceEnumerator(new FakeOutputDevice(SpeakersId, 0.25f), new FakeOutputDevice(HeadphonesId)) { DefaultId = SpeakersId });

        Assert.That(provider.GetOutputDevices(), Is.EqualTo(new[]
        {
            new OutputDevice(SpeakersId, "Speakers", IsDefault: true, Level: 0.25f),
            new OutputDevice(HeadphonesId, "Headphones", IsDefault: false, Level: 0)
        }));
    }

    /// <summary>With no default device, which is Windows' answer when none is connected, the devices are still listed, none as the default.</summary>
    [Test]
    public void Without_a_default_device_the_devices_are_listed_and_none_is_the_default()
    {
        using var provider = Provider(() => new FakeDeviceEnumerator(new FakeOutputDevice(SpeakersId, 0.25f)));

        Assert.That(provider.GetOutputDevices(), Is.EqualTo(new[] { new OutputDevice(SpeakersId, "Speakers", IsDefault: false, Level: 0.25f) }));
    }

    /// <summary>
    /// A level meter that fails, as one of a device unplugged during the look does, reads as silence; the listing
    /// goes on, and the failure is noted at Debug only, since it may come at every look.
    /// </summary>
    [Test]
    public void A_level_meter_that_fails_reads_as_silence()
    {
        var gone = new FakeOutputDevice(HeadphonesId, 0.5f) { MeterFailure = FakeResults.DeviceInvalidated };
        using var provider = Provider(() => new FakeDeviceEnumerator(gone, new FakeOutputDevice(SpeakersId, 0.25f)) { DefaultId = SpeakersId });

        Assert.That(provider.GetOutputDevices().Select(device => (device.Name, device.Level)), Is.EqualTo(new[] { ("Headphones", 0f), ("Speakers", 0.25f) }));
        Assert.That(_log.At(LogLevel.Debug), Has.Some.StartsWith("Could not read the level of an output device"));
    }

    /// <summary>
    /// ClassScribe's own processes are not apps to record: this process, and another ClassScribe such as a second
    /// window, whose capture shows up as a session on the device it records.
    /// </summary>
    [Test]
    public void ClassScribes_own_processes_are_left_out_of_the_apps()
    {
        var processes = new Dictionary<int, string> { [Environment.ProcessId] = "ClassScribe", [4242] = "ClassScribe", [5150] = "chrome" };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f,
            new FakeAudioSession(Environment.ProcessId, 0.5f), new FakeAudioSession(4242, 0.9f), new FakeAudioSession(5150, 0.3f));
        using var provider = Provider(() => new FakeDeviceEnumerator(speakers), processes.GetValueOrDefault);

        Assert.That(provider.GetPlayingApps(), Is.EqualTo(new[] { new PlayingApp(5150, "chrome", "Speakers", 0.3f, SpeakersId) }));
    }

    /// <summary>An app is listed once, at its loudest; expired sessions, Windows' own sounds and processes that have ended are left out.</summary>
    [Test]
    public void An_app_is_listed_once_at_its_loudest_and_only_while_its_sound_is_open()
    {
        var processes = new Dictionary<int, string> { [0] = "Idle", [5150] = "chrome", [5151] = "msedge", [6000] = "Zoom" };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f,
            new FakeAudioSession(5150, 0.1f), new FakeAudioSession(5150, 0.6f),
            new FakeAudioSession(6000, 0.9f, FakeAudioSession.Expired), new FakeAudioSession(0, 0.8f));
        var headphones = new FakeOutputDevice(HeadphonesId, 0.3f,
            new FakeAudioSession(5150, 0.3f), new FakeAudioSession(5151, 0.2f), new FakeAudioSession(7000, 0.7f));
        using var provider = Provider(() => new FakeDeviceEnumerator(speakers, headphones), processes.GetValueOrDefault);

        Assert.That(provider.GetPlayingApps(), Is.EquivalentTo(new[]
        {
            new PlayingApp(5150, "chrome", "Speakers", 0.6f, SpeakersId),
            new PlayingApp(5151, "msedge", "Headphones", 0.2f, HeadphonesId)
        }));
    }

    /// <summary>
    /// A session whose state or process Windows does not give, as with one being torn down during the look, is left
    /// out with a Debug line, and the other apps are still listed: one such session must not fail the whole look.
    /// </summary>
    [Test]
    public void A_session_whose_state_or_process_cannot_be_read_is_left_out_and_the_others_are_listed()
    {
        var processes = new Dictionary<int, string> { [5150] = "chrome", [6000] = "Zoom", [7000] = "Teams" };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f,
            new FakeAudioSession(6000, 0.9f) { StateFailure = FakeResults.DeviceInvalidated },
            new FakeAudioSession(7000, 0.9f) { ProcessIdFailure = FakeResults.DeviceInvalidated },
            new FakeAudioSession(5150, 0.3f));
        using var provider = Provider(() => new FakeDeviceEnumerator(speakers), processes.GetValueOrDefault);

        var apps = provider.GetPlayingApps();

        Assert.Multiple(() =>
        {
            Assert.That(apps, Is.EqualTo(new[] { new PlayingApp(5150, "chrome", "Speakers", 0.3f, SpeakersId) }));
            Assert.That(_log.At(LogLevel.Debug), Is.EqualTo(new[]
            {
                "Left out an audio session whose state Windows did not give (0x88890004)",
                "Left out an audio session whose process Windows did not give (0x88890004)"
            }));
        });
    }

    /// <summary>
    /// As with the Zoom app in a meeting on 18 October, a session that answers E_INVALIDARG to a call the listing makes
    /// of it, its state, its process, or the listing's own call that hands it out, is left out with a Debug line that
    /// gives the result, and every other app is listed. It used to reach the caller as an ArgumentException, which no
    /// catch expected, so the whole look failed. Whatever was handed out is released, and only the enumerator is kept.
    /// </summary>
    [TestCase("GetState")]
    [TestCase("GetProcessId")]
    [TestCase("GetSession")]
    public void A_session_that_answers_E_INVALIDARG_is_left_out_and_the_other_apps_are_listed(string call)
    {
        var ledger = new FakeComLedger();
        var processes = new Dictionary<int, string> { [5150] = "chrome", [6000] = "Zoom" };
        var zoom = call switch
        {
            "GetState" => new FakeAudioSession(6000, 0.9f) { StateFailure = FakeResults.InvalidArgument },
            "GetProcessId" => new FakeAudioSession(6000, 0.9f) { ProcessIdFailure = FakeResults.InvalidArgument },
            _ => new FakeAudioSession(6000, 0.9f) { HandOutFailure = FakeResults.InvalidArgument }
        };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f, zoom, new FakeAudioSession(5150, 0.3f)) { Ledger = ledger };
        var provider = new WasapiLoopbackCaptureProvider(_log, () => ledger.HandOut(new FakeDeviceEnumerator(speakers) { Ledger = ledger }),
            DeviceName, processes.GetValueOrDefault, ledger.Release);
        _log.Clear();

        var apps = provider.GetPlayingApps();
        var heldBetweenListings = ledger.Unreleased;
        provider.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(apps, Is.EqualTo(new[] { new PlayingApp(5150, "chrome", "Speakers", 0.3f, SpeakersId) }));
            Assert.That(_log.At(LogLevel.Debug), Has.Exactly(1).Matches<string>(line => line.StartsWith("Left out an audio session", StringComparison.Ordinal) && line.Contains("(0x80070057)")));
            Assert.That(_log.Messages, Has.Count.EqualTo(1), "a Debug line alone: the look comes once a second");
            Assert.That(heldBetweenListings, Is.EqualTo(new[] { "FakeDeviceEnumerator x1" }), "held between listings");
            Assert.That(ledger.Unreleased, Is.Empty, "held after Dispose");
            Assert.That(ledger.OverReleased, Is.Empty, "released without being held");
        });
    }

    /// <summary>
    /// A session whose level meter answers E_INVALIDARG reads as silence, as a meter that fails always did, and its app is
    /// still listed: recording an app goes through the app's process, never through its meter.
    /// </summary>
    [Test]
    public void A_session_whose_meter_answers_E_INVALIDARG_is_listed_at_silence()
    {
        var processes = new Dictionary<int, string> { [5150] = "chrome", [6000] = "Zoom" };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f,
            new FakeAudioSession(6000, 0.9f) { PeakFailure = FakeResults.InvalidArgument }, new FakeAudioSession(5150, 0.3f));
        using var provider = Provider(() => new FakeDeviceEnumerator(speakers), processes.GetValueOrDefault);

        Assert.That(provider.GetPlayingApps(), Is.EquivalentTo(new[]
        {
            new PlayingApp(6000, "Zoom", "Speakers", 0f, SpeakersId),
            new PlayingApp(5150, "chrome", "Speakers", 0.3f, SpeakersId)
        }));
        Assert.That(_log.At(LogLevel.Debug), Has.Some.StartsWith("Could not read the level of an app's sound"));
    }

    /// <summary>
    /// A device that answers E_INVALIDARG costs only what it was asked for: asked for its level meter, or by its meter, it
    /// is listed at silence; asked for its sessions, its own apps are left out with a Debug line, and the other devices'
    /// apps are listed.
    /// </summary>
    [Test]
    public void A_device_that_answers_E_INVALIDARG_costs_only_its_own_level_or_its_own_apps()
    {
        var processes = new Dictionary<int, string> { [5150] = "chrome", [6000] = "Zoom" };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f, new FakeAudioSession(5150, 0.3f)) { MeterActivationFailure = FakeResults.InvalidArgument };
        var headphones = new FakeOutputDevice(HeadphonesId, 0.25f, new FakeAudioSession(6000, 0.9f))
        {
            MeterFailure = FakeResults.InvalidArgument,
            SessionManagerFailure = FakeResults.InvalidArgument
        };
        using var provider = Provider(() => new FakeDeviceEnumerator(speakers, headphones) { DefaultId = SpeakersId }, processes.GetValueOrDefault);
        _log.Clear();

        var devices = provider.GetOutputDevices();
        var apps = provider.GetPlayingApps();

        Assert.Multiple(() =>
        {
            Assert.That(devices, Is.EqualTo(new[]
            {
                new OutputDevice(SpeakersId, "Speakers", IsDefault: true, Level: 0),
                new OutputDevice(HeadphonesId, "Headphones", IsDefault: false, Level: 0)
            }));
            Assert.That(apps, Is.EqualTo(new[] { new PlayingApp(5150, "chrome", "Speakers", 0.3f, SpeakersId) }));
            Assert.That(_log.At(LogLevel.Debug), Has.Exactly(1).Matches<string>(line => line.Contains("\"Headphones\"") && line.Contains("(0x80070057)")), "the device whose apps were left out");
            Assert.That(_log.At(LogLevel.Information).Concat(_log.At(LogLevel.Warning)), Is.Empty);
        });
    }

    /// <summary>
    /// A process with many sessions, as a browser has, is looked up once per listing, and again at the next
    /// listing, because Windows gives the number of a process that has ended to the next one. This process
    /// needs no look-up at all.
    /// </summary>
    [Test]
    public void Each_process_is_looked_up_once_per_listing()
    {
        var lookups = new List<int>();
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f,
            new FakeAudioSession(5150, 0.1f), new FakeAudioSession(5150, 0.6f), new FakeAudioSession(Environment.ProcessId, 0.4f), new FakeAudioSession(5151, 0.2f));
        var headphones = new FakeOutputDevice(HeadphonesId, 0.3f, new FakeAudioSession(5150, 0.3f));
        using var provider = Provider(() => new FakeDeviceEnumerator(speakers, headphones), processId =>
        {
            lookups.Add(processId);
            return processId == 5150 ? "chrome" : "msedge";
        });
        lookups.Clear();   // the provider has looked up its own name

        provider.GetPlayingApps();
        provider.GetPlayingApps();

        Assert.That(lookups, Is.EqualTo(new[] { 5150, 5151, 5150, 5151 }));
    }

    /// <summary>
    /// Listings that keep failing, as while Windows' audio service restarts, keep the enumerator, and the first
    /// listing that works again uses it. Windows keeps a single enumerator per process and holds on to it itself,
    /// so making it again would only give the same object back; the factory here does the same. Each failure
    /// reaches the caller, which tries again a second later.
    /// </summary>
    [Test]
    public void The_enumerator_is_kept_through_failed_listings_and_used_again_once_Windows_answers()
    {
        var enumerator = new FakeDeviceEnumerator(new FakeOutputDevice(SpeakersId)) { DefaultId = SpeakersId, Failure = FakeResults.ServiceNotRunning };
        var made = 0;
        using var provider = Provider(() =>
        {
            made++;
            return enumerator;
        });

        for (var i = 0; i < 10; i++)
        {
            Assert.Throws<COMException>(() => provider.GetOutputDevices());
            Assert.Throws<COMException>(() => provider.GetPlayingApps());
        }

        enumerator.Failure = FakeResults.Ok;

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetOutputDevices().Select(device => device.Name), Is.EqualTo(new[] { "Speakers" }), "listed once Windows answers again");
            Assert.That(made, Is.EqualTo(1), "enumerators made");
        });
    }

    /// <summary>
    /// A look that fails is noted at Debug, once, and nothing else is logged: while Windows' audio service is down
    /// every look fails, once a second, and the recording itself warns about it at once and then every 30 seconds.
    /// </summary>
    [Test]
    public void A_failed_look_is_noted_at_debug_only()
    {
        var enumerator = new FakeDeviceEnumerator(new FakeOutputDevice(SpeakersId)) { Failure = FakeResults.ServiceNotRunning };
        using var provider = Provider(() => enumerator);
        _log.Clear();   // what the provider logged as it was made, such as a refused registration for device notifications

        for (var i = 0; i < 10; i++)
        {
            Assert.Throws<COMException>(() => provider.GetOutputDevices());
            Assert.Throws<COMException>(() => provider.GetPlayingApps());
        }

        Assert.Multiple(() =>
        {
            Assert.That(_log.At(LogLevel.Debug), Has.Count.EqualTo(20), "Debug lines, one per failed look");
            Assert.That(_log.Messages, Has.Count.EqualTo(20), "lines at any level");
            Assert.That(_log.At(LogLevel.Debug).Take(2), Is.EqualTo(new[]
            {
                "Could not look at the output devices (0x88890010)",
                "Could not look at the apps with sound (0x88890010)"
            }));
        });
    }

    /// <summary>An enumerator Windows will not make yet, as while its audio service starts, is asked for again at the next listing.</summary>
    [Test]
    public void An_enumerator_that_cannot_be_made_is_asked_for_again_at_the_next_listing()
    {
        var made = 0;
        using var provider = Provider(() =>
        {
            if (++made <= 2) throw new COMException("Windows' audio service is not running.", FakeResults.ServiceNotRunning);
            return new FakeDeviceEnumerator(new FakeOutputDevice(SpeakersId)) { DefaultId = SpeakersId };
        });

        Assert.Throws<COMException>(() => provider.GetOutputDevices());
        Assert.Throws<COMException>(() => provider.GetOutputDevices());

        Assert.That(provider.GetOutputDevices().Select(device => device.Name), Is.EqualTo(new[] { "Speakers" }));
        Assert.That(made, Is.EqualTo(3));
    }

    /// <summary>
    /// Every object Windows hands the listings is released once, as soon as it has been used: the default device,
    /// the device listing, each device, level meter, session manager, session listing and session, also when a
    /// meter fails or a session is left out. Only the enumerator is kept between listings, until Dispose.
    /// </summary>
    [Test]
    public void Every_object_the_listings_get_from_Windows_is_released_once()
    {
        var ledger = new FakeComLedger();
        var processes = new Dictionary<int, string> { [Environment.ProcessId] = "ClassScribe", [4242] = "ClassScribe", [5150] = "chrome" };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f,
            new FakeAudioSession(5150, 0.3f), new FakeAudioSession(5150, 0.6f), new FakeAudioSession(Environment.ProcessId, 0.2f), new FakeAudioSession(4242, 0.2f),
            new FakeAudioSession(6000, 0.9f, FakeAudioSession.Expired), new FakeAudioSession(0, 0.8f), new FakeAudioSession(7000, 0.1f)) { Ledger = ledger };
        var headphones = new FakeOutputDevice(HeadphonesId, 0.5f, new FakeAudioSession(5150, 0.1f)) { Ledger = ledger, MeterFailure = FakeResults.DeviceInvalidated };
        var provider = new WasapiLoopbackCaptureProvider(_log, () => ledger.HandOut(new FakeDeviceEnumerator(speakers, headphones) { DefaultId = SpeakersId, Ledger = ledger }),
            DeviceName, processes.GetValueOrDefault, ledger.Release);

        provider.GetOutputDevices();
        provider.GetPlayingApps();
        var heldBetweenListings = ledger.Unreleased;
        provider.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(ledger.HandedOut, Is.EqualTo(22), "objects handed out: the enumerator, 6 for the devices, 15 for the apps");
            Assert.That(heldBetweenListings, Is.EqualTo(new[] { "FakeDeviceEnumerator x1" }), "held between listings");
            Assert.That(ledger.Unreleased, Is.Empty, "held after Dispose");
            Assert.That(ledger.OverReleased, Is.Empty, "released without being held");
        });
    }

    /// <summary>
    /// A listing that fails part way, here at a device whose identifier cannot be read, still releases what it was
    /// handed. Only the enumerator is kept, through any number of failed listings, until Dispose.
    /// </summary>
    [Test]
    public void A_listing_that_fails_part_way_releases_what_it_was_handed()
    {
        var ledger = new FakeComLedger();
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f, new FakeAudioSession(5150, 0.3f)) { Ledger = ledger };
        var broken = new FakeOutputDevice(HeadphonesId) { Ledger = ledger, IdFailure = FakeResults.DeviceInvalidated };
        var provider = new WasapiLoopbackCaptureProvider(_log, () => ledger.HandOut(new FakeDeviceEnumerator(speakers, broken) { Ledger = ledger }),
            DeviceName, _ => "chrome", ledger.Release);

        for (var i = 0; i < 10; i++)
            Assert.Throws<COMException>(() => provider.GetPlayingApps());
        var heldAfterFailures = ledger.Unreleased;
        provider.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(heldAfterFailures, Is.EqualTo(new[] { "FakeDeviceEnumerator x1" }), "held after ten failed listings");
            Assert.That(ledger.Unreleased, Is.Empty, "held after Dispose");
            Assert.That(ledger.OverReleased, Is.Empty, "released without being held");
        });
    }

    /// <summary>Once disposed, the provider lists nothing, and asks Windows for nothing.</summary>
    [Test]
    public void Once_disposed_nothing_is_listed()
    {
        var made = 0;
        var provider = Provider(() =>
        {
            made++;
            return new FakeDeviceEnumerator(new FakeOutputDevice(SpeakersId, 0.5f, new FakeAudioSession(5150, 0.5f)));
        }, _ => "chrome");

        provider.Dispose();

        Assert.That(provider.GetOutputDevices(), Is.Empty);
        Assert.That(provider.GetPlayingApps(), Is.Empty);
        Assert.That(made, Is.Zero);
    }

    /// <summary>
    /// Disposing while Windows holds up a listing does not wait for it, since the app may be on its way out, and
    /// does not pull the enumerator from under it: the listing releases the enumerator itself as it ends. Every
    /// object the listing was handed, the level meter included, is released once.
    /// </summary>
    [Test]
    public void Disposing_during_a_listing_Windows_holds_up_does_not_wait_and_the_listing_releases_the_enumerator()
    {
        var ledger = new FakeComLedger();
        using var reached = new ManualResetEventSlim();
        using var holdUp = new ManualResetEventSlim();
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f) { Ledger = ledger };
        var provider = new WasapiLoopbackCaptureProvider(_log, () => ledger.HandOut(new FakeDeviceEnumerator(speakers) { DefaultId = SpeakersId, Ledger = ledger }),
            _ =>
            {
                reached.Set();
                holdUp.Wait();
                return "Speakers";
            },
            _ => null, ledger.Release);

        var listing = Task.Run(provider.GetOutputDevices);
        var disposing = Task.CompletedTask;
        var disposedPromptly = false;
        IReadOnlyList<string> heldWhileListing = [];
        try
        {
            Assert.That(reached.Wait(TimeSpan.FromSeconds(5)), Is.True, "the listing reached Windows");
            disposing = Task.Run(provider.Dispose);
            disposedPromptly = disposing.Wait(TimeSpan.FromSeconds(5));
            heldWhileListing = ledger.Unreleased;
        }
        finally
        {
            holdUp.Set();
        }

        var bothEnded = Task.WaitAll([listing, disposing], TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(disposedPromptly, Is.True, "Dispose returned while the listing was held up");
            Assert.That(heldWhileListing, Does.Contain("FakeDeviceEnumerator x1"), "held while the listing ran");
            Assert.That(bothEnded, Is.True, "the listing and Dispose ended");
            Assert.That(ledger.Unreleased, Is.Empty, "held after the listing ended");
            Assert.That(ledger.OverReleased, Is.Empty, "released twice");
        });
    }

    /// <summary>
    /// A provider whose listings use the enumerators <paramref name="enumerators"/> makes, with the fake devices'
    /// names, and with process names from <paramref name="processNames"/> (none when not given).
    /// </summary>
    private WasapiLoopbackCaptureProvider Provider(Func<IMMDeviceEnumeratorRaw> enumerators, Func<int, string?>? processNames = null) =>
        new(_log, enumerators, DeviceName, processNames ?? (_ => null));

    /// <summary>The name Windows would give each fake device.</summary>
    private static string DeviceName(string id) => id switch
    {
        SpeakersId => "Speakers",
        HeadphonesId => "Headphones",
        _ => throw new AssertionException($"No name for the device {id}.")
    };

    /// <summary>
    /// A log that keeps each message with its level, because what the console shows depends on the level: a line
    /// at Information or above every second would fill the window during a class.
    /// </summary>
    private sealed class LevelLogger : ILogger<WasapiLoopbackCaptureProvider>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        /// <summary>Every message logged, whatever its level, in order.</summary>
        public IReadOnlyList<string> Messages => [.. _entries.Select(entry => entry.Message)];

        /// <summary>The messages logged at <paramref name="level"/>, in order.</summary>
        public IReadOnlyList<string> At(LogLevel level) => [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];

        /// <summary>Forgets what was logged so far.</summary>
        public void Clear() => _entries.Clear();

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
