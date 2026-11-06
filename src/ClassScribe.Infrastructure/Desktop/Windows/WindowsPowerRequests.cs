using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ClassScribe.Desktop.Windows;

/// <summary>The kinds of power request Windows knows (POWER_REQUEST_TYPE).</summary>
internal enum PowerRequestType
{
    /// <summary>PowerRequestDisplayRequired: the screen stays on.</summary>
    Display = 0,

    /// <summary>PowerRequestSystemRequired: the system stays in the working state.</summary>
    System = 1,

    /// <summary>PowerRequestAwayModeRequired: away mode instead of sleep. Not used.</summary>
    AwayMode = 2,

    /// <summary>PowerRequestExecutionRequired: the process keeps running.</summary>
    Execution = 3
}

/// <summary>Windows' power request functions, behind an interface so that tests can stand in for Windows.</summary>
internal interface IPowerRequestApi
{
    /// <summary>
    /// Makes a power request with <paramref name="reason"/> as its reason (PowerCreateRequest). When Windows refuses, the
    /// handle is invalid and <paramref name="error"/> holds Windows' error code; otherwise it is 0.
    /// </summary>
    SafeHandle Create(string reason, out int error);

    /// <summary>Sets one kind of request on <paramref name="request"/> (PowerSetRequest): 0 when Windows accepted it, otherwise Windows' error code.</summary>
    int Set(SafeHandle request, PowerRequestType type);

    /// <summary>Clears one kind of request on <paramref name="request"/> (PowerClearRequest): 0 when Windows accepted it, otherwise Windows' error code.</summary>
    int Clear(SafeHandle request, PowerRequestType type);
}

