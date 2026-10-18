using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Tests;

/// <summary>
/// A test's clock with a local time zone of the test's choosing: the time of day, the steady count and the timers are those
/// of <paramref name="clock"/>, which the test moves, and local times are read in <paramref name="zone"/>, so that a time
/// shown in local time, such as 19:47, reads the same on every machine.
/// </summary>
/// <param name="clock">The clock the test moves.</param>
/// <param name="zone">The local time zone.</param>
public sealed class ZonedClock(ManualTimeProvider clock, TimeZoneInfo zone) : TimeProvider
{
    /// <summary>The test's clock in US Eastern time in October, where an evening class stamped 23:00 UTC began at 19:00.</summary>
    public static ZonedClock Eastern(ManualTimeProvider clock) => new(clock, ZonedTimeProvider.EasternSummer.LocalTimeZone);

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => zone;

    /// <inheritdoc />
    public override long TimestampFrequency => clock.TimestampFrequency;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    /// <inheritdoc />
    public override long GetTimestamp() => clock.GetTimestamp();

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        clock.CreateTimer(callback, state, dueTime, period);
}

/// <summary>One recorder a <see cref="PlayedRecorder"/> played: the start asked for, and what the recorder took and wrote.</summary>
/// <param name="Launch">The start, with the pretend process.</param>
/// <param name="Request">The request the recorder took from its request file; null when it ended before it took it.</param>
/// <param name="Settings">The settings that came with the request; null when it ended before it took them.</param>
/// <param name="Run">The handle on the run file it wrote; null when it wrote none.</param>
/// <param name="Files">The files of the recording it records into; null when it never recorded.</param>
public sealed record PlayedRun(FakeLaunch Launch, RecordRequest? Request, IReadOnlyDictionary<string, string?>? Settings, RunHandle? Run, RecordingFiles? Files);

/// <summary>
/// Plays the recorders a <see cref="FakeProcessStarter"/> starts, as the recorder's host does at its start: it takes its
/// request from the run's request file, and then, as the test has queued for each start in turn, records (the run file
/// says Recording, with the recording's files), is refused because another recording holds the lock (Refused, exit code
/// 3, or exit code 3 alone with a run file that still says Starting), fails with a reason of its own (Failed, with its
/// exit code), ends before it answers (no run file, with lines in recorder.log), hangs (alive, no run file), or stays
/// starting (alive, a run file that says Starting). With nothing queued, it records. Each pretend process is alive for the
/// run files' liveness from its start until it ends. The test then plays the rest of each run through
/// <see cref="Finish"/>, <see cref="Stopping"/>, <see cref="Transcribing"/> and <see cref="Lose"/>.
/// </summary>
public sealed class PlayedRecorder
{
    private readonly Lock _gate = new();
    private readonly Queue<Action<FakeLaunch>> _parts = new();
    private readonly List<PlayedRun> _runs = [];
    private readonly TestRuntime _runtime;

    /// <summary>Plays every recorder <paramref name="starter"/> starts, with the files, processes and clock of <paramref name="runtime"/>.</summary>
    public PlayedRecorder(TestRuntime runtime, FakeProcessStarter starter)
    {
        _runtime = runtime;
        starter.OnStart = Play;
    }

    /// <summary>Every recorder played so far, in the order they started.</summary>
    public IReadOnlyList<PlayedRun> Runs
    {
        get { lock (_gate) return [.. _runs]; }
    }

    /// <summary>The next recorder records: it takes its request and writes its run file, Starting and then Recording into the recording's files.</summary>
    public PlayedRecorder Records() => Queue(launch => Record(launch, RunState.Recording));

    /// <summary>The next recorder takes its request, writes its run file as Starting, and stays there, alive.</summary>
    public PlayedRecorder StaysStarting() => Queue(launch => Record(launch, RunState.Starting));

