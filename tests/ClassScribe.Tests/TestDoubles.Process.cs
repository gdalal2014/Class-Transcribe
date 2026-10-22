using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>Which process a <see cref="FakeProcessStarter"/> was asked to start.</summary>
public enum FakeLaunchKind
{
    /// <summary>The recorder of a run (<see cref="IProcessStarter.StartRecorder"/>).</summary>
    Recorder,

    /// <summary>The alert window of a run (<see cref="IProcessStarter.StartAlertWindow"/>).</summary>
    AlertWindow,

    /// <summary>Chrome, for a YouTube video's window (<see cref="IProcessStarter.StartBrowser"/>).</summary>
    Browser
}

/// <summary>
/// One start a <see cref="FakeProcessStarter"/> was asked for: what was asked, whether Windows was made to refuse it, and,
/// when it started, the pretend process, which runs until the test ends it with <see cref="Exit"/>.
/// </summary>
public sealed class FakeLaunch
{
    private readonly Lock _gate = new();
    private int? _exitCode;

    /// <summary>A start Windows was made to refuse with <paramref name="error"/>.</summary>
    internal FakeLaunch(FakeLaunchKind kind, string runId, bool breakaway, int error)
    {
        Kind = kind;
        RunId = runId;
        Breakaway = breakaway;
        RefusedWith = error;
    }

    /// <summary>A process started as <paramref name="processId"/> at <paramref name="startedUtc"/>; with <paramref name="exitedWith"/>, one that has already ended.</summary>
    internal FakeLaunch(FakeLaunchKind kind, string runId, bool breakaway, int processId, DateTimeOffset startedUtc, int? exitedWith)
    {
        Kind = kind;
        RunId = runId;
        Breakaway = breakaway;
        ProcessId = processId;
        StartedUtc = startedUtc;
        _exitCode = exitedWith;
        Process = new StartedProcess(processId, startedUtc, () => ExitCode);
    }

    /// <summary>Which process was asked for.</summary>
    public FakeLaunchKind Kind { get; }

    /// <summary>The program a browser was asked to start from; null for ClassScribe's own processes.</summary>
    public string? Program { get; init; }

    /// <summary>A browser's arguments, in order; empty for ClassScribe's own processes.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>A browser's working folder; null for ClassScribe's own processes, which run in ClassScribe's own folder.</summary>
    public string? WorkingFolder { get; init; }

    /// <summary>The run it was asked for.</summary>
    public string RunId { get; }

    /// <summary>True when the recorder was asked to start outside the job (CREATE_BREAKAWAY_FROM_JOB); never for the alert window.</summary>
    public bool Breakaway { get; }

    /// <summary>Windows' error, when the start was refused; null when the process started.</summary>
    public int? RefusedWith { get; }

    /// <summary>True when the process started.</summary>
    public bool Started => RefusedWith is null;

    /// <summary>The pretend process's id; 0 when the start was refused.</summary>
    public int ProcessId { get; }

    /// <summary>When the pretend process started, by the starter's clock; the default when the start was refused.</summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>What the starter handed back for the process; null when the start was refused.</summary>
    public StartedProcess? Process { get; }

    /// <summary>The process's exit code once it has ended; null while it runs, and when it never started.</summary>
    public int? ExitCode
    {
        get { lock (_gate) return _exitCode; }
    }

    /// <summary>Ends the pretend process with <paramref name="code"/>, as a recorder that fails or an alert window Gulzar closes ends. A second end changes nothing.</summary>
    public void Exit(int code)
    {
        if (!Started) throw new InvalidOperationException("A process that never started cannot end.");
        lock (_gate) _exitCode ??= code;
    }
}

/// <summary>
/// Starts pretend processes and keeps every start it was asked for, refused ones included, in order. Windows can be made to
/// refuse breakaway, as a job that does not allow it does, or every start; and each process can be made to end at once,
/// as a recorder that fails before it answers does. <see cref="OnStart"/> lets a test play the process that starts, such
/// as a recorder writing its run file. Like the real starter, it refuses anything but a run id before anything starts.
/// </summary>
/// <param name="time">The clock the pretend processes' start times come from; the computer's when none is given.</param>
public sealed class FakeProcessStarter(TimeProvider? time = null) : IProcessStarter
{
    private readonly Lock _gate = new();
    private readonly List<FakeLaunch> _launches = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _nextProcessId = 4000;

