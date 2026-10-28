using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Link;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// The recorder process: ClassScribe started as <c>ClassScribe.exe --recorder &lt;run id&gt;</c> by a window, with no
/// window of its own, to record one class whatever happens to that window. It takes the request the window wrote, once;
/// builds its host from the request's settings and the key in its own environment, with no console and no
/// appsettings.json; serves the windows that show the run on the run's pipe, and writes its run file, starting, which
/// tells the window the pipe is up; then records at once, with or without a window, through the same run as the window
/// would (see <see cref="IRecordRun"/>), stopping for the keys the windows pass on and for the stop asked for from another
/// window (see <see cref="StopSignal"/>). At the end it sends every window how the run ended and waits a little for them to
/// take it; opens the results itself when no window will; after a transcript, says what it cost the account (see
/// <see cref="UsageReport"/>); last of all, once Drive is set up, uploads the recording to Google Drive, then what waits in
/// the queue (see <see cref="IDriveAutoUpload"/>), so the process lives on for the upload while the next class may already
/// record; and ends with the recorder's own exit codes: 0 done, 1 failed, 3 refused because
/// another recording held the lock, 4 for a request it could not take, 130 cancelled. It never touches the console, and
/// never waits for Enter: nobody could press it.
/// </summary>
public sealed class RecorderHost
{
    /// <summary>The recorder's exit code when it failed before or while it recorded.</summary>
    public const int FailedExitCode = 1;

    /// <summary>The recorder's exit code when it could not take its request: none, one it cannot read, or a run id that is not one.</summary>
    public const int UnreadableRequestExitCode = 4;

    /// <summary>The recorder's exit code when the transcription was cancelled with Ctrl+C from a window.</summary>
    public const int CancelledExitCode = 130;

    /// <summary>How long the end of the run waits for the windows to take it and go.</summary>
    public static readonly TimeSpan EndPatience = TimeSpan.FromSeconds(2);

    /// <summary>
    /// ClassScribe's own folder, where the request, the run file, the lock and recorder.log are: %LOCALAPPDATA%\ClassScribe,
    /// made only when first asked for, unless a test gives another.
    /// </summary>
    public RuntimeFolder Folder
    {
        get => _folder ??= new RuntimeFolder();
        init => _folder = value;
    }

    private RuntimeFolder? _folder;

    /// <summary>
    /// Where the glossary is looked for: the user's folder when it holds a settings file, else the program's, the same
    /// answer as the window's (see <see cref="SettingsPlaces"/>). Program gives the places for this user; a test gives its
    /// own, so that no test looks into the real user folder.
    /// </summary>
    public required SettingsPlaces Places { get; init; }

    /// <summary>The clock the recorder runs on: the computer's, unless a test gives another.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Changes the recorder's services last, as a test does to put stand-ins in place; null for none.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>
    /// True when <paramref name="args"/> start ClassScribe as a recorder: exactly <c>--recorder</c> and a well-formed run
    /// id (<see cref="RunIds.IsWellFormed"/>, amendment 24), as the window starts it, with the id in <paramref name="runId"/>.
    /// Program hands such a start to <see cref="RunAsync(string, string?)"/> before the command line is parsed and before
    /// anything touches the console. Anything else is the command line's, so <c>--recorder</c> with anything but a run id is
    /// a mistake with the help, never a hidden recorder.
    /// </summary>
    public static bool IsRecorderStart(string[] args, [NotNullWhen(true)] out string? runId)
    {
        if (args is [var switchWord, var id] && switchWord == ProcessArguments.Recorder && RunIds.IsWellFormed(id))
        {
            runId = id;
            return true;
        }

        runId = null;
        return false;
    }

    /// <summary>
    /// Runs the recorder for run <paramref name="runId"/>, with <paramref name="apiKey"/> as the value of ELEVENLABS_API_KEY
    /// in its environment, or null when it is not set, and returns the exit code (see the class). Never throws: a failure
    /// is logged into recorder.log, written into the run file when there is one, and becomes exit code 1.
    /// </summary>
    public async Task<int> RunAsync(string runId, string? apiKey)
    {
        using var log = new RecorderLog(Time, Path.Combine(Folder.Root, RecorderLog.StartupFileName));
        using var bootstrap = Loggers(log, settings: null);
        var logger = bootstrap.CreateLogger<RecorderHost>();
        try
        {
            return await RunAsync(runId, apiKey, log, bootstrap, logger);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this is the last stop in the recorder, which has no console for an exception to
            // reach; what happened goes into recorder.log, and the window that started the recorder reads its exit code.
            logger.LogError(ex, "The recorder stopped because of an error");
            return FailedExitCode;
        }
    }

