namespace ClassScribe.Desktop;

/// <summary>
/// What Windows says about the job ClassScribe runs in, if any: the limits of the job it is directly in that decide
/// whether a recorder it starts can outlive it. dotnet run, for one, puts the program in a job that ends every process in
/// it when dotnet run ends, and lets none of them start a process outside it (limits 0x2000, measured on this laptop).
/// Only the job ClassScribe is directly in can be read; a job around that one is not seen.
/// </summary>
/// <param name="InJob">True when ClassScribe runs in a job.</param>
/// <param name="KillOnClose">True when the job ends every process in it once it closes (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE).</param>
/// <param name="BreakawayAllowed">
/// True when a process in the job may start another outside it by asking for that (CREATE_BREAKAWAY_FROM_JOB, allowed by
/// JOB_OBJECT_LIMIT_BREAKAWAY_OK). Windows refuses that request in a job without this limit.
/// </param>
/// <param name="SilentBreakaway">True when every process started from the job is outside it without asking (JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK).</param>
public sealed record JobFacts(bool InJob, bool KillOnClose, bool BreakawayAllowed, bool SilentBreakaway)
{
    /// <summary>JOB_OBJECT_LIMIT_BREAKAWAY_OK.</summary>
    public const uint BreakawayOkLimit = 0x800;

    /// <summary>JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK.</summary>
    public const uint SilentBreakawayOkLimit = 0x1000;

    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.</summary>
    public const uint KillOnJobCloseLimit = 0x2000;

    /// <summary>Not in a job: what ClassScribe starts lives on after it.</summary>
    public static JobFacts NoJob { get; } = new(InJob: false, KillOnClose: false, BreakawayAllowed: false, SilentBreakaway: false);

    /// <summary>
    /// What is taken when Windows does not say: a job that ends its processes when it closes and lets none out, as under
    /// dotnet run. A recording then runs in its own window, where closing the window is said to end it, rather than in a
    /// recorder that might end with the window after ClassScribe said that it would not.
    /// </summary>
    public static JobFacts Unknown { get; } = new(InJob: true, KillOnClose: true, BreakawayAllowed: false, SilentBreakaway: false);

    /// <summary>
    /// Whether to start a recorder with CREATE_BREAKAWAY_FROM_JOB: only in a job that allows it, since Windows refuses it
    /// in any other job, and outside a job it means nothing.
    /// </summary>
    public bool Breakaway => InJob && BreakawayAllowed;

    /// <summary>
    /// The facts for a job whose limits are <paramref name="limitFlags"/>, the LimitFlags of its
    /// JOBOBJECT_BASIC_LIMIT_INFORMATION. Limits other than the three that decide a recorder's life are left out.
    /// </summary>
    public static JobFacts FromLimitFlags(uint limitFlags) => new(
        InJob: true,
        KillOnClose: (limitFlags & KillOnJobCloseLimit) != 0,
        BreakawayAllowed: (limitFlags & BreakawayOkLimit) != 0,
        SilentBreakaway: (limitFlags & SilentBreakawayOkLimit) != 0);

    /// <summary>
    /// Whether a recorder started from this process, the window, outlives it; started with CREATE_BREAKAWAY_FROM_JOB when
    /// <paramref name="breakaway"/> is true, which Windows accepts only where <see cref="Breakaway"/> is. It does outside a
    /// job; in a job that does not end its processes when it closes; and in one whose processes start others outside it,
    /// by themselves (silent breakaway) or by asking (breakaway). So under dotnet run it never does. And after Windows
    /// refused a breakaway, starting the recorder without one is worth it only when this is true without breakaway.
    /// </summary>
    public bool RecorderOutlivesWindow(bool breakaway) => !InJob || !KillOnClose || SilentBreakaway || (breakaway && BreakawayAllowed);
}

/// <summary>Reads what Windows says about the job ClassScribe runs in.</summary>
public interface IJobInspector
{
    /// <summary>
    /// The facts for this process, read from Windows each time. Never throws: when Windows does not answer, the facts are
    /// <see cref="JobFacts.Unknown"/>, and the reason is logged.
    /// </summary>
    JobFacts Inspect();
}