    /// <summary>Every start asked for so far, refused ones included, in order.</summary>
    public IReadOnlyList<FakeLaunch> Launches
    {
        get { lock (_gate) return [.. _launches]; }
    }

    /// <summary>The recorders that started, in order.</summary>
    public IReadOnlyList<FakeLaunch> Recorders => [.. Launches.Where(launch => launch.Kind == FakeLaunchKind.Recorder && launch.Started)];

    /// <summary>The alert windows that started, in order.</summary>
    public IReadOnlyList<FakeLaunch> AlertWindows => [.. Launches.Where(launch => launch.Kind == FakeLaunchKind.AlertWindow && launch.Started)];

    /// <summary>
    /// True to make Windows refuse every recorder asked to start outside the job with <see cref="IProcessStarter.AccessDenied"/>,
    /// as it does in a job that does not allow breakaway.
    /// </summary>
    public bool RefuseBreakaway { get; set; }

    /// <summary>Windows' error to refuse every start with, such as 2 (the exe is gone) or 225 (a virus scanner blocked it); null to start them.</summary>
    public int? RefuseWith { get; set; }

    /// <summary>The exit code every process started from now on has already ended with, as a recorder that fails before it answers; null to let them run.</summary>
    public int? EndAtOnceWith { get; set; }

    /// <summary>Runs as each process starts, on the thread that started it, before the starter hands it back.</summary>
    public Action<FakeLaunch>? OnStart { get; set; }

    /// <inheritdoc />
    public StartedProcess StartRecorder(string runId, bool breakaway) => Start(FakeLaunchKind.Recorder, runId, breakaway);

    /// <inheritdoc />
    public StartedProcess StartAlertWindow(string runId) => Start(FakeLaunchKind.AlertWindow, runId, breakaway: false);

    /// <summary>The browsers that started, in order.</summary>
    public IReadOnlyList<FakeLaunch> Browsers => [.. Launches.Where(launch => launch.Kind == FakeLaunchKind.Browser && launch.Started)];

    /// <summary>Windows' error to refuse every browser with, such as 2 (Chrome is gone); null to start them. The other starts are not touched.</summary>
    public int? RefuseBrowsersWith { get; set; }

    /// <summary>
    /// The exit code every browser started from now on has already ended with, as a Chrome that handed its window to one already
    /// open and ended; null to let them run until the test ends them.
    /// </summary>
    public int? BrowsersEndAtOnceWith { get; set; }

    /// <inheritdoc />
    public StartedProcess StartBrowser(string program, IReadOnlyList<string> arguments, string workingFolder)
    {
        FakeLaunch launch;
        lock (_gate)
        {
            launch = RefuseBrowsersWith is { } error
                ? new FakeLaunch(FakeLaunchKind.Browser, "", breakaway: true, error) { Program = program, Arguments = [.. arguments], WorkingFolder = workingFolder }
                : new FakeLaunch(FakeLaunchKind.Browser, "", breakaway: true, _nextProcessId++, _time.GetUtcNow(), BrowsersEndAtOnceWith)
                {
                    Program = program, Arguments = [.. arguments], WorkingFolder = workingFolder
                };
            _launches.Add(launch);
        }

        if (RefuseBrowsersWith is { } refused) throw StartRefusal.For(StartRefusal.Browser, refused);

        OnStart?.Invoke(launch);
        return launch.Process!;
    }

    /// <summary>Notes the start, then refuses it as Windows would, or starts a pretend process.</summary>
    private StartedProcess Start(FakeLaunchKind kind, string runId, bool breakaway)
    {
        if (!ProcessArguments.IsRunId(runId)) throw new ArgumentException($"\"{runId}\" is not a run id.", nameof(runId));

        var refusal = RefuseWith ?? (breakaway && RefuseBreakaway ? IProcessStarter.AccessDenied : null);
        FakeLaunch launch;
        lock (_gate)
        {
            launch = refusal is { } error
                ? new FakeLaunch(kind, runId, breakaway, error)
                : new FakeLaunch(kind, runId, breakaway, _nextProcessId++, _time.GetUtcNow(), EndAtOnceWith);
            _launches.Add(launch);
        }

        if (refusal is { } refused) throw StartRefusal.For(kind == FakeLaunchKind.Recorder ? StartRefusal.Recorder : StartRefusal.AlertWindow, refused);

        OnStart?.Invoke(launch);
        return launch.Process!;
    }
}