    /// <summary>The next recorder starts, alive, and never writes a run file nor takes its request: it hangs before it answers.</summary>
    public PlayedRecorder Hangs() => Queue(launch => _runtime.Processes.Start(launch.ProcessId, launch.StartedUtc));

    /// <summary>
    /// The next recorder finds another recording holding the lock: it takes its request, writes its run file as Refused and
    /// ends with exit code 3. With <paramref name="winner"/>, that other recording's recorder, alive, has just written its
    /// own run file, as one started from another window at the same moment has.
    /// </summary>
    public PlayedRecorder IsRefused(RunRecord? winner = null) => Queue(launch =>
    {
        StartWinner(winner);
        var run = Take(launch);
        run.Run!.End(new RunOutcome(OutcomeKind.Refused, 3, "Another recording is running.", null, null, null));
        End(run with { Files = null }, 3);
    });

    /// <summary>
    /// The next recorder finds another recording holding the lock, as <see cref="IsRefused"/> does, but writing the refusal
    /// into its run file fails, a failure the run file's handle reports and goes past: the file still says Starting, and
    /// only the exit code, 3, says that it was refused. With <paramref name="winner"/>, that other recording's recorder,
    /// alive, has just written its own run file.
    /// </summary>
    public PlayedRecorder IsRefusedWhileItsRunFileSaysStarting(RunRecord? winner = null) => Queue(launch =>
    {
        StartWinner(winner);
        End(Take(launch), 3);
    });

    /// <summary>
    /// The next recorder fails before it records: it takes its request, writes its run file as Failed with
    /// <paramref name="reason"/>, and ends with <paramref name="exitCode"/>.
    /// </summary>
    public PlayedRecorder Fails(string reason, int exitCode = 1) => Queue(launch =>
    {
        var run = Take(launch);
        run.Run!.End(new RunOutcome(OutcomeKind.RecorderFailed, 1, reason, null, null, null));
        End(run with { Files = null }, exitCode);
    });

    /// <summary>
    /// The next recorder ends with <paramref name="exitCode"/> before it takes its request or writes a run file, as one
    /// whose host cannot be built ends, after writing <paramref name="logLines"/> to recorder.log in ClassScribe's own folder.
    /// </summary>
    public PlayedRecorder EndsBeforeAnswering(int exitCode, params string[] logLines) => Queue(launch =>
    {
        if (logLines.Length > 0)
        {
            Directory.CreateDirectory(_runtime.Folder.Root);
            File.AppendAllLines(Path.Combine(_runtime.Folder.Root, RecorderLog.StartupFileName), logLines);
        }

        var run = new PlayedRun(launch, null, null, null, null);
        lock (_gate) _runs.Add(run);
        launch.Exit(exitCode);
    });

    /// <summary>The played recorder of run <paramref name="runId"/>.</summary>
    public PlayedRun Of(string runId)
    {
        lock (_gate) return _runs.Single(run => run.Launch.RunId == runId);
    }

    /// <summary>The run of <paramref name="runId"/> ends as <paramref name="outcome"/> says: its run file is finished and its recorder ends with the outcome's exit code.</summary>
    public void Finish(string runId, RunOutcome outcome)
    {
        var run = Of(runId);
        run.Run!.End(outcome);
        End(run, outcome.ExitCode);
    }

    /// <summary>The run of <paramref name="runId"/> was asked to stop and is closing its capture and files, as its run file now says.</summary>
    public void Stopping(string runId) => Of(runId).Run!.SetState(RunState.Stopping);

    /// <summary>The run of <paramref name="runId"/> stopped recording and is being transcribed, as its run file now says.</summary>
    public void Transcribing(string runId) => Of(runId).Run!.SetState(RunState.Transcribing);

    /// <summary>The recorder of run <paramref name="runId"/> is killed: it ends without a word, and its run file stays as it last was.</summary>
    public void Lose(string runId) => End(Of(runId), -1);

