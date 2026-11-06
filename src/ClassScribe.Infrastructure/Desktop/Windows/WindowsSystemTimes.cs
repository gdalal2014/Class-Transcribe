using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// The computer's clocks: the steady clock and the time of day from <see cref="TimeProvider"/>, and Windows' interrupt
/// times. The interrupt times are read with the precise functions of Windows 10 and later, which cannot fail.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemTimes(TimeProvider time) : ISystemTimes
{
    /// <summary>The set of Windows functions that holds the interrupt times (realtimeapiset.h).</summary>
    private const string RealtimeFunctions = "api-ms-win-core-realtime-l1-1-1.dll";

    /// <inheritdoc />
    public long TimestampFrequency => time.TimestampFrequency;

    /// <inheritdoc />
    public long Timestamp() => time.GetTimestamp();

    /// <inheritdoc />
    public DateTimeOffset UtcNow() => time.GetUtcNow();

    /// <inheritdoc />
    public long InterruptTime100ns()
    {
        QueryInterruptTimePrecise(out var interruptTime);
        return (long)interruptTime;
    }

    /// <inheritdoc />
    public long UnbiasedInterruptTime100ns()
    {
        QueryUnbiasedInterruptTimePrecise(out var unbiasedTime);
        return (long)unbiasedTime;
    }

    /// <summary>Windows' interrupt time, sleep included, in units of 100 nanoseconds.</summary>
    [DllImport(RealtimeFunctions, ExactSpelling = true)]
    private static extern void QueryInterruptTimePrecise(out ulong interruptTimePrecise);

    /// <summary>Windows' interrupt time with the time asleep left out, in units of 100 nanoseconds.</summary>
    [DllImport(RealtimeFunctions, ExactSpelling = true)]
    private static extern void QueryUnbiasedInterruptTimePrecise(out ulong unbiasedInterruptTimePrecise);
}
