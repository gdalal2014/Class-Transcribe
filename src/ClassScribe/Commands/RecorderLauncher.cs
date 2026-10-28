using System.ComponentModel;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>Where a run records: in a separate recorder in the background, or in this window, and why.</summary>
public abstract record RunHomeChoice
{
    /// <summary>Only the kinds declared here derive from it.</summary>
    private RunHomeChoice()
    {
    }

    /// <summary>In a separate recorder, which closing this window does not stop.</summary>
    /// <param name="Breakaway">True to start the recorder outside the job ClassScribe runs in (see <see cref="JobFacts.Breakaway"/>).</param>
    public sealed record Separate(bool Breakaway) : RunHomeChoice;

    /// <summary>In this window, where closing the window ends the recording and keeps the audio so far.</summary>
    /// <param name="Reason">Why, in words Gulzar reads, ending with what closing the window does.</param>
    public sealed record InWindow(string Reason) : RunHomeChoice;
}

/// <summary>How starting a separate recorder went.</summary>
public abstract record LaunchResult
{
    /// <summary>Only the kinds declared here derive from it.</summary>
    private LaunchResult()
    {
    }

    /// <summary>
    /// The recorder has started: its run file says it records, or it is still starting after the time allowed, which it
    /// says only once its pipe is up, so that a window can show it.
    /// </summary>
    /// <param name="Run">The run as the recorder's own run file describes it.</param>
    public sealed record Started(RunRecord Run) : LaunchResult;

    /// <summary>Another recording took the lock first, and the recorder ended without recording.</summary>
    /// <param name="Winner">The recording that runs.</param>
    public sealed record Refused(RunView Winner) : LaunchResult;

    /// <summary>The recorder did not start, or ended before it answered.</summary>
    /// <param name="Reason">Why, in words Gulzar reads: Windows' refusal, or the recorder's own reason, exit code and last lines in recorder.log.</param>
    public sealed record Failed(string Reason) : LaunchResult;
}