    /// <summary>
    /// The recording <paramref name="winner"/> describes, when there is one, holds the lock: the process its run file names
    /// is alive, and the file is written, as by a recorder started from another window at the same moment.
    /// </summary>
    private void StartWinner(RunRecord? winner)
    {
        if (winner is null) return;

        _runtime.Processes.Start(winner.ProcessId, winner.ProcessStartedUtc);
        _runtime.Journal.Write(_runtime.Journal.RunFile(winner.RunId), winner);
    }

    /// <summary>Queues what the next recorder does.</summary>
    private PlayedRecorder Queue(Action<FakeLaunch> part)
    {
        lock (_gate) _parts.Enqueue(part);
        return this;
    }

    /// <summary>Plays the recorder that has just started: what the test queued for it, or a recording.</summary>
    private void Play(FakeLaunch launch)
    {
        if (launch.Kind != FakeLaunchKind.Recorder) return;

        Action<FakeLaunch>? part;
        lock (_gate) part = _parts.TryDequeue(out var queued) ? queued : null;
        (part ?? (started => Record(started, RunState.Recording)))(launch);
    }

    /// <summary>Takes the request, as the recorder does, and writes the run file in <paramref name="state"/>: Starting, or Recording into the recording's files.</summary>
    private void Record(FakeLaunch launch, RunState state)
    {
        var run = Take(launch);
        if (state != RunState.Recording) return;

        var request = run.Request!;
        var folder = Path.Combine(request.OutputRoot, request.Name);
        var files = request.ContinueBaseName is { } baseName
            ? new RecordingFiles(folder, baseName)
            : RecordingFiles.ForNew(folder, request.Name, _runtime.Time.GetUtcNow());
        run.Run!.Recording(files);
        Replace(run with { Files = files });
    }

    /// <summary>The recorder starts, takes its request and begins its run file as Starting.</summary>
    private PlayedRun Take(FakeLaunch launch)
    {
        _runtime.Processes.Start(launch.ProcessId, launch.StartedUtc);
        var (request, settings) = _runtime.Journal.TakeRequest(launch.RunId)
                                  ?? throw new AssertionException($"The recorder of run {launch.RunId} found no request file.");
        var handle = _runtime.Journal.Begin(TestRuns.Record(launch.RunId, RunState.Starting, launch.ProcessId, launch.StartedUtc, request.Name) with
        {
            Request = request,
            StartedUtc = _runtime.Time.GetUtcNow()
        });

        var run = new PlayedRun(launch, request, settings, handle, null);
        lock (_gate) _runs.Add(run);
        return run;
    }

    /// <summary>Puts <paramref name="run"/> in the place of the run it replaces, by its start.</summary>
    private void Replace(PlayedRun run)
    {
        lock (_gate) _runs[_runs.FindIndex(played => played.Launch == run.Launch)] = run;
    }

    /// <summary>The recorder of <paramref name="run"/> ends with <paramref name="exitCode"/>, and lets go of its run file.</summary>
    private void End(PlayedRun run, int exitCode)
    {
        run.Run?.Dispose();
        _runtime.Processes.End(run.Launch.ProcessId);
        run.Launch.Exit(exitCode);
    }
}

/// <summary>
/// Shows runs as a test plays the view of them: it keeps each run asked for and how, and answers each with what the test
/// queued, in order. With nothing queued, the run ends with exit code 0 at once, its outcome a saved transcript.
/// </summary>
public sealed class FakeRunViewer : IRunViewer
{
    private readonly Lock _gate = new();
    private readonly Queue<Func<RunRecord, ViewMode, ViewResult>> _answers = new();
    private readonly List<(RunRecord Run, ViewMode Mode)> _shown = [];

    /// <summary>A transcript saved: the outcome a run ends with when the test says nothing else.</summary>
    public static RunOutcome Transcribed { get; } = new(OutcomeKind.Transcribed, 0, "Transcript saved.", @"D:\Classes\Kalam\Kalam.md", @"D:\Classes\Kalam", null);