/// <summary>
/// A job a test puts ClassScribe in, by hand. Not in a job unless the test says otherwise; the named jobs below are the
/// ones that matter, with the limits measured on this laptop.
/// </summary>
/// <param name="facts">The facts to answer with.</param>
public sealed class FakeJobInspector(JobFacts facts) : IJobInspector
{
    private volatile JobFacts _facts = facts;
    private int _inspections;

    /// <summary>Not in a job.</summary>
    public FakeJobInspector()
        : this(JobFacts.NoJob)
    {
    }

    /// <summary>The job dotnet run puts the program in: it kills on close and allows no breakaway (limits 0x2000).</summary>
    public static JobFacts DotnetRun { get; } = JobFacts.FromLimitFlags(JobFacts.KillOnJobCloseLimit);

    /// <summary>A job that kills on close but allows breakaway (limits 0x2800): a recorder outlives it only by breaking away.</summary>
    public static JobFacts KillOnCloseWithBreakaway { get; } = JobFacts.FromLimitFlags(JobFacts.KillOnJobCloseLimit | JobFacts.BreakawayOkLimit);

    /// <summary>A job that allows breakaway, silent breakaway too, and does not kill on close (limits 0x1800), like the one a coding agent's shell ran in here.</summary>
    public static JobFacts BreakawayWithoutKill { get; } = JobFacts.FromLimitFlags(JobFacts.BreakawayOkLimit | JobFacts.SilentBreakawayOkLimit);

    /// <summary>The facts the next inspection answers with.</summary>
    public JobFacts Facts
    {
        get => _facts;
        set => _facts = value;
    }

    /// <summary>How many times the job was inspected.</summary>
    public int Inspections => Volatile.Read(ref _inspections);

    /// <inheritdoc />
    public JobFacts Inspect()
    {
        Interlocked.Increment(ref _inspections);
        return Facts;
    }
}

/// <summary>Windows' job functions as a test plays them: in a job or not, its limits, and Windows refusing to answer either question.</summary>
internal sealed class FakeJobApi : IJobApi
{
    private int _limitReads;

    /// <summary>True when the process is meant to run in a job.</summary>
    public bool InJob { get; set; }

    /// <summary>The limits of the job it is meant to be directly in.</summary>
    public uint LimitFlags { get; set; }

    /// <summary>Windows' error for the question whether the process runs in a job; 0 answers it.</summary>
    public int InJobError { get; set; }

    /// <summary>Windows' error for the question of the job's limits; 0 answers it.</summary>
    public int LimitsError { get; set; }

    /// <summary>How many times the job's limits were asked for.</summary>
    public int LimitReads => Volatile.Read(ref _limitReads);

    /// <inheritdoc />
    public int IsInJob(out bool inJob)
    {
        inJob = InJobError == 0 && InJob;
        return InJobError;
    }

    /// <inheritdoc />
    public int ReadLimitFlags(out uint limitFlags)
    {
        Interlocked.Increment(ref _limitReads);
        limitFlags = LimitsError == 0 ? LimitFlags : 0;
        return LimitsError;
    }
}

/// <summary>
/// Windows' CreateProcessW as a test plays it: every launch it is given is kept, refused ones included, and it hands back
/// the process <see cref="Next"/> names, or refuses with <see cref="Refusal"/>.
/// </summary>
internal sealed class FakeProcessCreationApi : IProcessCreationApi
{
    private readonly Lock _gate = new();
    private readonly List<ProcessLaunch> _launches = [];

    /// <summary>Every launch given so far, in order.</summary>
    public IReadOnlyList<ProcessLaunch> Launches
    {
        get { lock (_gate) return [.. _launches]; }
    }

    /// <summary>Windows' error to refuse every launch with from now on; 0 starts them.</summary>
    public int Refusal { get; set; }

    /// <summary>Windows' error for each launch, from the launch itself, such as breakaway refused by a job; null leaves it to <see cref="Refusal"/>.</summary>
    public Func<ProcessLaunch, int>? RefusalFor { get; set; }