    /// <summary>
    /// The recorder's work (see the class), with <paramref name="log"/> as the log every line goes to, and
    /// <paramref name="bootstrap"/> making the loggers until the request, and its settings, have been read.
    /// </summary>
    private async Task<int> RunAsync(string runId, string? apiKey, RecorderLog log, ILoggerFactory bootstrap, ILogger logger)
    {
        // The id names the request and run files, the pipe and the stop event: one that is not a run id names nothing.
        if (!RunIds.IsWellFormed(runId))
        {
            logger.LogError("\"{RunId}\" is not a ClassScribe run id, so this recorder cannot find its request and records nothing", runId);
            return UnreadableRequestExitCode;
        }

        logger.LogInformation("ClassScribe's recorder started for run {RunId}, as process {ProcessId} of build {Build}, in {Folder}", runId, Environment.ProcessId, BuildInfo.Build, Folder.Root);
        var journal = new RunJournal(Folder, Liveness(bootstrap), Time, bootstrap.CreateLogger<RunJournal>());

        // 1. The request, taken once.
        RecordRequest request;
        IReadOnlyDictionary<string, string?> settings;
        try
        {
            if (journal.TakeRequest(runId) is not { } taken)
            {
                logger.LogWarning("There is no request for run {RunId}: the window that started this recorder gave up waiting for it, so it records nothing", runId);
                return UnreadableRequestExitCode;
            }

            (request, settings) = taken;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the journal has logged it. A request the recorder cannot take leaves it nothing to
            // record; the run file says so, with the reason, for the window that started it, which records the class itself.
            logger.LogError("The recorder cannot take its request ({Reason}), so it records nothing", ex.Message);
            WriteFailed(journal, Starting(runId, PlaceholderRequest(runId), pipe: null), UnreadableRequestExitCode, $"The recorder could not take its request: {ex.Message}", logger);
            return UnreadableRequestExitCode;
        }

        // From here the lines follow the Logging section the window handed over, as the host's lines will: recorder.log
        // then reads the same before and after the host exists.
        using var configured = Loggers(log, settings);
        logger = configured.CreateLogger<RecorderHost>();
        journal = new RunJournal(Folder, Liveness(configured), Time, configured.CreateLogger<RunJournal>());

        Prune(journal, logger);

        // 2. The host, from the request's settings and the key.
        IHost host;
        try
        {
            host = AppHost.BuildRecorder(new RecorderStart(runId, request, settings), apiKey, log, Folder, Places, Time, ConfigureServices);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a recorder that cannot be put together records nothing; the run file says why, for
            // the window that started it, which records the class itself.
            logger.LogError(ex, "The recorder could not be put together, so it records nothing");
            WriteFailed(journal, Starting(runId, request, pipe: null), FailedExitCode, $"The recorder could not be put together: {Reasons(ex)}", logger);
            return FailedExitCode;
        }

        try
        {
            return await RunWithAsync(host, runId, request, log);
        }
        finally
        {
            if (host is IAsyncDisposable disposable) await disposable.DisposeAsync();
            else host.Dispose();
        }
    }

