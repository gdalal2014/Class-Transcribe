using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>What was decided about continuing a recording of the same name that stopped recently without a transcript.</summary>
/// <param name="GoBack">True when Esc was pressed in the menu's question: nothing is to start.</param>
/// <param name="Continue">The recording to continue as its next part; null for a new recording.</param>
/// <param name="Hint">A line to show before the run starts, such as how to continue the other recording instead; null for none.</param>
public sealed record ContinueAnswer(bool GoBack, RecordingFiles? Continue, string? Hint);

/// <summary>
/// Whether a new run continues, as its next part, a recording of the same name that stopped recently without a transcript,
/// as after a crash, a closed window or a restart (section 1.14 of the design). The menu asks: C continues it, N starts a
/// new recording, Esc goes back. The command line never asks and never waits: --continue continues it, or says there is
/// none and starts a new one; --new starts a new one without a word; neither starts a new one, with a line on how to
/// continue the other one instead. Only a recording that may be continued is ever offered (see <see cref="Find"/>).
/// </summary>
public sealed class ContinueQuestion(ClassFolders folders, RunJournal journal, IConsoleUi ui, IOptionsSnapshot<RecordingOptions> recording,
    TimeProvider time, ILogger<ContinueQuestion> logger)
{
    /// <summary>
    /// The menu's question, for a run of <paramref name="name"/> recording what <paramref name="mode"/> names, with the camera
    /// when <paramref name="camera"/>, into <paramref name="root"/>: when there is a recording to continue, it is described
    /// and C, N or Esc is read, any other key passed over; Ctrl+C goes back as Esc does. With none, nothing is asked and a new
    /// recording starts.
    /// </summary>
    public async Task<ContinueAnswer> AskAsync(string name, RecordingMode mode, string root, bool camera = false)
    {
        if (Find(name, mode, root, camera) is not { } candidate) return NewRecording;

        ui.WriteLine($"{candidate.Files.BaseName} stopped {Ago(candidate.StoppedUtc)} ({Durations.Format(candidate.Length)} recorded{InParts(candidate)}, no transcript yet). "
                     + "C continues it as its next part, N starts a new recording, Esc goes back.");
        while (true)
        {
            if (await ui.ReadKeyAsync(CancellationToken.None) is not { } key || key.Key == ConsoleKey.Escape || IsCtrlC(key)) return GoBack;

            switch (key.Key)
            {
                case ConsoleKey.C:
                    return Continuing(candidate);
                case ConsoleKey.N:
                    return NewRecording;
            }
        }
    }

    /// <summary>
    /// The command line's answer, for <paramref name="choice"/>, which never asks: --continue (<see cref="ContinueChoice.Continue"/>)
    /// continues the recording, or says there is none and starts a new one; --new starts a new one without a word; neither
    /// (<see cref="ContinueChoice.Ask"/>) starts a new one, with a line saying how to continue the other one instead. With
    /// <paramref name="camera"/>, only a lesson recorded with the camera is continued.
    /// </summary>
    public ContinueAnswer Resolve(string name, RecordingMode mode, string root, ContinueChoice choice, bool camera = false)
    {
        if (choice == ContinueChoice.New) return NewRecording;

        var candidate = Find(name, mode, root, camera);
        if (choice == ContinueChoice.Continue)
        {
            if (candidate is not null) return Continuing(candidate);

            var within = Within();
            return NewRecording with
            {
                Hint = within <= TimeSpan.Zero
                    ? "Recording:ContinueWithinMinutes is 0, so no recording is continued; a new recording starts."
                    : $"There is no recording of \"{name}\" to continue (one that stopped in the last {Minutes(within)} without a transcript), so a new recording starts."
            };
        }

        return candidate is null
            ? NewRecording
            : NewRecording with { Hint = $"{candidate.Files.BaseName} stopped {Ago(candidate.StoppedUtc)} without a transcript; to add to it instead, run the same command with --continue." };
    }

    /// <summary>
    /// The recording a new run of <paramref name="name"/> may continue, recording what <paramref name="mode"/> names, with the
    /// camera when <paramref name="camera"/>, under <paramref name="root"/>, within Recording:ContinueWithinMinutes; null when
    /// there is none (see <see cref="FindContinuable"/>).
    /// </summary>
    public ContinueCandidate? Find(string name, RecordingMode mode, string root, bool camera = false) =>
        FindContinuable(folders, journal, name, mode, Within(), root, logger, ifUnreadable: "a new recording starts", camera);

    /// <summary>
    /// The recording a new run of <paramref name="name"/> may continue: the one <see cref="ClassFolders.FindContinuable"/>
    /// offers, the newest of the name, stopped no more than <paramref name="within"/> ago without a transcript, unless the
    /// run files hold it back, which see what the files cannot: a run that still owns it, recording, stopping or being
    /// transcribed, and a run that recorded it with other tracks, such as in person where this run records the class, both a
    /// single track on disk. Null when there is none. A recording that cannot be looked at now never keeps a class from
    /// being recorded, nor a report from being made: the failure is logged to <paramref name="logger"/> as a warning that
    /// ends with <paramref name="ifUnreadable"/>, what the caller does instead, such as "a new recording starts", and
    /// there is none to continue. A run with the camera (<paramref name="camera"/>) continues only a lesson recorded with
    /// it, and a run without only a recording without it, by one rule: the run files decide, and a lesson whose run asked for
    /// the camera is continued with it even when it holds no piece, the new part writing the first; when no run file of the
    /// recording is left, the camera's pieces decide.
    /// </summary>
    internal static ContinueCandidate? FindContinuable(ClassFolders folders, RunJournal journal, string name, RecordingMode mode, TimeSpan within, string root,
        ILogger logger, string ifUnreadable, bool camera = false)
    {
        ContinueCandidate? candidate;
        try
        {
            candidate = folders.FindContinuable(name, mode, within, root, camera);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: continuing is an offer, and a recording that cannot be read now, such as one another
            // program holds open, must not keep the class from being recorded or the report from being made; the caller goes
            // on without it, and this says why and what it does instead.
            logger.LogWarning(ex, "Could not look for a recording of \"{Name}\" to continue; {Instead}", name, ifUnreadable);
            return null;
        }

        if (candidate is null) return null;

        var runs = journal.List().Where(run => Records(run.Record, candidate.Files)).ToList();

        // With no run file left, the files decide: a lesson with pieces is the camera's (the files refused it to a run without),
        // and one without is not.
        if (runs.Count == 0 && camera && !candidate.Files.FindCameraPieces().Any())
        {
            logger.LogDebug("{Recording} is not offered to be continued: it holds no piece of the camera's and no run file says it was recorded with the camera, and this run records the camera",
                candidate.Files.BaseName);
            return null;
        }

        foreach (var run in runs)
        {
            if (run.IsAlive)
            {
                logger.LogDebug("{Recording} is not offered to be continued: run {RunId} still owns it ({State})", candidate.Files.BaseName, run.Record.RunId, run.Record.State);
                return null;
            }

            if (run.Record.Request.Source.Mode != mode)
            {
                logger.LogDebug("{Recording} is not offered to be continued: it was recorded as {Mode}, and this run records {This}", candidate.Files.BaseName,
                    run.Record.Request.Source.Mode, mode);
                return null;
            }

            if (run.Record.Request.Source.Camera is not null != camera)
            {
                logger.LogDebug("{Recording} is not offered to be continued: it was recorded {With} the camera, and this run records {This}", candidate.Files.BaseName,
                    camera ? "without" : "with", camera ? "the camera" : "no camera");
                return null;
            }
        }

        return candidate;
    }

    /// <summary>True when <paramref name="run"/> recorded into <paramref name="files"/>: the same base name in the same folder.</summary>
    internal static bool Records(RunRecord run, RecordingFiles files) =>
        string.Equals(run.BaseName, files.BaseName, StringComparison.OrdinalIgnoreCase)
        && run.Folder is { } folder
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(files.Folder)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>A length of time in minutes as words: "1 minute", "60 minutes".</summary>
    internal static string Minutes(TimeSpan length)
    {
        var minutes = (int)Math.Floor(length.TotalMinutes);
        return minutes == 1 ? "1 minute" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes");
    }

    /// <summary>The answer for a new recording, with nothing to say.</summary>
    private static ContinueAnswer NewRecording { get; } = new(GoBack: false, Continue: null, Hint: null);

    /// <summary>The answer for Esc: nothing starts.</summary>
    private static ContinueAnswer GoBack { get; } = new(GoBack: true, Continue: null, Hint: null);

    /// <summary>The answer that continues <paramref name="candidate"/>, saying so.</summary>
    private static ContinueAnswer Continuing(ContinueCandidate candidate) =>
        new(GoBack: false, candidate.Files, $"Continuing {candidate.Files.BaseName} as its next part ({Durations.Format(candidate.Length)} recorded so far).");

    /// <summary>" in 2 parts" for a recording continued before; empty for one made in one go.</summary>
    private static string InParts(ContinueCandidate candidate) =>
        candidate.Parts > 1 ? string.Create(CultureInfo.InvariantCulture, $" in {candidate.Parts} parts") : "";

    /// <summary>True for Ctrl+C, which leaves the question as Esc does.</summary>
    private static bool IsCtrlC(ConsoleKeyInfo key) => key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control);

    /// <summary>How recently a recording must have stopped to be offered: Recording:ContinueWithinMinutes; zero never offers one.</summary>
    private TimeSpan Within() => TimeSpan.FromMinutes(Math.Max(0, recording.Value.ContinueWithinMinutes));

    /// <summary>How long ago <paramref name="stoppedUtc"/> was, in words: "less than a minute ago", "4 minutes ago", "1 hour 5 minutes ago".</summary>
    private string Ago(DateTimeOffset stoppedUtc)
    {
        var ago = time.GetUtcNow() - stoppedUtc;
        if (ago < TimeSpan.FromMinutes(1)) return "less than a minute ago";
        if (ago < TimeSpan.FromHours(1)) return $"{Minutes(ago)} ago";

        var hours = (int)ago.TotalHours;
        var minutes = ago.Minutes;
        var hoursText = hours == 1 ? "1 hour" : string.Create(CultureInfo.InvariantCulture, $"{hours} hours");
        return minutes == 0 ? $"{hoursText} ago" : $"{hoursText} {Minutes(TimeSpan.FromMinutes(minutes))} ago";
    }
}