    /// <summary>The process handed back for each launch Windows starts: process 4321, running, unless the test says otherwise.</summary>
    public StartedProcess Next { get; set; } = new(4321, new DateTimeOffset(2026, 10, 16, 23, 0, 2, TimeSpan.Zero), () => null);

    /// <summary>
    /// Plays the process as each launch Windows starts is handed back, while the launch's handles are still open: a tool's,
    /// say, writing its lines into the standard handles it was given (see <see cref="WriteTo"/>).
    /// </summary>
    public Action<ProcessLaunch>? OnStart { get; set; }

    /// <summary>The environment block, standard handles and job of each tool launch, as they were when Windows was given them.</summary>
    public IReadOnlyList<(IReadOnlyDictionary<string, string>? Environment, bool HadHandles, bool HadJob)> ToolParts
    {
        get { lock (_gate) return [.. _launches.Select(launch => (launch.Environment, launch.Handles is not null, launch.Job is not null))]; }
    }

    /// <inheritdoc />
    public StartedProcess? Start(ProcessLaunch launch, out int error)
    {
        lock (_gate) _launches.Add(launch);
        error = RefusalFor?.Invoke(launch) ?? Refusal;
        if (error != 0) return null;

        OnStart?.Invoke(launch);
        return Next;
    }

    /// <summary>Writes <paramref name="text"/> as UTF-8 into <paramref name="handle"/>, a write end of a pipe a tool's launch hands the tool, as the tool would.</summary>
    public static void WriteTo(SafeHandle handle, string text)
    {
        using var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), FileAccess.Write, bufferSize: 0);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }
}

/// <summary>Everything one call of Windows' CreateProcessW was given, as <see cref="FakeCreateProcess"/> kept it.</summary>
/// <param name="ApplicationName">lpApplicationName: the program's full path.</param>
/// <param name="CommandLine">lpCommandLine: the characters given, the null character that ends them included.</param>
/// <param name="ProcessAttributes">lpProcessAttributes: the new process's security attributes.</param>
/// <param name="ThreadAttributes">lpThreadAttributes: its first thread's security attributes.</param>
/// <param name="InheritHandles">bInheritHandles: whether it inherits the caller's inheritable handles.</param>
/// <param name="CreationFlags">dwCreationFlags.</param>
/// <param name="Environment">lpEnvironment: an environment block, or zero to inherit the caller's.</param>
/// <param name="CurrentDirectory">lpCurrentDirectory: its working folder.</param>
/// <param name="StartupInfo">lpStartupInfo: the STARTUPINFO, as it was when CreateProcessW was called.</param>
internal sealed record CreateProcessCall(
    string ApplicationName,
    string CommandLine,
    IntPtr ProcessAttributes,
    IntPtr ThreadAttributes,
    bool InheritHandles,
    uint CreationFlags,
    IntPtr Environment,
    string CurrentDirectory,
    NativeProcessCreationApi.StartupInfo StartupInfo);

/// <summary>
/// Windows' CreateProcessW itself as a test plays it, for <see cref="NativeProcessCreationApi"/>: it keeps every argument
/// of every call, exactly as Windows would get them, and refuses each call with <see cref="Refusal"/>, left behind as the
/// last error as Windows leaves its own. So no process is ever started and no handle is made.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeCreateProcess
{
    private readonly Lock _gate = new();
    private readonly List<CreateProcessCall> _calls = [];

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<CreateProcessCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>Windows' error every call is refused with: ERROR_ACCESS_DENIED unless the test says otherwise.</summary>
    public int Refusal { get; set; } = IProcessStarter.AccessDenied;

    /// <summary>Takes a call of CreateProcessW in Windows' place: keeps what it was given, then refuses it.</summary>
    public bool CreateProcess(
        string applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref NativeProcessCreationApi.StartupInfo startupInfo,
        out NativeProcessCreationApi.ProcessInformation processInformation)
    {
        lock (_gate)
        {
            _calls.Add(new CreateProcessCall(
                applicationName, new string(commandLine), processAttributes, threadAttributes, inheritHandles, creationFlags, environment,
                currentDirectory, startupInfo));
        }

        processInformation = default;

        // Set last, so that nothing changes it before the caller reads it, as it reads Windows' own error.
        Marshal.SetLastPInvokeError(Refusal);
        return false;
    }
}
