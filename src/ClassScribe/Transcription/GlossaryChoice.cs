using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>
/// The one resolver of the glossary (architecture 8.2, item 5, and Gulzar's answer to question 11): every run and every
/// transcript, YouTube's plans included, takes its glossary from here. A recording's glossary is decided once, as it starts,
/// and kept beside it in its record (see <see cref="GlossaryRecord"/>); every later transcription reuses it. The order:
/// <list type="number">
/// <item>a glossary given to the command, transcribe --keyterms, or none;</item>
/// <item>the recording's own record, when it has one, with a line that says so; when its file is gone, the steps below
/// decide again, with a warning, and at a run's upload the glossary its start decided stands for them, since a recorder
/// has no saved classes to decide by; when the file changed, it is used as it is now, and the line says so;</item>
/// <item>the saved class the run was started as, when it names a glossary;</item>
/// <item>a saved class whose course is the recording's name, for a start by name and a later transcription alike; when the
/// classes of that course name different glossaries, the next step, with a warning that names them;</item>
/// <item>ElevenLabs:KeytermsFile;</item>
/// <item>none.</item>
/// </list>
/// A value that is empty, or none in any case, means no glossary; a relative file is taken from the settings folder (see
/// <see cref="IGlossaryFiles.Resolve"/>). One line says which glossary, at a run's start and before each upload. A glossary
/// never stops a recording or costs a transcript: a file that is not there, or cannot be read, gives a warning and no
/// terms, and a record that cannot be read or written is said, and nothing else changes. Its terms are read under the limits
/// the settings give (ElevenLabs:MaxKeyterms, KeytermCharacterLimit and MaxKeytermWords, architecture 7.2), which travel to
/// <see cref="KeytermRules"/> as a <see cref="KeytermLimits"/>. One per run, with the settings of its scope.
/// </summary>
/// <param name="files">The glossary's files and the recording's record.</param>
/// <param name="classes">The saved classes, as the settings in force hold them; a recorder's hold none.</param>
/// <param name="elevenLabs">The run's ElevenLabs settings, for ElevenLabs:KeytermsFile and the glossary's limits.</param>
/// <param name="time">The clock the record's times are written by.</param>
/// <param name="logger">Where the glossary's line and its warnings go.</param>
public sealed class GlossaryChoice(IGlossaryFiles files, ISavedClasses classes, IOptionsSnapshot<ElevenLabsOptions> elevenLabs, TimeProvider time, ILogger<GlossaryChoice> logger)
{
    /// <summary>The setting that names the glossary of every recording no class names one for.</summary>
    public const string SettingName = $"{ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.KeytermsFile)}";

    /// <summary>What one request's glossary may hold, by the run's settings.</summary>
    private KeytermLimits Limits =>
        new(elevenLabs.Value.MaxKeyterms, elevenLabs.Value.KeytermCharacterLimit, elevenLabs.Value.MaxKeytermWords);

    /// <summary>Where a glossary given to the command came from, in words.</summary>
    public const string CommandName = "transcribe --keyterms";

    /// <summary>Where a transcript's glossary came from when it was the one the recording's record holds.</summary>
    public const string AsDecided = "as decided when this recording started";

