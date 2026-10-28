using System.Globalization;
using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Commands;

/// <summary>
/// The list command: every recording under the recordings folder, in the course and date folders or flat in the folder of
/// its name as recordings were kept before, newest first, one line each, with when it started in local time (an evening
/// class in US Eastern time has a UTC stamp on the next day), its length, the date folder of a later class of its day, the
/// parts it was made in, whether your microphone was recorded with the class, its transcripts, and what its run is doing
/// now; then the transcriptions going on in the background. For example:
/// <code>
///   Kalam  15 Oct 19:00  01:58:10  2 parts  class + microphone  transcript (2)
///   Kalam  15 Oct 13:00  00:58:10  in 2026-10-15 (2)  transcript
///   Fiqh   14 Oct 19:30  00:42:13  no transcript yet  RECORDING NOW
/// </code>
/// It only reads: nothing is recorded, uploaded or changed, and no run file is marked seen.
/// </summary>
public sealed class ListCommand(IClassFolders folders, RunJournal journal, IConsoleUi ui, TimeProvider time)
{
    /// <summary>How the start of a recording is written in the list, in local time: 15 Oct 19:00.</summary>
    private const string StartFormat = "d MMM HH:mm";

    /// <summary>
    /// Lists the recordings under <paramref name="root"/>, the folder given with --output, or under Recording:OutputFolder
    /// when it is null, then the transcriptions in the background, wherever their recordings are. A folder that is not
    /// there yet holds no recordings. Returns the exit code, 0.
    /// </summary>
    public int Run(string? root)
    {
        var listed = string.IsNullOrWhiteSpace(root) ? folders.Root : folders.RootFor(root);
        var recordings = folders.ListAll(root);
        var runs = journal.List();

        if (recordings.Count == 0)
        {
            ui.WriteLine($"No recordings in {listed}.");
        }
        else
        {
            ui.WriteLine($"Recordings in {listed}, newest first (times are local):");
            var rows = recordings.Select(recorded => (Recorded: recorded, Started: StartOf(recorded.Files))).ToList();
            var nameWidth = rows.Max(row => row.Recorded.Files.Name.Length);
            var startWidth = rows.Max(row => row.Started.Length);
            foreach (var (recorded, started) in rows)
            {
                string?[] columns =
                [
                    recorded.Files.Name.PadRight(nameWidth), started.PadLeft(startWidth), Durations.Format(recorded.Length),
                    .. NotesOf(recorded), TranscriptOf(recorded), StateOf(recorded, NewestRunOf(recorded.Files, runs))
                ];
                ui.WriteLine($"  {string.Join("  ", columns.OfType<string>())}");
            }
        }

        foreach (var transcribing in runs.Where(run => run.IsTranscribingNow).Select(run => run.Record))
        {
            var progress = transcribing.UploadPercent is { } percent ? $" (uploading {percent}%)" : "";
            ui.WriteLine($"Being transcribed in the background: \"{transcribing.Request.Name}\"{progress}.");
        }

        return 0;
    }

    /// <summary>
    /// What sets a recording apart besides its name, start and length, as the list and the menu's choice 3 note it: the
    /// date folder of a later class of its day ("in 2026-10-17 (2)"), which tells it from the day's first class and says
    /// where to find it; the parts it was made in when there were more than one ("2 parts"); "class + microphone" when
    /// your microphone was recorded with the class; "lesson video", or "lesson video not made yet" while a lesson's camera
    /// pieces wait to be joined with its sound (see <see cref="IClassFolders.LessonVideoStateOf"/>); and "audio on Drive"
    /// when its audio is on Google Drive alone, freed from this laptop once checked there. A YouTube video's recording is
    /// first named by the video's title and how its audio was obtained ("The creed, part 1" (YouTube, downloaded)), so that
    /// a batch's videos in one date folder are told apart.
    /// </summary>
    public static IEnumerable<string> NotesOf(RecordedClass recorded)
    {
        if (recorded.Video is { } video) yield return $"\"{video.Title}\" (YouTube, {video.Obtained})";
        if (FolderNames.LaterClassOfItsDay(recorded.Files.Folder) is { } dateFolder) yield return $"in {dateFolder}";
        if (recorded.Parts > 1) yield return $"{recorded.Parts} parts";
        if (recorded.HasMicrophone) yield return "class + microphone";
        if (recorded.LessonVideo == LessonVideoState.Made) yield return LessonVideoMadeNote;
        if (recorded.LessonVideo == LessonVideoState.NotMade) yield return LessonVideoNotMadeNote;
        if (recorded.OnDrive is not null) yield return "audio on Drive";
    }

    /// <summary>The note of a lesson whose finished video is there.</summary>
    public const string LessonVideoMadeNote = "lesson video";

    /// <summary>The note of a lesson whose camera pieces wait to be joined with its sound.</summary>
    public const string LessonVideoNotMadeNote = "lesson video not made yet";

    /// <summary>When the recording started, in local time, as its name stamps it in UTC; empty for a name without a stamp.</summary>
    private string StartOf(RecordingFiles files) =>
        files.StartedUtc is { } started
            ? TimeZoneInfo.ConvertTime(started, time.LocalTimeZone).ToString(StartFormat, CultureInfo.InvariantCulture)
            : "";

    /// <summary>
    /// Its transcripts: "transcript" for one, "transcript (2)" and so on for more, made again or rebuilt; otherwise "answer
    /// kept" when an answer ElevenLabs was paid for is kept without a transcript, or "no transcript yet".
    /// </summary>
    private static string TranscriptOf(RecordedClass recorded) => TranscriptCopies.Transcripts(recorded.Files).Count switch
    {
        0 => recorded.KeptAnswer is not null ? "answer kept" : "no transcript yet",
        1 => "transcript",
        var copies => $"transcript ({copies})"
    };

    /// <summary>
    /// What is going on with the recording now, or null when nothing is: RECORDING NOW while a live recorder records it, or
    /// while a recorder holds its newest segment open for writing, as one without a run file does; TRANSCRIBING while a
    /// live recorder transcribes it; ENDED UNEXPECTEDLY when its recorder is gone while its run file still says it records,
    /// until that is dealt with at a start of ClassScribe.
    /// </summary>
    private static string? StateOf(RecordedClass recorded, RunView? run) =>
        recorded.BeingWritten || run is { IsRecordingNow: true } ? "RECORDING NOW"
        : run is { IsTranscribingNow: true } ? "TRANSCRIBING"
        : run is { EndedUnexpectedly: true } ? "ENDED UNEXPECTEDLY"
        : null;

    /// <summary>
    /// The newest run of the recording among <paramref name="runs"/>, which come newest first, or null when none is: a
    /// recording continued after its recorder ended unexpectedly has the run that continued it as its newest, which says
    /// what is going on now.
    /// </summary>
    private static RunView? NewestRunOf(RecordingFiles files, IReadOnlyList<RunView> runs) => runs.FirstOrDefault(run => RunView.IsRunOf(run.Record, files));
}
