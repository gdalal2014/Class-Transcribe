using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// The computer's clocks as a test plays them, read from the test's clock. The interrupt time counts every moment that
/// clock moves on. The unbiased interrupt time leaves out the moments given to <see cref="Sleep"/>, as Windows leaves out
/// the time the laptop sleeps. So a plain <see cref="ManualTimeProvider.Advance"/> is ClassScribe held up while the laptop
/// stays awake, and <see cref="Sleep"/> is the laptop asleep.
/// </summary>
public sealed class FakeSystemTimes(TimeProvider time) : ISystemTimes
{
    private readonly Lock _gate = new();

    /// <summary>The stretches of the test's steady count during which the laptop slept, from and to.</summary>
    private readonly List<(long From, long To)> _sleeps = [];

    /// <inheritdoc />
    public long TimestampFrequency => time.TimestampFrequency;

    /// <inheritdoc />
    public long Timestamp() => time.GetTimestamp();

    /// <inheritdoc />
    public DateTimeOffset UtcNow() => time.GetUtcNow();

    /// <inheritdoc />
    public long InterruptTime100ns() => To100ns(time.GetTimestamp());

    /// <inheritdoc />
    public long UnbiasedInterruptTime100ns()
    {
        var now = time.GetTimestamp();
        long asleep;
        lock (_gate) asleep = _sleeps.Sum(sleep => Math.Max(0, Math.Min(now, sleep.To) - sleep.From));
        return To100ns(now - asleep);
    }

    /// <summary>
    /// The laptop sleeps for <paramref name="duration"/>: the test's clock moves on by it, and each timer due fires once,
    /// as a program sees it on waking. Only a <see cref="ManualTimeProvider"/> can be made to sleep.
    /// </summary>
    public void Sleep(TimeSpan duration)
    {
        if (time is not ManualTimeProvider clock) throw new InvalidOperationException("Only a test's own clock can be made to sleep.");

        // Noted as a stretch of the steady count before the clock moves, so that every reading agrees with the count it is made at.
        var from = clock.GetTimestamp();
        lock (_gate) _sleeps.Add((from, from + (long)(duration.Ticks * (double)clock.TimestampFrequency / TimeSpan.TicksPerSecond)));
        clock.Advance(duration);
    }

    /// <summary>A count of the test's clock in units of 100 nanoseconds.</summary>
    private long To100ns(long timestamp) => (long)(timestamp * ((double)TimeSpan.TicksPerSecond / time.TimestampFrequency));
}

/// <summary>Power requests a test can look at: each hold, with its reason and what it needs, and whether it was let go.</summary>
public sealed class FakePowerRequests : IPowerRequests
{
    private readonly ConcurrentQueue<FakePowerHold> _holds = new();

    /// <summary>Every hold taken so far, in order.</summary>
    public IReadOnlyList<FakePowerHold> Holds => [.. _holds];

    /// <summary>True while a hold is taken and not let go.</summary>
    public bool Holding => _holds.Any(hold => !hold.Released);

    /// <summary>Runs as a hold is let go, the first time only, so that a test can see what had happened by then.</summary>
    public Action? OnRelease { get; set; }

    /// <inheritdoc />
    public IDisposable Hold(string reason, Func<PowerNeeds> needs)
    {
        var hold = new FakePowerHold(reason, needs, () => OnRelease?.Invoke());
        _holds.Enqueue(hold);
        return hold;
    }
}

/// <summary>One hold of <see cref="FakePowerRequests"/>.</summary>
public sealed class FakePowerHold(string reason, Func<PowerNeeds> needs, Action onFirstRelease) : IDisposable
{
    private int _releases;

    /// <summary>The reason the hold was taken for.</summary>
    public string Reason => reason;

    /// <summary>How many times the hold was let go. It is meant to be let go once.</summary>
    public int Releases => Volatile.Read(ref _releases);

    /// <summary>True once the hold was let go.</summary>
    public bool Released => Releases > 0;

    /// <summary>What the hold needs now, as the real requests read it every 30 seconds.</summary>
    public PowerNeeds Needs() => needs();