    /// <summary>Steps 3 to 8: the server, the run file, the run in its scope, the end, the transcript's cost and the upload to Google Drive, with the host built.</summary>
    private async Task<int> RunWithAsync(IHost host, string runId, RecordRequest request, RecorderLog log)
    {
        var services = host.Services;
        var logger = services.GetRequiredService<ILogger<RecorderHost>>();
        var journal = services.GetRequiredService<RunJournal>();
        var server = services.GetRequiredService<RecorderServer>();
        var pipe = LinkProtocol.PipeNameFor(runId);
        var starting = Starting(runId, request, pipe);
        RunHandle? handle = null;

        // 3. The pipe, before the run file: a run file that says starting tells the window that the pipe is up.
        try
        {
            server.Start(pipe, () => handle?.Current ?? starting);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Deliberately not rethrown: the server has logged it. A recorder its windows cannot reach does not record; the
            // run file says why, and the window that started it records the class itself.
            WriteFailed(journal, starting, FailedExitCode, $"The recorder could not make the pipe {pipe} for ClassScribe's windows ({ex.Message}), so it did not record", logger);
            await server.DisposeAsync();
            return FailedExitCode;
        }

        try
        {
            // 4. The run file, starting.
            try
            {
                handle = journal.Begin(starting);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: the journal has logged it. Without its run file the window can neither find the
                // recorder nor learn how it ended, so the recorder does not record, and the window records the class itself.
                logger.LogError("The recorder could not write its run file ({Reason}), so it did not record", ex.Message);
                return FailedExitCode;
            }

            using (handle)
            {
                return await RecordAsync(services, runId, request, pipe, handle, server, logger);
            }
        }
        finally
        {
            log.EndFile();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Steps 5 to 8: the run in a scope of its own, with the keys and Ctrl+C from the windows and the stop from another
    /// window wired to it; then the end to every window, and the results opened here when no window will; then, after a
    /// transcript, what it cost the account, which the run leaves to this step so that no read of the account holds the
    /// end up (see <see cref="UsageReport"/>); and last the upload to Google Drive, of the recording and then of the queue,
    /// which never changes the outcome or the exit code (see <see cref="IDriveAutoUpload"/>). The scope is still open, so
    /// the lines go into the recording's log and recorder.log; the window that was shown the end has left by then.
    /// </summary>
    private static async Task<int> RecordAsync(IServiceProvider services, string runId, RecordRequest request, string pipe, RunHandle handle, RecorderServer server, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var run = scope.ServiceProvider;
        run.GetRequiredService<RunContextSlot>().Fill(new RunContext(runId, RunHome.Separate, pipe) { Run = handle });
        var signals = run.GetRequiredService<IUserSignals>();
        services.GetRequiredService<UnattendedAlerts>().Start(runId);
        using var stop = ListenForStop(runId, signals, logger);
        Action ctrlC = signals.CtrlCPressed;
        server.CtrlCPressed += ctrlC;

        int exitCode;
        string? error = null;
        try
        {
            exitCode = await run.GetRequiredService<IRecordRun>().RunAsync(request);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: Ctrl+C from a window during the upload is a normal way to end, which the run has
            // written as its outcome; the recorder's exit code says cancelled.
            logger.LogWarning("Cancelled");
            exitCode = CancelledExitCode;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this is where a failure of the run becomes this line, the outcome in the run file
            // when the run wrote none, and exit code 1, all of which the windows and the next start of ClassScribe show.
            logger.LogError(ex, "The recording stopped because of an error");
            exitCode = FailedExitCode;
            error = ex.Message;
        }
        finally
        {
            server.CtrlCPressed -= ctrlC;
        }

        // 6. The end: to the windows, which open the results; or opened here when none took it.
        var outcome = handle.Current.Outcome ?? OutcomeWithout(handle, exitCode, error);
        var taken = await server.EndAsync(outcome, request.Open, EndPatience);
        if (!taken) run.GetRequiredService<RecorderResultShower>().OpenResults();

        // 7. What the transcript cost: only a transcript was paid for, and the windows have the end.
        if (outcome.Kind == OutcomeKind.Transcribed) await run.GetRequiredService<UsageReport>().ReportAsync(signals.Cancellation);

        // 8. Google Drive, last of all: the recording, then the queue, while the run's scope is still open, so the lines go
        // into the recording's log. It never changes the outcome or the exit code (see IDriveAutoUpload).
        await run.GetRequiredService<IDriveAutoUpload>().AfterRunAsync();
        logger.LogDebug("The recorder ends with exit code {ExitCode}", exitCode);
        return exitCode;
    }

    /// <summary>
    /// Listens for the stop asked for from another window, through the run's stop event, and makes it stop the run as Q
    /// pressed twice does; null when the event cannot be made, which the signal has logged, and the run goes on without it.
    /// </summary>
    private static IDisposable? ListenForStop(string runId, IUserSignals signals, ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            return StopSignal.Listen(runId, signals.RequestStop, logger);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Deliberately not rethrown: the signal has logged that something else made an event of that name, and said
            // that Q pressed twice in a window still stops the recording; the class is recorded all the same.
            return null;
        }
    }

    /// <summary>
    /// How the run ended when it wrote no outcome: Windows ended the session, which the run file keeps as it is, recording,
    /// for the next start of ClassScribe to report, so this outcome goes to the windows only; otherwise the run stopped
    /// because of an error, or was cancelled, which the run file is told as well.
    /// </summary>
    private static RunOutcome OutcomeWithout(RunHandle handle, int exitCode, string? error)
    {
        var current = handle.Current;
        var kept = current.Folder is { } folder ? $"The audio recorded so far is kept in {folder}." : "Nothing was recorded.";
        if (current.EndedBy == RunEndCause.Windows)
            return new RunOutcome(OutcomeKind.RecorderFailed, exitCode, $"Windows ended the session, so the recording stopped. {kept} The next start of ClassScribe reports it", null, current.Folder, null);

        var outcome = exitCode == CancelledExitCode
            ? new RunOutcome(OutcomeKind.Cancelled, exitCode, $"The run was cancelled. {kept}", null, current.Folder, null)
            : new RunOutcome(OutcomeKind.RecorderFailed, exitCode, $"The recording stopped because of an error{(error is null ? "" : $": {error.TrimEnd('.')}")}. {kept}", null, current.Folder, null);
        handle.End(outcome);
        return outcome;
    }

    /// <summary>Writes a run file for <paramref name="starting"/> that says the recorder failed, with <paramref name="message"/> and <paramref name="exitCode"/>, for the window that started it.</summary>
    private static void WriteFailed(RunJournal journal, RunRecord starting, int exitCode, string message, ILogger logger)
    {
        try
        {
            using var handle = journal.Begin(starting);
            handle.End(new RunOutcome(OutcomeKind.RecorderFailed, exitCode, message, TranscriptPath: null, Folder: null, RetryCommand: null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the journal has logged it, and the failure being written is the one to report; the
            // window that started the recorder then reads its exit code and the last lines of recorder.log instead.
            logger.LogDebug(ex, "Could not write into the run file that the recorder failed");
        }
    }

    /// <summary>Deletes the run files that have nothing left to report, as every recorder does when it starts, so that they never pile up.</summary>
    private void Prune(RunJournal journal, ILogger logger)
    {
        try
        {
            journal.Prune(RunJournal.KeepSeenRunsFor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: pruning is housekeeping, which the next recorder tries again, and must not cost the class.
            logger.LogWarning(ex, "Could not delete the old run files in {Folder}; the next recorder tries again", Folder.Runs);
        }
    }

    /// <summary>The run file of run <paramref name="runId"/> as it begins: this process, starting, with <paramref name="pipe"/> for its windows.</summary>
    private RunRecord Starting(string runId, RecordRequest request, string? pipe)
    {
        var now = Time.GetUtcNow();
        using var process = Process.GetCurrentProcess();
        return new RunRecord(RunRecord.CurrentVersion, runId, process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            Environment.ProcessPath ?? AppContext.BaseDirectory, BuildInfo.Build, LinkProtocol.Version, pipe, RunHome.Separate, request, BaseName: null, Folder: null,
            RunState.Starting, now, now, TimeSpan.Zero, UploadPercent: null, EndedBy: null, Outcome: null, Seen: false);
    }

    /// <summary>A request that stands for one the recorder could not take, so that its run file can say that it failed: a run file needs one.</summary>
    private RecordRequest PlaceholderRequest(string runId) =>
        new(runId, "unknown", Transcribe: false, new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), Microphone: null), Folder.Root,
            ContinueChoice.New, ContinueBaseName: null, Minutes: null, Open: false);

    /// <summary>
    /// Loggers that write into <paramref name="log"/> alone, the lines at the levels the Logging section of
    /// <paramref name="settings"/> names, as the host's loggers do; before the settings are known, with null, at the usual
    /// levels, Information and above.
    /// </summary>
    private static ILoggerFactory Loggers(RecorderLog log, IReadOnlyDictionary<string, string?>? settings) =>
        LoggerFactory.Create(logging =>
        {
            if (settings is not null)
                logging.AddConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection("Logging"));
            logging.AddProvider(new RecorderLogProvider(log));
        });

    /// <summary>Whether a process still runs, from Windows: the recorder runs on Windows only.</summary>
    private IProcessLiveness Liveness(ILoggerFactory loggers) => OperatingSystem.IsWindows()
        ? new ProcessLiveness(Time, loggers.CreateLogger<ProcessLiveness>())
        : throw new PlatformNotSupportedException("The recorder runs on Windows only.");

    /// <summary>What went wrong, in the words of each exception in the chain.</summary>
    private static string Reasons(Exception failure)
    {
        var reasons = new List<string>();
        for (var exception = failure; exception is not null; exception = exception.InnerException) reasons.Add(exception.Message.TrimEnd('.'));
        return string.Join(". ", reasons);
    }
}