/// <summary>
/// Decides where a run records (table 1.3 of the design) and starts the separate recorder. A run records in this window
/// when Recording:SeparateProcess is false; when ClassScribe runs in a job that would end a recorder together with this
/// window, as under dotnet run; and when ClassScribe's own folder cannot be written. Otherwise it records in a recorder,
/// this very program started again with --recorder and the run's id: the request file first, then the start, outside the
/// job where the job allows it and inside it again only where the recorder outlives the window there too, then the wait
/// until the recorder's run file says it records. Closing this window is said to be safe only where it is.
/// </summary>
public sealed class RecorderLauncher(IProcessStarter starter, IJobInspector jobs, IRuntimeFolder folder, IRunJournal journal, IRunLog log,
    IOptionsMonitor<RecordingOptions> options, TimeProvider time, ILogger<RecorderLauncher> logger)
{
    /// <summary>How long a recorder has to answer, by its run file, before the start counts as failed: 20 seconds, as for a window connecting to it.</summary>
    public static readonly TimeSpan AnswerPatience = TimeSpan.FromSeconds(20);

    /// <summary>What closing the window does to a run in this window, said whenever a run records there.</summary>
    public const string ClosingEndsIt = "So this recording runs in this window: closing it ends the recording, and the audio so far is kept.";

    /// <summary>Row 1: Recording:SeparateProcess is false.</summary>
    public const string SeparateProcessOff = "Recording in this window (Recording:SeparateProcess is false): closing it ends the recording; the audio so far is kept.";

    /// <summary>Row 2: a job that ends every process in it when it closes, and lets none out, as under dotnet run.</summary>
    public const string EndsWithItsStarter =
        "ClassScribe was started in a way that ends every program it starts when it ends, as dotnet run does. " + ClosingEndsIt
        + " For classes, start ClassScribe.exe itself (README, Set up once).";

    /// <summary>Row 3, after the reason: what is missing when ClassScribe's own folder cannot be written.</summary>
    public const string WithoutBookkeeping = "One recording at a time and the report after a crash are not available this time.";

    /// <summary>How often the run file is looked at while the recorder starts.</summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>How many of the last lines of recorder.log a failure quotes.</summary>
    private const int QuotedLogLines = 5;

    /// <summary>The recorder's exit code when another recording held the lock and it did not start (section 1.11 of the design).</summary>
    private const int RefusedExitCode = 3;

    /// <summary>
    /// Where the run records, checked in the order of table 1.3: Recording:SeparateProcess; the job ClassScribe runs in,
    /// which must let a recorder outlive this window, by breaking away where it allows that; and ClassScribe's own folder,
    /// which must be writable for the request, the run file and the lock. The settings are read as they are now.
    /// </summary>
    public RunHomeChoice ChooseHome()
    {
        if (!options.CurrentValue.SeparateProcess)
        {
            logger.LogDebug("Recording in this window: Recording:SeparateProcess is false");
            return new RunHomeChoice.InWindow(SeparateProcessOff);
        }

        var facts = jobs.Inspect();
        if (!facts.RecorderOutlivesWindow(facts.Breakaway))
        {
            logger.LogDebug("Recording in this window: the job ClassScribe runs in would end a recorder with this window ({Facts})", facts);
            return new RunHomeChoice.InWindow(EndsWithItsStarter);
        }

        if (folder.DescribeWriteProblem() is { } problem)
        {
            logger.LogDebug("Recording in this window: {Problem}", problem);
            return new RunHomeChoice.InWindow($"{Sentence.Of(problem)} {ClosingEndsIt} {WithoutBookkeeping}");
        }

        logger.LogDebug("Recording in a separate recorder ({Facts})", facts);
        return new RunHomeChoice.Separate(facts.Breakaway);
    }

    /// <summary>
    /// Starts the recorder of <paramref name="request"/>'s run with <paramref name="settings"/>, the checked settings it
    /// records with (see <see cref="SettingsReloader.Snapshot"/>), and waits until it answers. The request file is written
    /// first. The recorder starts outside the job where the job allows it; when Windows refuses that, it starts inside the
    /// job only where it outlives this window there too. It has started once its run file says it records, or after
    /// <see cref="AnswerPatience"/> still says it starts, which it writes only once its pipe is up. It was refused when its
    /// run file says another recording held the lock, and the one recording now is handed back. Anything else is a
    /// failure with the reason: Windows' refusal, the recorder's own reason and exit code, its last lines in recorder.log,
    /// or no answer in time. A request nobody took is removed, so that a recorder that wakes up late finds nothing to do. A
    /// run that failed or was refused is marked seen, since the caller says what happened.
    /// </summary>
    public async Task<LaunchResult> StartAsync(RecordRequest request, IReadOnlyDictionary<string, string?> settings, CancellationToken cancellationToken)
    {
        var runId = request.RunId;
        try
        {
            journal.WriteRequest(request, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: without its request no recorder can start, and the caller records in this window
            // instead, saying this reason.
            logger.LogDebug(ex, "Could not hand run {RunId} its request; the recorder is not started", runId);
            return new LaunchResult.Failed($"The request for the recorder could not be written: {Sentence.Of(ex.Message)}");
        }

        var facts = jobs.Inspect();
        var (process, refusal) = TryStart(runId, facts.Breakaway);
        if (process is null && facts.Breakaway && refusal?.NativeErrorCode == IProcessStarter.AccessDenied)
        {
            if (!facts.RecorderOutlivesWindow(breakaway: false))
            {
                DiscardRequest(runId);
                return new LaunchResult.Failed(
                    $"Windows did not let the recorder start outside the job ClassScribe runs in ({Words(refusal.NativeErrorCode)}), and inside it the recorder would end with this window.");
            }

            logger.LogDebug("Windows refused the breakaway; starting the recorder inside the job, which does not end it with this window");
            (process, refusal) = TryStart(runId, breakaway: false);
        }

        if (process is null)
        {
            DiscardRequest(runId);
            return new LaunchResult.Failed(Sentence.Of(refusal!.Message));
        }

        logger.LogDebug("Started the recorder of run {RunId}: process {ProcessId}", runId, process.ProcessId);
        return await AwaitAnswerAsync(runId, process, cancellationToken);
    }

    /// <summary>Windows' words for <paramref name="error"/>, and the code: "Access is denied, error 5".</summary>
    private static string Words(int error) => $"{new Win32Exception(error).Message.TrimEnd('.', ' ')}, error {error}";

    /// <summary>Starts the recorder of <paramref name="runId"/>, outside the job when <paramref name="breakaway"/> is true; or gives Windows' refusal.</summary>
    private (StartedProcess? Process, Win32Exception? Refusal) TryStart(string runId, bool breakaway)
    {
        try
        {
            return (starter.StartRecorder(runId, breakaway), null);
        }
        catch (Win32Exception ex)
        {
            // Deliberately not rethrown: a recorder Windows will not start is a reason to start it another way or to record
            // in this window instead, which the caller decides; the refusal goes back with Windows' words and error.
            logger.LogDebug(ex, "Windows did not start the recorder of run {RunId} (outside the job: {Breakaway})", runId, breakaway);
            return (null, ex);
        }
    }

    /// <summary>
    /// Waits until the recorder answers through its run file, ends, or runs out of <see cref="AnswerPatience"/>. Its exit
    /// code is read before its run file each time, so that whatever it wrote before it ended is in the file read.
    /// </summary>
    private async Task<LaunchResult> AwaitAnswerAsync(string runId, StartedProcess process, CancellationToken cancellationToken)
    {
        var began = time.GetTimestamp();
        while (true)
        {
            var exitCode = process.ExitCode();
            var run = journal.Find(runId);
            if (run is not null && Answer(run, exitCode) is { } answer) return answer;
            if (exitCode is { } ended) return Ended(runId, process, ended, run);

            if (time.GetElapsedTime(began) >= AnswerPatience)
            {
                if (run is not null)
                {
                    logger.LogDebug("The recorder of run {RunId} still says it starts; the view of the run takes over", runId);
                    return new LaunchResult.Started(run.Record);
                }

                DiscardRequest(runId);
                return new LaunchResult.Failed(
                    $"The recorder (process {process.ProcessId}) did not answer within {AnswerPatience.TotalSeconds:0} seconds.{LastLogLines(process)}");
            }

            await Task.Delay(LookEvery, time, cancellationToken);
        }
    }

    /// <summary>What the recorder's run file says about its start, with its exit code when it has ended; null while it says it starts.</summary>
    private LaunchResult? Answer(RunView run, int? exitCode)
    {
        switch (run.Record.State)
        {
            case RunState.Starting:
                return null;

            case RunState.Refused:
                return Refusal(run.Record.RunId);

            case RunState.Failed:
                journal.MarkSeen(run.Record.RunId);
                var reason = Sentence.Of(run.Record.Outcome?.Message is { Length: > 0 } message ? message : "The recorder failed");
                return new LaunchResult.Failed(exitCode is { } code ? $"{reason} The recorder ended with exit code {code}." : reason);

            default:
                logger.LogDebug("The recorder of run {RunId} answered: {State}", run.Record.RunId, run.Record.State);
                return new LaunchResult.Started(run.Record);
        }
    }

    /// <summary>
    /// The recorder of <paramref name="runId"/> ended with <paramref name="exitCode"/> before its run file said anything
    /// but that it starts: refused by the lock (exit code 3), or ended with its last lines in recorder.log as the reason.
    /// </summary>
    private LaunchResult Ended(string runId, StartedProcess process, int exitCode, RunView? run)
    {
        if (exitCode == RefusedExitCode) return Refusal(runId);

        DiscardRequest(runId);
        journal.MarkSeen(runId);
        var when = run is null ? "before it answered" : "before it recorded";
        return new LaunchResult.Failed($"The recorder ended {when}, with exit code {exitCode}.{LastLogLines(process)}");
    }

    /// <summary>
    /// The recorder of <paramref name="runId"/> was refused because another recording held the lock: that recording, when
    /// it still records; otherwise a failure, so that the start can be tried again.
    /// </summary>
    private LaunchResult Refusal(string runId)
    {
        journal.MarkSeen(runId);
        if (journal.List().FirstOrDefault(other => other.IsRecordingNow && other.Record.RunId != runId) is { } winner)
        {
            logger.LogDebug("The recorder of run {RunId} was refused: run {Winner} records", runId, winner.Record.RunId);
            return new LaunchResult.Refused(winner);
        }

        return new LaunchResult.Failed("Another recording was running when this one started, and it has ended since.");
    }

    /// <summary>
    /// The last lines of recorder.log, where a recorder writes until its recording's folder is known, as a new paragraph,
    /// when the file was written since <paramref name="process"/> started; empty otherwise, or when it cannot be read.
    /// </summary>
    private string LastLogLines(StartedProcess process)
    {
        var path = Path.Combine(folder.Root, IRunLog.StartupFileName);
        try
        {
            var lines = log.LastStartupLines(path, process.StartedUtc - IProcessLiveness.StartTimeTolerance, QuotedLogLines);
            return lines.Count == 0 ? "" : $"{Environment.NewLine}The last lines of {path}:{string.Concat(lines.Select(line => $"{Environment.NewLine}  {line}"))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the lines only add to a reason that is given anyway.
            logger.LogDebug(ex, "Could not read the last lines of {Path}", path);
            return "";
        }
    }

    /// <summary>
    /// Removes the request of <paramref name="runId"/> when no recorder took it, so that one that wakes up late finds nothing
    /// to record. A request that cannot be removed now is left to the pruning of old runs.
    /// </summary>
    private void DiscardRequest(string runId)
    {
        try
        {
            if (journal.TakeRequest(runId) is not null) logger.LogDebug("Removed the request of run {RunId}, which no recorder took", runId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Deliberately not rethrown: the request is housekeeping; the journal has logged why it could not be read, and
            // pruning removes it at a later start.
            logger.LogDebug(ex, "Could not remove the request of run {RunId}", runId);
        }
    }
}
