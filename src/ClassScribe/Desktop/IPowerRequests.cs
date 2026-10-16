using ClassScribe.Settings;

namespace ClassScribe.Desktop;

/// <summary>What ClassScribe asks Windows to keep up while it records a class and uploads it.</summary>
[Flags]
public enum PowerNeeds
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The laptop stays in the working state: Windows does not put it to sleep for being left alone (PowerRequestSystemRequired).</summary>
    System = 1,

    /// <summary>ClassScribe keeps running, even where Windows would otherwise pause programs to save power (PowerRequestExecutionRequired).</summary>
    Execution = 2,

    /// <summary>
    /// The screen stays on (PowerRequestDisplayRequired). On a laptop with Modern Standby that runs on battery, standby
    /// starts when the screen turns off, whatever a program asks, so there this is what keeps the recording going.
    /// </summary>
    Display = 4
}

/// <summary>Asks Windows to keep the laptop awake.</summary>
public interface IPowerRequests
{
    /// <summary>
    /// Asks Windows to keep up what <paramref name="needs"/> returns until the result is disposed, under
    /// <paramref name="reason"/>, which powercfg /requests shows. <paramref name="needs"/> is read at once and again every
    /// 30 seconds, so that the screen can follow the power source. A request Windows refuses is logged with advice and
    /// never thrown: the recording goes on without it.
    /// </summary>
    IDisposable Hold(string reason, Func<PowerNeeds> needs);
}

/// <summary>Where the laptop takes its power from.</summary>
public interface IPowerSource
{
    /// <summary>True while the laptop runs on its battery, and when Windows cannot say; false on mains, and on a computer without a battery.</summary>
    bool OnBattery { get; }
}

/// <summary>
/// The power requests on a system other than Windows, where there is none to make: a hold asks for nothing, and what it
/// needs is never read. ClassScribe can only transcribe there, since recording needs Windows.
/// </summary>
internal sealed class NoPowerRequests : IPowerRequests
{
    /// <inheritdoc />
    public IDisposable Hold(string reason, Func<PowerNeeds> needs) => new NoHold();

    /// <summary>A hold that holds nothing, so there is nothing to let go.</summary>
    private sealed class NoHold : IDisposable
    {
        /// <summary>Does nothing.</summary>
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Where the computer takes its power from, on a system other than Windows, which ClassScribe does not ask: counted as
/// battery, as Windows' own answer is when Windows cannot say. Nothing reads it there (see <see cref="NoPowerRequests"/>).
/// </summary>
internal sealed class UnknownPowerSource : IPowerSource
{
    /// <inheritdoc />
    public bool OnBattery => true;
}

/// <summary>What ClassScribe asks of Windows while it records a class and while it uploads a recording.</summary>
public static class StayAwake
{
    /// <summary>The reason Windows shows, in powercfg /requests, for the request held while a class is recorded and then uploaded.</summary>
    public const string RecordingReason = "ClassScribe is recording a class";

    /// <summary>The reason Windows shows, in powercfg /requests, for the request held while a recording or a file made earlier is uploaded.</summary>
    public const string TranscribingReason = "ClassScribe is transcribing a recording";

    /// <summary>
    /// The laptop awake and ClassScribe running, always; the screen as <paramref name="screen"/> says: with
    /// <see cref="KeepScreenOn.OnBattery"/> while <paramref name="power"/> says the laptop runs on battery, which is
    /// read only for that setting.
    /// </summary>
    public static PowerNeeds Needs(KeepScreenOn screen, IPowerSource power)
    {
        const PowerNeeds awake = PowerNeeds.System | PowerNeeds.Execution;
        var keepScreenOn = screen switch
        {
            KeepScreenOn.Always => true,
            KeepScreenOn.Never => false,
            _ => power.OnBattery
        };

        return keepScreenOn ? awake | PowerNeeds.Display : awake;
    }
}