    /// <summary>Lets the hold go.</summary>
    public void Dispose()
    {
        if (Interlocked.Increment(ref _releases) == 1) onFirstRelease();
    }
}

/// <summary>A power source the test switches by hand: on mains unless it says otherwise.</summary>
public sealed class FakePowerSource : IPowerSource
{
    private volatile bool _onBattery;

    /// <summary>True while the laptop is meant to run on battery.</summary>
    public bool OnBattery
    {
        get => _onBattery;
        set => _onBattery = value;
    }
}

/// <summary>
/// Windows' power request functions as a test plays them. Each request made is kept, with what is set on it; Windows can
/// be made to refuse to make requests, or to set one kind of request.
/// </summary>
internal sealed class FakePowerRequestApi : IPowerRequestApi
{
    private readonly Lock _gate = new();
    private readonly List<FakePowerRequest> _requests = [];
    private readonly Dictionary<PowerRequestType, int> _setErrors = [];
    private int _createError;

    /// <summary>The requests made so far, those Windows refused included, in order.</summary>
    public IReadOnlyList<FakePowerRequest> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    /// <summary>Makes Windows refuse to make requests from now on, with <paramref name="error"/>; 0 makes it accept them again.</summary>
    public void RefuseToCreate(int error)
    {
        lock (_gate) _createError = error;
    }

    /// <summary>Makes Windows refuse to set requests of <paramref name="type"/> from now on, with <paramref name="error"/>; 0 makes it accept them again.</summary>
    public void RefuseToSet(PowerRequestType type, int error)
    {
        lock (_gate) _setErrors[type] = error;
    }

    /// <inheritdoc />
    public SafeHandle Create(string reason, out int error)
    {
        lock (_gate)
        {
            error = _createError;
            var request = new FakePowerRequest(reason, valid: error == 0);
            _requests.Add(request);
            return request;
        }
    }

    /// <inheritdoc />
    public int Set(SafeHandle request, PowerRequestType type)
    {
        int error;
        lock (_gate) error = _setErrors.GetValueOrDefault(type);
        ((FakePowerRequest)request).Set(type, accepted: error == 0);
        return error;
    }

    /// <inheritdoc />
    public int Clear(SafeHandle request, PowerRequestType type)
    {
        ((FakePowerRequest)request).Clear(type);
        return 0;
    }
}

/// <summary>One request <see cref="FakePowerRequestApi"/> made: its reason, what is set on it, the calls made on it, and whether its handle was closed.</summary>
internal sealed class FakePowerRequest : SafeHandle
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<PowerRequestType> _active = [];

    /// <summary>A request with <paramref name="reason"/>: a handle that works, or the invalid one Windows gives back when it refuses.</summary>
    public FakePowerRequest(string reason, bool valid) : base(IntPtr.Zero, ownsHandle: true)
    {
        Reason = reason;
        if (valid) SetHandle(1);
    }

    /// <summary>The reason the request was made with.</summary>
    public string Reason { get; }

    /// <summary>The calls made on the request, in order, such as "Set System", "Refused Display", "Clear System" and "Close".</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>The kinds of request set on it now: set, and not cleared since.</summary>
    public IReadOnlyList<PowerRequestType> Active
    {
        get { lock (_gate) return [.. _active]; }
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Notes a request to set <paramref name="type"/>, and sets it when Windows <paramref name="accepted"/> it.</summary>
    public void Set(PowerRequestType type, bool accepted)
    {
        lock (_gate)
        {
            _calls.Add(accepted ? $"Set {type}" : $"Refused {type}");
            if (accepted && !_active.Contains(type)) _active.Add(type);
        }
    }

    /// <summary>Notes a request to clear <paramref name="type"/>, and clears it.</summary>
    public void Clear(PowerRequestType type)
    {
        lock (_gate)
        {
            _calls.Add($"Clear {type}");
            _active.Remove(type);
        }
    }

    /// <summary>Notes that the handle was closed, as CloseHandle would close it.</summary>
    protected override bool ReleaseHandle()
    {
        lock (_gate) _calls.Add("Close");
        return true;
    }
}
