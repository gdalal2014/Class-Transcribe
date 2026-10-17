using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// Processes as a test plays them, for the code that asks whether the process named in a run file still runs. The test
/// starts a process under an id with a start time (<see cref="Start"/>) and ends it (<see cref="End"/>). A process the
/// test has not started, or has ended, is gone. A process started again under the same id with another start time is a
/// new process that Windows gave the id to once the old one had ended, so it does not count as the old one, unless the
/// two start times are within <see cref="IProcessLiveness.StartTimeTolerance"/> of each other, as for the real check. The
/// computer last started at <see cref="BootUtc"/>.
/// </summary>
public sealed class FakeProcessLiveness : IProcessLiveness
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, DateTimeOffset> _running = [];
    private DateTimeOffset _bootUtc = TestRuns.Start.AddHours(-11);

    /// <summary>When the computer last started; by default eleven hours before the tests' class, at noon UTC that day.</summary>
    public DateTimeOffset BootUtc
    {
        get { lock (_gate) return _bootUtc; }
        set { lock (_gate) _bootUtc = value; }
    }

    /// <summary>A process runs under <paramref name="processId"/>, started at <paramref name="startedUtc"/>, in place of any that ran under that id before.</summary>
    public void Start(int processId, DateTimeOffset startedUtc)
    {
        lock (_gate) _running[processId] = startedUtc;
    }

    /// <summary>The process under <paramref name="processId"/> has ended.</summary>
    public void End(int processId)
    {
        lock (_gate) _running.Remove(processId);
    }

    /// <inheritdoc />
    public bool IsAlive(int processId, DateTimeOffset startedUtc)
    {
        lock (_gate) return _running.TryGetValue(processId, out var actual) && IProcessLiveness.StartedTogether(actual, startedUtc);
    }

    /// <inheritdoc />
    public DateTimeOffset LastBootUtc() => BootUtc;
}

/// <summary>
/// Runs as the tests write them down: run ids, requests and run files, with every field filled in the usual way, to be
/// changed with <c>with</c> where a test needs something else.
/// </summary>
public static class TestRuns
{
    /// <summary>The process that records the tests' runs, unless a test says otherwise.</summary>
    public const int ProcessId = 4321;

    /// <summary>When the tests' class started: 23:00:02 UTC on 15 October 2026, which was 19:00 in the evening in Eastern time.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 15, 23, 0, 2, TimeSpan.Zero);

    /// <summary>When the process that records the tests' runs started: a second before the class.</summary>
    public static readonly DateTimeOffset ProcessStart = Start.AddSeconds(-1);

    /// <summary>
    /// The id of the tests' run numbered <paramref name="number"/>, as <see cref="RunIds.New"/> makes them: its start, that
    /// many minutes after <see cref="Start"/>, so that later numbers sort later, and the number in 8 hexadecimal digits.
    /// </summary>
    public static string Id(int number = 1) =>
        $"{Start.AddMinutes(number).UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z-{number:x8}";

    /// <summary>A request to record <paramref name="name"/> from the device the class plays on into D:\Classes, and to transcribe it.</summary>
    public static RecordRequest Request(string runId, string name = "Kalam") =>
        new(runId, name, Transcribe: true, new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null), @"D:\Classes",
            ContinueChoice.New, ContinueBaseName: null, Minutes: null, Open: true);

    /// <summary>
    /// The run file of run <paramref name="runId"/>, recording <paramref name="name"/> in a separate recorder, in
    /// <paramref name="state"/>: recorded by process <paramref name="processId"/>, which started at
    /// <paramref name="processStartedUtc"/> (<see cref="ProcessStart"/> unless given), and begun at <see cref="Start"/>.
    /// </summary>
    public static RunRecord Record(string runId, RunState state = RunState.Starting, int processId = ProcessId, DateTimeOffset? processStartedUtc = null, string name = "Kalam") =>
        new(RunRecord.CurrentVersion, runId, processId, processStartedUtc ?? ProcessStart, @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\ClassScribe.exe",
            Build: "1.0.0+test", Protocol: 1, Pipe: $"ClassScribe-{runId}", RunHome.Separate, Request(runId, name), BaseName: null, Folder: null, state,
            StartedUtc: Start, HeartbeatUtc: Start, Position: TimeSpan.Zero, UploadPercent: null, EndedBy: null, Outcome: null, Seen: false);
}

/// <summary>
/// ClassScribe's own folder made in a temporary folder of the test's, with a run journal over processes the test plays
/// (<see cref="Processes"/>) and a clock the test moves (<see cref="Time"/>). The lock and the run files are real files the
/// test can look at, and the real %LOCALAPPDATA%\ClassScribe, where a class may be recording, is never touched. Disposing it
/// deletes the folder.
/// </summary>
public sealed class TestRuntime : IDisposable
{
    /// <summary>Makes the folder, the processes, the clock at <see cref="TestRuns.Start"/>, a log to inspect, and the journal.</summary>
    public TestRuntime()
    {
        Temp = new TempFolder();
        Folder = new RuntimeFolder(Temp.Path);
        Journal = JournalPausingWith(Thread.Sleep);
    }

    /// <summary>The temporary folder that stands for %LOCALAPPDATA%\ClassScribe.</summary>
    public TempFolder Temp { get; }

    /// <summary>ClassScribe's own folder, in <see cref="Temp"/>.</summary>
    public RuntimeFolder Folder { get; }

    /// <summary>The processes the run files name.</summary>
    public FakeProcessLiveness Processes { get; } = new();

    /// <summary>The clock the journal reads, at <see cref="TestRuns.Start"/> until the test moves it.</summary>
    public ManualTimeProvider Time { get; } = new(TestRuns.Start);

    /// <summary>What the journal logged.</summary>
    public RecordingLogger<RunJournal> Log { get; } = new();

    /// <summary>The journal, which sleeps for real before it tries a file again, as ClassScribe does.</summary>
    public RunJournal Journal { get; }

    /// <summary>
    /// A journal over the same folder, processes, clock and log that waits with <paramref name="pause"/> before it tries a
    /// file again, so that a test can act at that moment, as the program that holds the file would.
    /// </summary>
    public RunJournal JournalPausingWith(Action<TimeSpan> pause) => new(Folder, Processes, Time, Log, pause);

    /// <summary>The run files and other files in the runs folder, by name, in order.</summary>
    public IReadOnlyList<string> FilesInRuns() =>
        Directory.Exists(Folder.Runs) ? [.. Directory.GetFiles(Folder.Runs).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)] : [];

    /// <summary>Deletes the folder.</summary>
    public void Dispose() => Temp.Dispose();
}