    /// <summary>
    /// Steps 3 to 6, for a run about to start: the glossary of <paramref name="className"/>, the saved class the run was
    /// started as, when it names one; else that of a saved class whose course is <paramref name="name"/>, the recording's
    /// name; else ElevenLabs:KeytermsFile, which may be none. Nothing is read or said: the run's start says it (see
    /// <see cref="AtStart"/>), and a warning about how it was decided travels with it.
    /// </summary>
    public GlossaryPick ForRun(string name, string? className)
    {
        if (className is not null && classes.Find(className)?.Class is { KeytermsFile: { } own } started)
            return Pick(own, ClassFrom(started.Name), started.Name);

        var ofCourse = classes.All().Select(entry => entry.Class).OfType<SavedClass>()
            .Where(saved => saved.KeytermsFile is not null && saved.Course.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var different = ofCourse.Select(saved => Key(saved.KeytermsFile!)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (different == 1) return Pick(ofCourse[0].KeytermsFile!, ClassFrom(ofCourse[0].Name), ofCourse[0].Name);

        var warning = different > 1
            ? $"The saved classes {Listed(ofCourse.Select(saved => $"\"{saved.Name}\""))} record {name.Trim()} with different glossaries ({string.Join(", ", ofCourse.Select(saved => saved.KeytermsFile))}), so {SettingName} decides"
            : null;
        return Pick(elevenLabs.Value.KeytermsFile ?? "", SettingName) with { Warning = warning };
    }

    /// <summary>
    /// At a run's start, in either home: the run's glossary is the request's, as the window decided it, or, for a request
    /// that carries none or one that is incomplete, decided here (see <see cref="ForRun"/>). A recording that has its record,
    /// as a continued one does, keeps it, and its line says the glossary decided then; when the file decided then is gone,
    /// the run's glossary is decided in its place and written into the record, so that this recording's transcripts send
    /// what this start says. Otherwise the record is written with the run's glossary, before any transcript, so that a
    /// recording made with record-only has it too. The line says which glossary, and a glossary that is not there is warned
    /// about now, as the class starts, not after the upload. Returns the run's glossary, which the run hands to its
    /// transcript for when the record gives none then (see <see cref="ForTranscript"/>): a recorder's settings hold no saved
    /// classes to decide again by. Never throws over the glossary or its record.
    /// </summary>
    public GlossaryPick AtStart(RecordingFiles files, RecordRequest request)
    {
        var run = Whole(request.Glossary) ?? ForRun(request.Name, request.ClassName);
        var (record, readable) = ReadRecord(files);
        if (record is not null)
        {
            if (Reuse(record.Decided) is null)
                WriteRecord(files, record with { Decided = Decision(run, Decide(run)) }, "the recording is not affected");
            return run;
        }

        var loaded = Decide(run);
        if (readable) WriteRecord(files, new GlossaryRecord(GlossaryRecord.CurrentVersion, Decision(run, loaded), []), "the recording is not affected");
        return run;
    }

    /// <summary>
    /// Before an upload of <paramref name="files"/>: the glossary to send, by the order of the class (see the class), with
    /// <paramref name="given"/> as the command's own when it is not null, and its line. When the recording's record gives
    /// none, because it cannot be read, was never written or names a file that is gone, the glossary is
    /// <paramref name="run"/>, the one the recording's run decided at its start (see <see cref="AtStart"/>), so that the
    /// transcript sends what the start line named; without one, as for a later transcription, steps 4 to 6 decide. A
    /// recording without a record gets one now, so that the next transcription reuses it. Never throws over the glossary or
    /// its record.
    /// </summary>
    public GlossaryUse ForTranscript(RecordingFiles files, string? given, GlossaryPick? run = null)
    {
        var (record, readable) = ReadRecord(files);
        GlossaryUse? use = null;
        if (given is not null)
        {
            var pick = Pick(given, CommandName);
            var loaded = Load(pick, say: true);
            use = new GlossaryUse(pick.Path, pick.From, loaded.Terms, loaded.Sha256, record?.Decided);
        }
        else if (record is not null)
        {
            use = Reuse(record.Decided);
        }

        GlossaryPick? decided = null;
        Loaded? decidedLoaded = null;
        if (use is null)
        {
            decided = Whole(run) ?? ForRun(files.Name, className: null);
            decidedLoaded = Decide(decided);
            use = new GlossaryUse(decided.Path, decided.From, decidedLoaded.Terms, decidedLoaded.Sha256, record?.Decided);
        }

        if (record is not null || !readable) return readable ? use : use with { Decided = null };

        decided ??= Whole(run) ?? ForRun(files.Name, className: null);
        decidedLoaded ??= Load(decided, say: false);
        var decision = Decision(decided, decidedLoaded);
        WriteRecord(files, new GlossaryRecord(GlossaryRecord.CurrentVersion, decision, []), "the transcript is not affected");
        return use with { Decided = decision };
    }

    /// <summary>
    /// After the transcript <paramref name="transcriptPath"/> of <paramref name="files"/> was saved: its entry added to the
    /// recording's record, what <paramref name="use"/> sent. A record that cannot be read is left as it is; one that cannot be
    /// written is said. Never throws over the record.
    /// </summary>
    public void Sent(RecordingFiles files, GlossaryUse use, string transcriptPath)
    {
        if (use.Decided is not { } decided) return;

        var (record, readable) = ReadRecord(files);
        if (!readable) return;

        record ??= new GlossaryRecord(GlossaryRecord.CurrentVersion, decided, []);
        var copy = files.TranscriptCopyOf(transcriptPath) ?? record.Sent.Count + 1;
        var sent = new GlossarySent(copy, use.Path, use.Terms.Count, use.Sha256, time.GetUtcNow(), use.From);
        WriteRecord(files, record with { Sent = [.. record.Sent, sent] }, "the transcript is not affected");
    }

    /// <summary>
    /// How many terms a recording of <paramref name="course"/> would be sent with, for a YouTube plan's price: steps 4 to 6,
    /// read quietly, since nothing is sent yet.
    /// </summary>
    public int TermsFor(string course)
    {
        var pick = ForRun(course, className: null);
        var terms = Load(pick, say: false).Terms.Count;
        logger.LogDebug("A recording of {Course} would be sent with {Terms} keyterms ({From})", course, terms, pick.From);
        return terms;
    }

    /// <summary>
    /// The warnings the menu's header gives for the saved classes whose own glossary is a file that is not there: the class
    /// can still be used, and its transcripts are made without its terms until the file is there.
    /// </summary>
    public static IEnumerable<string> MissingGlossaries(IEnumerable<SavedClassEntry> saved, IGlossaryFiles files)
    {
        foreach (var entry in saved)
        {
            if (entry.Class?.KeytermsFile is not { } glossary || SavedClass.MeansNoGlossary(glossary)) continue;

            var path = ResolveQuietly(files, glossary);
            if (!files.Exists(path))
                yield return $"The saved class \"{entry.Name}\" names the glossary {path}, which is not there; its transcripts are made without its terms until it is.";
        }
    }

    /// <summary>
    /// The pick <paramref name="value"/> names, from <paramref name="from"/>: none for an empty value or none in any case,
    /// else the file it resolves to.
    /// </summary>
    private GlossaryPick Pick(string value, string from, string? className = null)
    {
        var setting = value.Trim();
        return SavedClass.MeansNoGlossary(setting)
            ? new GlossaryPick(setting, "", from, className)
            : new GlossaryPick(setting, ResolveQuietly(files, setting), from, className);
    }

    /// <summary>The file <paramref name="value"/> names, as a full path; the value as it is when no path can hold it, which then names no file that is there.</summary>
    private static string ResolveQuietly(IGlossaryFiles files, string value)
    {
        try
        {
            return files.Resolve(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // Deliberately not rethrown: a value no path can hold names no file, and is warned about as one that is not
            // there; a glossary must never stop a recording.
            return value.Trim();
        }
    }

    /// <summary>
    /// <paramref name="pick"/>, when it is whole; null, after a warning, when a field of it is missing, as a request or run file
    /// of another shape or a damaged one reads, so that the glossary is decided here instead, as for a request that carries none.
    /// </summary>
    private GlossaryPick? Whole(GlossaryPick? pick)
    {
        // A field missing from the file reads as null, whatever its type says.
        if (pick is null or { Setting: not null, Path: not null, From: not null }) return pick;

        logger.LogWarning("The glossary this run's request carries is incomplete, so the glossary is decided here instead");
        return null;
    }

    /// <summary>What decides whether two classes name the same glossary: the file, in any case, or nothing for none.</summary>
    private string Key(string value) => SavedClass.MeansNoGlossary(value) ? "" : ResolveQuietly(files, value);

    /// <summary>Says how <paramref name="pick"/> was decided, when that needs a warning, then reads it and says which glossary it is.</summary>
    private Loaded Decide(GlossaryPick pick)
    {
        if (pick.Warning is { } warning) logger.LogWarning("{Warning}", warning);
        return Load(pick, say: true);
    }

    /// <summary>
    /// The terms of <paramref name="pick"/>, and its file's hash. When <paramref name="say"/>, the line that names it, or says
    /// there is none and why, or the warning for a file that is not there or cannot be read, and each term left out.
    /// </summary>
    private Loaded Load(GlossaryPick pick, bool say)
    {
        if (pick.Path.Length == 0)
        {
            if (say) logger.LogInformation("No glossary: {From} {Why}", pick.From, pick.Setting.Length == 0 ? "is empty" : "says none");
            return Loaded.None;
        }

        GlossaryText? text;
        try
        {
            text = files.Read(pick.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a glossary must never stop a recording or cost a transcript; the transcript is made
            // without its terms, and the warning says why.
            if (say) logger.LogWarning("The glossary {Path} ({From}) could not be read ({Reason}), so the transcript is made without its terms", pick.Path, pick.From, ex.Message);
            return Loaded.None;
        }

        if (text is null)
        {
            if (say) logger.LogWarning("No glossary at {Path} ({From}), so the transcript is made without its terms. {Advice}", pick.Path, pick.From, Advice(pick));
            return Loaded.None;
        }

        var read = KeytermRules.Read(text.Lines, Limits);
        if (say)
        {
            SayLeftOut(read);
            logger.LogInformation("Glossary: {Name} ({From}), {Terms}", SavedClass.GlossaryName(pick.Path), pick.From, Count(read.Terms.Count));
        }

        return new Loaded(read.Terms, text.Sha256);
    }

    /// <summary>
    /// The glossary <paramref name="decided"/> when the recording started, reused, with the line that says so and both counts,
    /// or that it changed since; null, after a warning that names it, when its file is not there now, so that it is decided
    /// again. Its entry's <see cref="GlossaryUse.Decided"/> is <paramref name="decided"/>.
    /// </summary>
    private GlossaryUse? Reuse(GlossaryDecision decided)
    {
        if (decided.Path.Length == 0)
        {
            logger.LogInformation("No glossary, {AsDecided} ({From} {Why})", AsDecided, decided.From, decided.Setting.Trim().Length == 0 ? "is empty" : "says none");
            return new GlossaryUse("", AsDecided, [], null, decided);
        }

        GlossaryText? text;
        try
        {
            text = files.Read(decided.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: as in Load, a glossary that cannot be read costs the transcript its terms, never the
            // transcript.
            logger.LogWarning("The glossary {Path}, {AsDecided}, could not be read ({Reason}), so the transcript is made without its terms", decided.Path, AsDecided, ex.Message);
            return new GlossaryUse(decided.Path, AsDecided, [], null, decided);
        }

        if (text is null)
        {
            logger.LogWarning("The glossary decided when this recording started, {Path}, is not there now, so the glossary is decided again", decided.Path);
            return null;
        }

        var read = KeytermRules.Read(text.Lines, Limits);
        SayLeftOut(read);
        if (string.Equals(text.Sha256, decided.Sha256, StringComparison.OrdinalIgnoreCase))
            logger.LogInformation("Glossary: {Name}, {AsDecided} ({Then} then, {Now} now)", SavedClass.GlossaryName(decided.Path), AsDecided, Count(decided.Terms), read.Terms.Count);
        else
            logger.LogInformation("Glossary: {Name}, {AsDecided}; changed since: {Then} then, {Now} now", SavedClass.GlossaryName(decided.Path), AsDecided, Count(decided.Terms), read.Terms.Count);

        return new GlossaryUse(decided.Path, AsDecided, read.Terms, text.Sha256, decided);
    }

    /// <summary>Warns of each term left out of <paramref name="read"/>, and of terms beyond the limit, as the glossary always has.</summary>
    private void SayLeftOut(KeytermList read)
    {
        foreach (var skipped in read.Skipped) logger.LogWarning("Skipping keyterm on line {Line} ({Term}): {Problem}", skipped.Line, skipped.Term, skipped.Problem);
        if (read.OverLimit) logger.LogWarning("The keyterms file has more than {MaxTerms} terms; the rest are ignored", Limits.MaxTerms);
    }

    /// <summary>
    /// What a warning about a file that is not there says to do, by where <paramref name="pick"/> came from: the file put
    /// there reaches this recording's next transcript, as its record names it, and another reaches it through transcribe
    /// --again --keyterms. A change to the class or the setting counts from the next recording: a run keeps the glossary its
    /// start decided, so a change made during the class never reaches it.
    /// </summary>
    private static string Advice(GlossaryPick pick) =>
        pick.ClassName is { } name ? $"{FileThere} To point the class at another from its next recording: ClassScribe class set \"{name}\" --keyterms \"<file>\""
        : pick.From == SettingName ? $"{FileThere} To point the setting at another from the next recording: ClassScribe settings set {SettingName} \"<file>\""
        : "Put the file there, or give another";

    /// <summary>The first part of the advice for a class's or the setting's glossary that is not there: what reaches this recording.</summary>
    private const string FileThere =
        "Put the file there, and this recording's next transcript sends it, or send another: ClassScribe transcribe \"<recording>\" --again --keyterms \"<file>\".";

    /// <summary>What the record holds of <paramref name="pick"/>, read as <paramref name="loaded"/>, now.</summary>
    private GlossaryDecision Decision(GlossaryPick pick, Loaded loaded) =>
        new(pick.Setting, pick.Path, pick.From, loaded.Terms.Count, loaded.Sha256, time.GetUtcNow());

    /// <summary>
    /// The recording's record, and whether it could be read: null and true when it has none; null and false, after a warning,
    /// when it is there and cannot be read, so that it is left as it is.
    /// </summary>
    private (GlossaryRecord? Record, bool Readable) ReadRecord(RecordingFiles recording)
    {
        try
        {
            return (files.ReadRecord(recording), true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the record only keeps the glossary from one transcription to the next; one that
            // cannot be read is decided again, left as it is for a person to look at, and said.
            logger.LogWarning("The glossary record {Path} could not be read ({Reason}); the glossary is decided again, and the record is left as it is", recording.Glossary, ex.Message);
            return (null, false);
        }
    }

    /// <summary>Writes <paramref name="record"/> as the recording's record; one that cannot be written is said, with <paramref name="unaffected"/>.</summary>
    private void WriteRecord(RecordingFiles recording, GlossaryRecord record, string unaffected)
    {
        try
        {
            files.WriteRecord(recording, record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a record that cannot be written must not cost the class or the transcript; the next
            // transcription decides again, and this says why.
            logger.LogWarning("Could not write the glossary record {Path} ({Reason}); {Unaffected}", recording.Glossary, ex.Message, unaffected);
        }
    }

    /// <summary>A saved class as a glossary's source names it: the saved class "Mantiq".</summary>
    private static string ClassFrom(string name) => $"the saved class \"{name}\"";

    /// <summary>Names in words: "A" and "B", or "A", "B" and "C".</summary>
    private static string Listed(IEnumerable<string> names)
    {
        var all = names.ToList();
        return all.Count <= 1 ? string.Concat(all) : $"{string.Join(", ", all.Take(all.Count - 1))} and {all[^1]}";
    }

    /// <summary>A number of terms in words: 1 term, 94 terms.</summary>
    private static string Count(int terms) => terms == 1 ? "1 term" : $"{terms} terms";

    /// <summary>A glossary as read: its terms, and its file's hash; none for no glossary, or one that is not there.</summary>
    /// <param name="Terms">The terms.</param>
    /// <param name="Sha256">The file's SHA-256; null when nothing was read.</param>
    private sealed record Loaded(IReadOnlyList<string> Terms, string? Sha256)
    {
        /// <summary>No terms, and no file read.</summary>
        public static Loaded None { get; } = new([], null);
    }
}