    /// <summary>Every run shown so far, with how, in order.</summary>
    public IReadOnlyList<(RunRecord Run, ViewMode Mode)> Shown
    {
        get { lock (_gate) return [.. _shown]; }
    }

    /// <summary>Queues the answer to the next run shown: what happens while it is shown, and how showing it ends.</summary>
    public FakeRunViewer Then(Func<RunRecord, ViewMode, ViewResult> answer)
    {
        lock (_gate) _answers.Enqueue(answer);
        return this;
    }

    /// <inheritdoc />
    public Task<ViewResult> ShowAsync(RunRecord run, ViewMode mode, CancellationToken cancellationToken)
    {
        Func<RunRecord, ViewMode, ViewResult>? answer;
        lock (_gate)
        {
            _shown.Add((run, mode));
            answer = _answers.TryDequeue(out var queued) ? queued : null;
        }

        return Task.FromResult(answer?.Invoke(run, mode) ?? new ViewResult(ViewEnd.RunEnded, 0, Transcribed));
    }
}

/// <summary>
/// The runs a test's <see cref="IRecordRun"/> stand-ins ran in this window: each request, and the scope it ran in, in order.
/// Each run answers with the exit code the test queued, or 0, or fails with the error the test queued; and Google Drive's
/// part in it, when the scope has one, is told how it ended, as the record command tells it (see
/// <see cref="IDriveAutoUpload.Ended"/>).
/// </summary>
public sealed class FakeRecordRuns
{
    private readonly Lock _gate = new();
    private readonly List<(RecordRequest Request, IServiceProvider Scope)> _runs = [];
    private readonly Queue<int> _exitCodes = new();
    private readonly Queue<Exception> _failures = new();

    /// <summary>Queues the error the next run fails with, before any exit code queued.</summary>
    public FakeRecordRuns ThenFail(Exception failure)
    {
        lock (_gate) _failures.Enqueue(failure);
        return this;
    }

    /// <summary>Every run in this window so far, with the scope's services it was resolved from, in order.</summary>
    public IReadOnlyList<(RecordRequest Request, IServiceProvider Scope)> Runs
    {
        get { lock (_gate) return [.. _runs]; }
    }

    /// <summary>The requests run in this window so far, in order.</summary>
    public IReadOnlyList<RecordRequest> Requests => [.. Runs.Select(run => run.Request)];

    /// <summary>Queues the exit code the next run answers with.</summary>
    public FakeRecordRuns ThenExit(int exitCode)
    {
        lock (_gate) _exitCodes.Enqueue(exitCode);
        return this;
    }

    /// <summary>
    /// Notes a run of <paramref name="request"/> in the scope of <paramref name="scope"/>, and gives its exit code, or throws
    /// the error queued; Google Drive's part in the scope, when there is one, is told that the run ended with a transcript or
    /// failed.
    /// </summary>
    internal int Run(RecordRequest request, IServiceProvider scope)
    {
        Exception? failure;
        int exitCode;
        lock (_gate)
        {
            _runs.Add((request, scope));
            failure = _failures.TryDequeue(out var queued) ? queued : null;
            exitCode = _exitCodes.TryDequeue(out var code) ? code : 0;
        }

        var kind = failure is null ? OutcomeKind.Transcribed : OutcomeKind.TranscriptFailed;
        scope.GetService<IDriveAutoUpload>()?.Ended(new RunOutcome(kind, failure is null ? exitCode : 1, $"{kind}.", null, null, null));
        return failure is null ? exitCode : throw failure;
    }
}

/// <summary>One run in this window, as <see cref="FakeRecordRuns"/> keeps it: resolved in a scope, and noted there when it runs.</summary>
/// <param name="runs">Where the run is noted.</param>
/// <param name="scope">The services of the scope it was resolved in.</param>
public sealed class FakeRecordRun(FakeRecordRuns runs, IServiceProvider scope) : IRecordRun
{
    /// <inheritdoc />
    public Task<int> RunAsync(RecordRequest request) => Task.FromResult(runs.Run(request, scope));
}