/// <summary>
/// Asks Windows to keep the laptop awake through one power request per hold, which powercfg /requests lists with its
/// reason. A request Windows refuses is logged with advice, and the recording goes on without it. Windows ends every
/// request of the process when the process ends, however it ends.
/// </summary>
/// <remarks>
/// Microsoft documents that on a laptop with Modern Standby that runs on battery, the system and execution requests end
/// a few minutes after the sleep timeout, and that standby starts when the screen turns off; there only the screen's
/// request keeps the laptop awake. On mains the system request keeps it awake for as long as it is held. Closing the lid,
/// the power button and Start > Sleep put the laptop to sleep whatever is asked.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerRequests(TimeProvider time, ILogger<WindowsPowerRequests> logger) : IPowerRequests
{
    /// <summary>How often a hold reads again what it needs, so that the screen follows the power source.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>What to do when Windows refuses a request, for the log.</summary>
    private const string Advice = "keep it plugged in, with sleep set to Never in Settings > System > Power & battery";

    /// <summary>Windows' functions, or a test's stand-in for them.</summary>
    private readonly IPowerRequestApi _api = NativePowerRequestApi.Instance;

    /// <summary>Makes the requests through <paramref name="api"/> instead of Windows' own functions. Tests pass a fake.</summary>
    internal WindowsPowerRequests(IPowerRequestApi api, TimeProvider time, ILogger<WindowsPowerRequests> logger)
        : this(time, logger) => _api = api;

    /// <inheritdoc />
    public IDisposable Hold(string reason, Func<PowerNeeds> needs)
    {
        var request = _api.Create(reason, out var error);
        if (request.IsInvalid)
        {
            request.Dispose();
            logger.LogWarning(
                "Windows did not accept the request to keep the laptop awake ({Error}). Recording goes on, but the laptop may go to sleep if it is left alone: " + Advice,
                Describe(error));
            return new NoHold();
        }

        var hold = new PowerHold(_api, request, needs, logger);
        try
        {
            // Read here, outside the timer, so that a mistake in what is needed shows at once.
            hold.Apply(needs());
            hold.CheckEvery(RecheckInterval, time);
        }
        catch
        {
            hold.Dispose();
            throw;
        }

        logger.LogDebug("Asked Windows to keep the laptop awake: {Reason}", reason);
        return hold;
    }

    /// <summary>Windows' words for an error code, and the code, as in "Access is denied, error 5".</summary>
    private static string Describe(int error) => $"{new Win32Exception(error).Message.TrimEnd('.')}, error {error}";

    /// <summary>What a hold Windows refused gives back: there is nothing to let go.</summary>
    private sealed class NoHold : IDisposable
    {
        /// <summary>Does nothing.</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// One power request and what is set on it. What is needed is read again on a timer; letting the hold go clears what
    /// is set and closes the request. The lock keeps the timer and the release apart.
    /// </summary>
    private sealed class PowerHold(IPowerRequestApi api, SafeHandle request, Func<PowerNeeds> needs, ILogger logger) : IDisposable
    {
        /// <summary>Each need with the kind of request that answers it, in the order they are set; they are cleared in the reverse order.</summary>
        private static readonly (PowerNeeds Need, PowerRequestType Type)[] Kinds =
        [
            (PowerNeeds.System, PowerRequestType.System),
            (PowerNeeds.Execution, PowerRequestType.Execution),
            (PowerNeeds.Display, PowerRequestType.Display)
        ];

        private readonly Lock _gate = new();

        /// <summary>What Windows has accepted and keeps up now.</summary>
        private PowerNeeds _set;

        /// <summary>Whether a refusal to keep the laptop awake, and one to keep the screen on, was logged: each is logged once per hold.</summary>
        private bool _awakeRefused, _screenRefused;

        private ITimer? _recheck;
        private bool _released;

        /// <summary>Sets what is wanted and not yet set, and clears what is set and no longer wanted.</summary>
        public void Apply(PowerNeeds wanted)
        {
            lock (_gate)
            {
                if (_released) return;

                foreach (var (need, type) in Kinds)
                {
                    var want = wanted.HasFlag(need);
                    if (want == _set.HasFlag(need)) continue;

                    if (want)
                        SetLocked(need, type);
                    else
                        ClearLocked(need, type);
                }
            }
        }

        /// <summary>Reads what is needed again every <paramref name="interval"/>, on <paramref name="time"/>'s timer.</summary>
        public void CheckEvery(TimeSpan interval, TimeProvider time)
        {
            lock (_gate)
            {
                if (_released) return;
                _recheck = time.CreateTimer(_ => Recheck(), null, interval, interval);
            }
        }

        /// <summary>Clears every request set, closes the request, and stops the checks. Letting go a second time does nothing.</summary>
        public void Dispose()
        {
            ITimer? recheck;
            lock (_gate)
            {
                if (_released) return;
                _released = true;
                recheck = _recheck;
                _recheck = null;

                for (var kind = Kinds.Length - 1; kind >= 0; kind--)
                {
                    if (_set.HasFlag(Kinds[kind].Need)) ClearLocked(Kinds[kind].Need, Kinds[kind].Type);
                }

                // Closing the handle ends the request for Windows, even a part that would not clear.
                request.Dispose();
            }

            recheck?.Dispose();
            logger.LogDebug("Let Windows put the laptop to sleep again when it is left alone");
        }

        /// <summary>Reads what is needed and applies it. Runs on a timer.</summary>
        private void Recheck()
        {
            try
            {
                lock (_gate)
                {
                    if (_released) return;
                }

                Apply(needs());
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: this runs on a timer, where an exception would end the process, and the
                // recording with it. The requests already set stay as they are, and the next check tries again.
                logger.LogWarning(ex, "Could not check again whether to keep the screen on; the requests stay as they are");
            }
        }

        /// <summary>Sets one kind of request, or logs Windows' refusal. Call with the lock held.</summary>
        private void SetLocked(PowerNeeds need, PowerRequestType type)
        {
            var error = api.Set(request, type);
            if (error == 0)
            {
                _set |= need;
                logger.LogDebug("Windows keeps up {Need} for ClassScribe", need);
                return;
            }

            // Logged once per hold; every later check tries again, so a refusal that passes heals by itself.
            if (need == PowerNeeds.Display ? _screenRefused : _awakeRefused)
            {
                logger.LogDebug("Windows again did not accept the request for {Need} ({Error})", need, Describe(error));
                return;
            }

            if (need == PowerNeeds.Display)
            {
                _screenRefused = true;
                logger.LogWarning(
                    "Windows did not accept the request to keep the screen on ({Error}). Recording goes on, but on battery the laptop may go into standby once the screen turns off: " + Advice,
                    Describe(error));
            }
            else
            {
                _awakeRefused = true;
                logger.LogWarning(
                    "Windows did not accept the request to keep the laptop awake ({Error}). Recording goes on, but the laptop may go to sleep if it is left alone: " + Advice,
                    Describe(error));
            }
        }

        /// <summary>Clears one kind of request. Call with the lock held.</summary>
        private void ClearLocked(PowerNeeds need, PowerRequestType type)
        {
            var error = api.Clear(request, type);

            // No longer counted as set either way: what Windows would not clear ends when the request is closed.
            _set &= ~need;
            if (error == 0)
                logger.LogDebug("Windows no longer keeps up {Need} for ClassScribe", need);
            else
                logger.LogDebug("Windows did not clear the request for {Need} ({Error}); it ends when the request is closed", need, Describe(error));
        }
    }
}

/// <summary>Where the laptop takes its power from, as Windows' GetSystemPowerStatus reports it.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerSource(ILogger<WindowsPowerSource> logger) : IPowerSource
{
    /// <summary>ACLineStatus: on mains.</summary>
    private const byte AcOnline = 1;

    /// <summary>BatteryFlag: the computer has no battery.</summary>
    private const byte NoSystemBattery = 128;

    /// <summary>BatteryFlag: Windows does not know the battery's state.</summary>
    private const byte BatteryUnknown = 255;

    /// <inheritdoc />
    public bool OnBattery
    {
        get
        {
            if (GetSystemPowerStatus(out var status)) return IsOnBattery(status.AcLineStatus, status.BatteryFlag);

            // Not known: counted as battery, where keeping the screen on is what keeps the recording going.
            logger.LogDebug("Windows did not say where the laptop takes its power from (error {Error}); counted as battery", Marshal.GetLastPInvokeError());
            return true;
        }
    }

    /// <summary>
    /// True when Windows' answer means the laptop runs on battery: it is not known to be on mains
    /// (<paramref name="acLineStatus"/> 1) and it has a battery (<paramref name="batteryFlag"/> holds 128 only when it has
    /// none; 255 means the state is not known). Offline, and a state Windows does not know, count as battery, the safer side.
    /// </summary>
    internal static bool IsOnBattery(byte acLineStatus, byte batteryFlag)
    {
        var noBattery = batteryFlag != BatteryUnknown && (batteryFlag & NoSystemBattery) != 0;
        return acLineStatus != AcOnline && !noBattery;
    }

    /// <summary>Windows' SYSTEM_POWER_STATUS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        /// <summary>0 offline, 1 on mains, 255 not known.</summary>
        public byte AcLineStatus;

        /// <summary>Flags for the battery's charge; 128 when there is no battery, 255 when its state is not known.</summary>
        public byte BatteryFlag;

        /// <summary>The charge left, in percent, or 255 when not known.</summary>
        public byte BatteryLifePercent;

        /// <summary>1 while the battery saver is on.</summary>
        public byte SystemStatusFlag;

        /// <summary>Seconds of battery left, or -1 when not known.</summary>
        public uint BatteryLifeTime;

        /// <summary>Seconds of a full battery, or -1 when not known.</summary>
        public uint BatteryFullLifeTime;
    }

    /// <summary>Reads where the laptop takes its power from.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>Windows' own power request functions, from kernel32.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativePowerRequestApi : IPowerRequestApi
{
    /// <summary>POWER_REQUEST_CONTEXT_VERSION.</summary>
    private const uint ContextVersion = 0;

    /// <summary>POWER_REQUEST_CONTEXT_SIMPLE_STRING: the reason is a plain string.</summary>
    private const uint SimpleString = 0x1;

    /// <summary>The one instance; it holds nothing.</summary>
    public static NativePowerRequestApi Instance { get; } = new();

    /// <inheritdoc />
    public SafeHandle Create(string reason, out int error)
    {
        var context = new ReasonContext { Version = ContextVersion, Flags = SimpleString, SimpleReasonString = reason };
        var request = PowerCreateRequest(ref context);
        error = request.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return request;
    }

    /// <inheritdoc />
    public int Set(SafeHandle request, PowerRequestType type) => PowerSetRequest(request, type) ? 0 : Marshal.GetLastPInvokeError();

    /// <inheritdoc />
    public int Clear(SafeHandle request, PowerRequestType type) => PowerClearRequest(request, type) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>Windows' REASON_CONTEXT, with the simple reason string, the one member of its union used here.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        /// <summary>POWER_REQUEST_CONTEXT_VERSION.</summary>
        public uint Version;

        /// <summary>POWER_REQUEST_CONTEXT_SIMPLE_STRING.</summary>
        public uint Flags;

        /// <summary>The reason, as powercfg /requests shows it.</summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    /// <summary>Makes a power request; an invalid handle when Windows refuses.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern PowerRequestHandle PowerCreateRequest(ref ReasonContext context);

    /// <summary>Sets one kind of request on a power request.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeHandle powerRequest, PowerRequestType requestType);

    /// <summary>Clears one kind of request on a power request.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeHandle powerRequest, PowerRequestType requestType);
}

/// <summary>A power request's handle, closed with CloseHandle, which also ends whatever is still set on the request.</summary>
[SupportedOSPlatform("windows")]
internal sealed class PowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>An empty handle, which the call that makes the request fills in.</summary>
    public PowerRequestHandle() : base(ownsHandle: true)
    {
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => CloseHandle(handle);

    /// <summary>Closes a handle Windows gave out.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
