using System.Globalization;
using System.Reflection;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Drive;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ClassScribe.Settings;

/// <summary>
/// One problem the check found: the sentence that says it, starting with the setting's name, and the settings it names,
/// which a recording drops from a file that fails the check while it keeps the rest (build-plan amendment 4). A name may be
/// a section, such as Logging:Console, which stands for every setting under it.
/// </summary>
/// <param name="Text">The sentence.</param>
/// <param name="Keys">The settings it names, the first being the one the sentence starts with.</param>
public sealed record SettingsProblem(string Text, IReadOnlyList<string> Keys);

/// <summary>
/// Checks settings before they are used. Settings are otherwise read only when first used, which for the transcript
/// layout is after the paid upload; here a value that cannot be read, or one that makes no sense, is found at once, with
/// a message that names the setting. The settings to check are an argument, so that a staged copy of them is checked
/// before any of it reaches the app (see <see cref="SettingsReloader"/>). The log's own settings are checked too, since
/// they are read again with the others. Each problem names its settings (see <see cref="FindProblemsByKey"/>).
/// <paramref name="folders"/> tells the warnings about where the recordings go what is on this computer (see
/// <see cref="FindWarnings"/>); without it, as in tests that do not need it, only a folder's path is looked at.
/// </summary>
public sealed class SettingsCheck(IOutputFolderFacts? folders = null)
{
    /// <summary>The folders' facts the warnings use: those given, or none, which looks at nothing on the computer.</summary>
    private readonly IOutputFolderFacts _folders = folders ?? new NoFolderFacts();

    /// <summary>How to write a path in appsettings.json. A single backslash there escapes the next letter: \t becomes a tab, \n a line break.</summary>
    private const string PathAdvice = "In appsettings.json, write each backslash twice, as in \"D:\\\\Classes\", or use forward slashes, as in \"D:/Classes\".";

    /// <summary>The section that holds the log's settings.</summary>
    private const string LoggingSection = "Logging";

    /// <summary>The most Recording:ContinueWithinMinutes may be: a day.</summary>
    private const int LongestContinueWithinMinutes = 1440;

    /// <summary>The name of ClassScribe's own process, which the microphone must never follow.</summary>
    private const string OwnProcessName = "ClassScribe";

    /// <summary>What Recording:MicrophoneFollows takes, for its messages.</summary>
    private const string FollowsAdvice = $"it must be part of the class app's name, such as chrome, or {FollowRule.AnyWord} (any app that holds the microphone) or {FollowRule.NoneWord} (V alone).";

    /// <summary>The environment variables in which Windows names the OneDrive folders.</summary>
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    /// <summary>Lists what is wrong with <paramref name="configuration"/>, one sentence per problem, each starting with the setting's name. Empty when it can be used.</summary>
    public IReadOnlyList<string> FindProblems(IConfiguration configuration) => [.. FindProblemsByKey(configuration).Select(problem => problem.Text)];

    /// <summary>
    /// The same problems, each with the settings it names (see <see cref="SettingsProblem"/>): the setting each sentence
    /// starts with, and for a channel in both YouTube lists, both lists.
    /// </summary>
    public IReadOnlyList<SettingsProblem> FindProblemsByKey(IConfiguration configuration)
    {
        Found problems = [];

        const string recordingSection = RecordingOptions.SectionName;
        var recording = Bind<RecordingOptions>(configuration, recordingSection, problems);
        CheckPath(problems, Name(recordingSection, nameof(RecordingOptions.OutputFolder)), recording.OutputFolder);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.FlushSeconds)), recording.FlushSeconds, 1);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.SilenceWarningSeconds)), recording.SilenceWarningSeconds, 1);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.StatusIntervalSeconds)), recording.StatusIntervalSeconds, 1);

        // A WAV file cannot describe more than 4 GB of audio, and tiny segments would scatter a class over hundreds of files.
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.MaxSegmentMegabytes)), recording.MaxSegmentMegabytes, 10, 4000);

        // A class cut short by a crash or a restart is continued within minutes or hours. A recording that stopped more
        // than a day ago belongs to another class, and continuing it would join the two into one recording. 0 never offers.
        var continueWithin = recording.ContinueWithinMinutes;
        if (continueWithin is < 0 or > LongestContinueWithinMinutes)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{Name(recordingSection, nameof(RecordingOptions.ContinueWithinMinutes))} is {continueWithin}; it must be from 0 to {LongestContinueWithinMinutes} (a day), such as 60. 0 never offers to continue a recording."));

        // ElevenLabs takes at most 10 hours of audio in one file, and ClassScribe sends at most the longest recording the
        // workflow allows: a later safety stop would let a recording grow too long to transcribe. 0 turns the stop off.
        var stopAfter = recording.StopAfterHours;
        var latestStop = TranscriptionWorkflow.LongestRecording.TotalHours;
        if (!double.IsFinite(stopAfter) || stopAfter < 0 || stopAfter > latestStop)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{Name(recordingSection, nameof(RecordingOptions.StopAfterHours))} is {stopAfter}; it must be from 0 to {latestStop} (hours), such as 9. ElevenLabs takes at most 10 hours of audio in one file; 0 turns the safety stop off."));

        // The microphone track follows the app named here. Empty, it would follow nothing and record nothing; naming
        // ClassScribe, whose own capture holds the microphone for the whole class, it would follow ClassScribe and record
        // everything. A name part matches any app whose process name contains it, so any part of "ClassScribe" names it.
        var follows = Name(recordingSection, nameof(RecordingOptions.MicrophoneFollows));
        if (!FollowRule.TryParse(recording.MicrophoneFollows, out var rule))
            problems.Add($"{follows} is \"{recording.MicrophoneFollows}\"; {FollowsAdvice}");
        else if (rule is FollowRule.Named named && named.Matches(OwnProcessName))
            problems.Add($"{follows} is \"{recording.MicrophoneFollows}\", which names ClassScribe itself; {FollowsAdvice}");

        // The camera's picture size: the one parser the command line and the menu use too (amendment 39).
        if (!CameraSize.TryParse(recording.CameraResolution, out _))
            problems.Add($"{Name(recordingSection, nameof(RecordingOptions.CameraResolution))} is \"{recording.CameraResolution}\"; it must be {CameraSize.Rule}.");

        const string elevenLabsSection = ElevenLabsOptions.SectionName;
        var elevenLabs = Bind<ElevenLabsOptions>(configuration, elevenLabsSection, problems);
        var baseUrl = Name(elevenLabsSection, nameof(ElevenLabsOptions.BaseUrl));
        if (!Uri.TryCreate(elevenLabs.BaseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http"))
            problems.Add($"{baseUrl} is \"{elevenLabs.BaseUrl}\"; it must be a web address, such as https://api.elevenlabs.io/.");

        if (string.IsNullOrWhiteSpace(elevenLabs.ModelId))
            problems.Add($"{Name(elevenLabsSection, nameof(ElevenLabsOptions.ModelId))} is empty; it names the ElevenLabs model, such as scribe_v2.");

        var keytermsFile = Name(elevenLabsSection, nameof(ElevenLabsOptions.KeytermsFile));
        if (elevenLabs.KeytermsFile is null)
            problems.Add($"{keytermsFile} is null; write a file name, such as keyterms.txt, or \"\" to send no glossary.");
        else
            CheckPath(problems, keytermsFile, elevenLabs.KeytermsFile);

        // A day is far more than an upload and its transcript take; HttpClient itself stops at about 24 days.
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.TimeoutMinutes)), elevenLabs.TimeoutMinutes, 1, 1440);
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.MaxAttempts)), elevenLabs.MaxAttempts, 1, 10);
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.RetryDelaySeconds)), elevenLabs.RetryDelaySeconds, 0, 3600);

        // A tenth of a megabit per second still gives the longest recording ElevenLabs takes a time limit of about a day;
        // a gigabit line is faster than any upload needs to be allowed for.
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.SlowestUploadMbps)), elevenLabs.SlowestUploadMbps, 0.1, 1000, "megabits per second", "1");

        // The prices behind the cost estimate: 0 turns the estimate off, and a hundred dollars an hour is far above any
        // price ElevenLabs lists; a negative price or NaN is a slip.
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.PricePerHourUsd)), elevenLabs.PricePerHourUsd, 0, 100, "dollars an hour", "0.22");
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.KeytermsPricePerHourUsd)), elevenLabs.KeytermsPricePerHourUsd, 0, 100, "dollars an hour", "0.05");

        const string transcriptSection = TranscriptOptions.SectionName;
        var transcript = Bind<TranscriptOptions>(configuration, transcriptSection, problems);

        // 0 turns a kind of paragraph break off, as TranscriptFormatter reads it. A value below 0 would do the
        // same there, but it is far more likely a slip than a way of saying "off", so it is refused.
        var pause = transcript.ParagraphPauseSeconds;
        if (!double.IsFinite(pause) || pause < 0)
            problems.Add($"{Name(transcriptSection, nameof(TranscriptOptions.ParagraphPauseSeconds))} is {pause.ToString(CultureInfo.InvariantCulture)}; it must be 0 or more, such as 2.0. 0 turns paragraph breaks at pauses off.");

        var length = transcript.MaxParagraphCharacters;
        if (length < 0)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Name(transcriptSection, nameof(TranscriptOptions.MaxParagraphCharacters))} is {length}; it must be 0 or more, such as 900. 0 turns the splitting of long paragraphs off."));

        // 0 writes no heading for silence left out, as the transcript reads it; below 0 is a slip, as above.
        var gap = transcript.ShowGapsLongerThanSeconds;
        if (gap < 0)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Name(transcriptSection, nameof(TranscriptOptions.ShowGapsLongerThanSeconds))} is {gap}; it must be 0 or more, such as 60. 0 turns the headings for silence left out off."));

        CheckDrive(configuration, problems);
        CheckYouTube(configuration, problems);
        CheckLogging(configuration, problems);
        return problems;
    }

    /// <summary>
    /// The Drive section: the folder's name must be a plain name, which Drive takes as it is; a piece is from 1 to 256 MiB
    /// (Drive:Upload, an enum, is checked by its names when the section is bound); and Google's addresses must be https, or
    /// http only to this computer, for a local stub in a dry run: the client secret and the tokens go there, so an address
    /// elsewhere without encryption is refused.
    /// </summary>
    private static void CheckDrive(IConfiguration configuration, Found problems)
    {
        const string driveSection = DriveOptions.SectionName;
        var drive = Bind<DriveOptions>(configuration, driveSection, problems);

        var folderName = Name(driveSection, nameof(DriveOptions.FolderName));
        if (string.IsNullOrWhiteSpace(drive.FolderName))
            problems.Add($"{folderName} is empty; it names the folder in My Drive that holds the classes, such as ClassScribe.");
        else if (drive.FolderName.Trim().Length > DriveOptions.LongestFolderName)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{folderName} is {drive.FolderName.Trim().Length} characters long; it may be at most {DriveOptions.LongestFolderName}, such as ClassScribe."));
        else if (drive.FolderName.Any(char.IsControl))
            problems.Add($"{folderName} holds a tab, a line break or another control character; write a plain name, such as ClassScribe.");

        // A piece below a MiB costs a request for every MiB of a class; one above 256 MiB holds too much to send again after
        // a dropped connection. Whole MiB are always a multiple of the 256 KiB Drive asks for.
        CheckRange(problems, Name(driveSection, nameof(DriveOptions.ChunkMegabytes)), drive.ChunkMegabytes, DriveOptions.SmallestChunkMegabytes, DriveOptions.LargestChunkMegabytes);

        var defaults = new DriveOptions();
        CheckLocalOrHttpsAddress(problems, Name(driveSection, nameof(DriveOptions.ApiBaseUrl)), drive.ApiBaseUrl, defaults.ApiBaseUrl);
        CheckLocalOrHttpsAddress(problems, Name(driveSection, nameof(DriveOptions.OAuthBaseUrl)), drive.OAuthBaseUrl, defaults.OAuthBaseUrl);
    }

    /// <summary>
    /// The YouTube section: yt-dlp's path and Chrome's, when given, must each be a full path to an .exe that is there; each
    /// channel in YouTube:MayDownloadChannels and YouTube:CourseChannels must be a UC id or an @handle, and none may be in
    /// both, since a course's channel is never downloaded; the longest a video is recorded for is from a quarter of an hour
    /// to 9 hours (one above the safety stop is a warning, see <see cref="FindWarnings"/>); and the address the
    /// titles are read from must be https, or http to this computer only, for a local stub. YouTube:DriveUpload, an enum, is
    /// checked by its names when the section is bound, and a list written as one value as a value that cannot be read.
    /// </summary>
    private static void CheckYouTube(IConfiguration configuration, Found problems)
    {
        const string youTubeSection = YouTubeOptions.SectionName;
        var youTube = Bind<YouTubeOptions>(configuration, youTubeSection, problems);

        CheckExe(problems, Name(youTubeSection, nameof(YouTubeOptions.YtDlpPath)), youTube.YtDlpPath,
            "yt-dlp.exe, such as %LOCALAPPDATA%\\Microsoft\\WinGet\\Links\\yt-dlp.exe", "yt-dlp's", "install yt-dlp (winget install --source winget --id yt-dlp.yt-dlp)");
        CheckExe(problems, Name(youTubeSection, nameof(YouTubeOptions.BrowserPath)), youTube.BrowserPath,
            "chrome.exe, such as C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "Chrome's", "install Chrome");

        // A video's recording stops at this length. One above the safety stop is only warned about (see FindWarnings): a
        // refusal would hold up every class over a setting of the videos', and the safety stop ends the video first anyway.
        CheckRange(problems, Name(youTubeSection, nameof(YouTubeOptions.LongestVideoHours)), youTube.LongestVideoHours,
            YouTubeOptions.ShortestLongestVideoHours, YouTubeOptions.LargestLongestVideoHours, "hours", "4");

        CheckLocalOrHttpsAddress(problems, Name(youTubeSection, nameof(YouTubeOptions.OEmbedBaseUrl)), youTube.OEmbedBaseUrl, new YouTubeOptions().OEmbedBaseUrl);

        var mayDownload = Name(youTubeSection, nameof(YouTubeOptions.MayDownloadChannels));
        var course = Name(youTubeSection, nameof(YouTubeOptions.CourseChannels));
        CheckChannels(problems, mayDownload, youTube.MayDownloadChannels);
        CheckChannels(problems, course, youTube.CourseChannels);
        foreach (var both in youTube.MayDownloadChannels.Where(entry => youTube.CourseChannels.Contains(entry.Trim(), StringComparer.OrdinalIgnoreCase)))
            problems.Add($"{mayDownload} names {both}, which is also in {course}; a course's channel is never downloaded, so take it out of one of them.", course);
    }

    /// <summary>Adds a problem for each entry of the channel list <paramref name="name"/> that is neither a UC id nor an @handle, by its place.</summary>
    private static void CheckChannels(Found problems, string name, string[] channels)
    {
        for (var i = 0; i < channels.Length; i++)
        {
            if (YouTubeOptions.IsChannel(channels[i].Trim())) continue;

            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name}:{i} is \"{channels[i]}\"; each channel is its @handle, such as @yourchannel, or its UC id, UC and 22 more letters and digits, as the channel's page shows them."));
        }
    }

    /// <summary>
    /// Adds a problem when the program setting <paramref name="name"/>, when given, is not the full path of an .exe file that
    /// is there: <paramref name="what"/> names the file and gives an example, <paramref name="whose"/> says whose .exe it must
    /// name, and <paramref name="install"/> how to get it. Environment variables are expanded and quotes around it taken off,
    /// as the program is found with it. Empty finds the program, and passes.
    /// </summary>
    private static void CheckExe(Found problems, string name, string? value, string what, string whose, string install)
    {
        CheckPath(problems, name, value);
        var path = YouTubeOptions.Expanded(value);
        if (path.Length == 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return;

        if (!Path.IsPathFullyQualified(path))
            problems.Add($"{name} is \"{value}\"; it must be the full path of {what}, or empty to find it. {PathAdvice}");
        else if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            problems.Add($"{name} is \"{value}\"; it must name {whose} .exe file, or be empty to find it.");
        else if (!File.Exists(path))
            problems.Add($"{name} is \"{value}\", which is not there; {install}, or leave it empty to find it.");
    }

    /// <summary>
    /// Adds a problem when <paramref name="value"/> is not an https address, nor an http one to 127.0.0.1 or localhost, with
    /// the service's own address, <paramref name="example"/>, as the example: a request's answer, or a secret, must never
    /// travel unencrypted beyond this computer.
    /// </summary>
    private static void CheckLocalOrHttpsAddress(Found problems, string name, string? value, string example)
    {
        var usable = Uri.TryCreate(value, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttps
                || (address.Scheme == Uri.UriSchemeHttp && (address.IsLoopback || address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))));
        if (!usable)
            problems.Add($"{name} is \"{value}\"; it must be an https address, such as {example}, or http://127.0.0.1:<port>/ for a local stub.");
    }

    /// <summary>
    /// Lists what is worth saying about <paramref name="configuration"/> without refusing it: an API key written into the
    /// settings, which is never used; where the recordings go, for Recording:OutputFolder and each saved class's own
    /// folder (see <see cref="FolderWarnings"/>); and a YouTube video's longest recording above the safety stop, which then
    /// ends it first.
    /// </summary>
    public IReadOnlyList<string> FindWarnings(IConfiguration configuration)
    {
        List<string> warnings = [];
        if (ApiKeyFromEnvironment.IsSetElsewhere(configuration))
            warnings.Add($"{ElevenLabsOptions.ApiKeySetting} is set in the settings, in appsettings.json or as an environment variable. It is not used: the key is read only from {ElevenLabsOptions.ApiKeyVariable}. Remove it from the file, so it cannot end up in git or be shared by mistake");

        var outputFolderName = Name(RecordingOptions.SectionName, nameof(RecordingOptions.OutputFolder));
        warnings.AddRange(FolderWarnings(outputFolderName, configuration[outputFolderName]));
        foreach (var saved in configuration.GetSection(SettingsCatalog.ClassesSection).GetChildren())
        {
            if (saved[ClassFields.OutputFolder] is { Length: > 0 } own)
                warnings.AddRange(FolderWarnings($"{SettingsCatalog.ClassesSection}:{saved.Key}:{ClassFields.OutputFolder}", own));
        }

        // A video's longest recording above the safety stop is not refused, since a refusal would hold up every class over a
        // setting of the videos'; the safety stop then ends a video first, which is said here.
        var longestName = Name(YouTubeOptions.SectionName, nameof(YouTubeOptions.LongestVideoHours));
        var stopName = Name(RecordingOptions.SectionName, nameof(RecordingOptions.StopAfterHours));
        var longest = Hours(configuration[longestName]) ?? new YouTubeOptions().LongestVideoHours;
        var stop = Hours(configuration[stopName]) ?? new RecordingOptions().StopAfterHours;
        if (stop > 0 && longest > stop)
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{longestName} is {longest}, above {stopName} ({stop}), so a YouTube video's recording stops at {stop} hours, at the safety stop. Make {longestName} at most {stop} if that is not what you want"));

        return warnings;
    }

    /// <summary>A number of hours as a setting's text gives it; null when it is not given, or is not a finite number.</summary>
    private static double? Hours(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && double.IsFinite(hours) ? hours : null;

    /// <summary>
    /// What is worth saying about the recordings folder <paramref name="value"/>, which the setting <paramref name="name"/>
    /// gives (layout-config 2): at most one warning about where it is, the first that holds of a drive's root, the user
    /// folder itself, or a folder holding AppData or Windows, where each course's folder would sit among folders that are
    /// not the classes'; and at most one about another program's sync folder, the first that holds of OneDrive, Google
    /// Drive for desktop (a drive labelled Google Drive, or a path through My Drive) and Dropbox (a folder its info.json
    /// names), which upload a recording again and again while it grows. A path that cannot be a path is a problem of its
    /// own (see <see cref="FindProblems"/>), not a place to look up.
    /// </summary>
    private IEnumerable<string> FolderWarnings(string name, string? value)
    {
        if (value is not null && value.IndexOfAny(Path.GetInvalidPathChars()) >= 0) yield break;

        var root = Path.TrimEndingDirectorySeparator(ClassFolders.RootFor(value));
        if (Placement(root) is { } placement) yield return $"{name} {placement}";

        if (IsInsideOneDrive(root))
            yield return $"{name} is inside OneDrive ({root}). OneDrive uploads a recording again and again while it grows, and can hold files that ClassScribe needs to delete; a folder outside OneDrive, such as %USERPROFILE%\\ClassScribe, is safer";
        else if (IsInsideGoogleDrive(root))
            yield return $"{name} is inside Google Drive ({root}). Google Drive for desktop uploads a recording again and again while it grows, and ClassScribe's own copy on Google Drive would put each class there a second time; a folder outside Google Drive, such as %USERPROFILE%\\ClassScribe, is safer";
        else if (_folders.DropboxFolders().Any(dropbox => IsInside(root, dropbox)))
            yield return $"{name} is inside Dropbox ({root}). Dropbox uploads a recording again and again while it grows, and can hold files that ClassScribe needs to delete, and with ClassScribe's own copy on Google Drive each class would go up to the cloud twice; a folder outside Dropbox, such as %USERPROFILE%\\ClassScribe, is safer";
    }

    /// <summary>
    /// The rest of the sentence when <paramref name="root"/> is a drive's root, the user folder itself, or a folder that
    /// holds AppData or Windows; null when it is none of these.
    /// </summary>
    private string? Placement(string root)
    {
        if (Path.GetPathRoot(root) is { Length: > 0 } driveRoot && string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), root, StringComparison.OrdinalIgnoreCase))
            return $"is the root of a drive ({driveRoot}). Each course's folder would sit among the drive's own folders; a folder of its own, such as {Path.Combine(driveRoot, "ClassScribe")}, keeps the classes together";

        var profile = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (profile.Length > 0 && string.Equals(profile, root, StringComparison.OrdinalIgnoreCase))
            return $"is your user folder itself ({root}). Each course's folder would sit beside Documents, Desktop and AppData; a folder of its own, such as %USERPROFILE%\\ClassScribe, keeps the classes together";

        foreach (var (held, why) in new[] { ("AppData", "as a user folder does"), ("Windows", "as Windows' own drive does") })
        {
            if (_folders.HasFolder(root, held))
                return $"({root}) holds a folder named {held}, {why}. Each course's folder would sit beside it; a folder of its own, such as {Path.Combine(root, "ClassScribe")}, keeps the classes together";
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="root"/> is on a drive that Google Drive for desktop added, whose label is Google Drive, or
    /// lies in a folder named My Drive, as when Google Drive for desktop mirrors My Drive into a folder.
    /// </summary>
    private bool IsInsideGoogleDrive(string root)
    {
        if (Path.GetPathRoot(root) is { Length: > 0 } driveRoot && !driveRoot.StartsWith(@"\\", StringComparison.Ordinal)
            && string.Equals(_folders.VolumeLabel(driveRoot), OutputFolderFacts.GoogleDriveLabel, StringComparison.OrdinalIgnoreCase))
            return true;

        return root.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("My Drive", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="folder"/> is <paramref name="outer"/>, a full path, or lies inside it; false for a path that is not one.</summary>
    private static bool IsInside(string folder, string outer)
    {
        var trimmed = outer.Trim();
        if (trimmed.Length == 0 || trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(trimmed)) return false;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        return folder.Equals(full, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folders' facts when none are given: nothing on the computer is looked at, so only a path's own words count.</summary>
    private sealed class NoFolderFacts : IOutputFolderFacts
    {
        /// <inheritdoc />
        public bool HasFolder(string folder, string name) => false;

        /// <inheritdoc />
        public string? VolumeLabel(string root) => null;

        /// <inheritdoc />
        public IReadOnlyList<string> DropboxFolders() => [];
    }

    /// <summary>True when <paramref name="folder"/> is a OneDrive folder that Windows names, or lies inside one.</summary>
    private static bool IsInsideOneDrive(string folder) =>
        OneDriveVariables
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Select(oneDrive => oneDrive.Trim())
            .Where(oneDrive => oneDrive.Length > 0 && oneDrive.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(oneDrive))
            .Select(oneDrive => Path.TrimEndingDirectorySeparator(Path.GetFullPath(oneDrive)))
            .Any(oneDrive => folder.Equals(oneDrive, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(oneDrive + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The log's settings. Each log level, under Logging:LogLevel and under a log's own section such as
    /// Logging:Console:LogLevel, must be a level's name; an empty one is passed over, as the log passes it over. The
    /// console log's settings and those of its formats must be values the console log takes. Read again while
    /// ClassScribe runs, a value the log cannot take would keep it from taking any new settings at all.
    /// </summary>
    private static void CheckLogging(IConfiguration configuration, Found problems)
    {
        foreach (var section in configuration.GetSection(LoggingSection).GetChildren())
        {
            var levels = section.Key.Equals("LogLevel", StringComparison.OrdinalIgnoreCase) ? section : section.GetSection("LogLevel");
            foreach (var (key, text) in levels.AsEnumerable())
            {
                if (string.IsNullOrEmpty(text) || IsName(typeof(LogLevel), text)) continue;
                problems.Add($"{key} is \"{text}\", which is not {Names(typeof(LogLevel))}.");
            }
        }

        var captureScopes = $"{LoggingSection}:CaptureScopes";
        if (configuration[captureScopes] is { } scopes && !bool.TryParse(scopes, out _))
            problems.Add($"{captureScopes} is \"{scopes}\", which is not true or false.");

        CheckBinding<ConsoleLoggerOptions>(configuration, $"{LoggingSection}:Console", problems);

        // Every format of the console log reads the same section; the two richest cover the settings of the others.
        var formatterOptions = $"{LoggingSection}:Console:FormatterOptions";
        if (CheckBinding<SimpleConsoleFormatterOptions>(configuration, formatterOptions, problems))
            CheckBinding<JsonConsoleFormatterOptions>(configuration, formatterOptions, problems);
    }

    /// <summary>
    /// Binds the section at <paramref name="path"/> to <typeparamref name="TOptions"/> as the log does, and adds a problem
    /// when that fails: a value of the wrong kind, or one the log refuses. Returns true when the section can be used.
    /// </summary>
    private static bool CheckBinding<TOptions>(IConfiguration configuration, string path, Found problems) where TOptions : new()
    {
        try
        {
            configuration.GetSection(path).Bind(new TOptions());
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a value the log cannot take is a problem to report with the others, in words,
            // and the reason is in the problem; it is not a failure of the check itself.
            var reason = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            problems.Add($"{path} holds a value the console log cannot use: {reason.Message}");
            return false;
        }
    }

    /// <summary>
    /// Binds a section the way the app does, after reporting each value that cannot be read as the type
    /// its setting needs. Those values are left out of the binding, so that the section's other settings
    /// can still be checked; the settings left out keep their defaults here.
    /// </summary>
    private static TOptions Bind<TOptions>(IConfiguration configuration, string section, Found problems) where TOptions : class, new()
    {
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in typeof(TOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite))
        {
            var key = Name(section, property.Name);
            if (configuration[key] is not { } text || Expected(property.PropertyType, text) is not { } expected) continue;

            problems.Add($"{key} is \"{text}\", which is not {expected}.");
            unreadable.Add(key);
        }

        var readable = configuration.GetSection(section).AsEnumerable().Where(setting => !unreadable.Contains(setting.Key));
        return new ConfigurationBuilder().AddInMemoryCollection(readable).Build().GetSection(section).Get<TOptions>() ?? new TOptions();
    }

    /// <summary>
    /// What a setting of type <paramref name="type"/> must look like, when <paramref name="text"/> cannot
    /// be read as one; null when it can. The rules are those the settings are bound with, except that an
    /// enum setting takes only the names of its values, in any case: the binding would also take a number,
    /// or several names joined by a comma, and read them as some value without saying which.
    /// </summary>
    private static string? Expected(Type type, string text)
    {
        if (type == typeof(int))
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? null : "a whole number, such as 60";

        if (type == typeof(double))
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? null : "a number written with a dot, such as 1.5";

        if (type == typeof(bool))
            return bool.TryParse(text, out _) ? null : "true or false";

        if (type.IsEnum)
            return IsName(type, text) ? null : Names(type);

        // A list is written in square brackets; a single value in its place would bind as no list at all.
        if (type.IsArray)
            return text.Length == 0 ? null : "a list in square brackets, such as [\"@yourchannel\"]";

        return null;
    }

    /// <summary>True when <paramref name="text"/> is the name of one of the values of the enum <paramref name="type"/>, in any case.</summary>
    private static bool IsName(Type type, string text) =>
        Enum.GetNames(type).Any(name => name.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of the values of the enum <paramref name="type"/>, as in "OnBattery, Always or Never".</summary>
    private static string Names(Type type)
    {
        var names = Enum.GetNames(type);
        return names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} or {names[^1]}";
    }

    /// <summary>Adds a problem when a whole-number setting is below <paramref name="min"/> or above <paramref name="max"/>.</summary>
    private static void CheckRange(Found problems, string name, int value, int min, int max = int.MaxValue)
    {
        if (value >= min && value <= max) return;

        if (max == int.MaxValue)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} is {value}; it must be {min} or more."));
        else
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} is {value}; it must be from {min} to {max}."));
    }

    /// <summary>Adds a problem when a number setting is not a number from <paramref name="min"/> to <paramref name="max"/>, saying in what <paramref name="unit"/>, with an <paramref name="example"/>.</summary>
    private static void CheckRange(Found problems, string name, double value, double min, double max, string unit, string example)
    {
        if (double.IsFinite(value) && value >= min && value <= max) return;

        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} is {value}; it must be from {min} to {max} ({unit}), such as {example}."));
    }

    /// <summary>Adds a problem when a path holds a character no path can hold, which is what a single backslash in appsettings.json often turns into.</summary>
    private static void CheckPath(Found problems, string name, string? path)
    {
        if (path is not null && path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            problems.Add($"{name} holds a tab, a line break or another character that cannot be part of a path. {PathAdvice}");
    }

    /// <summary>The full name of a setting, such as Recording:FlushSeconds.</summary>
    private static string Name(string section, string setting) => $"{section}:{setting}";

    /// <summary>
    /// The problems found so far. Every sentence starts with the name of the setting it is about, followed by a space, so
    /// that name is the problem's first key; a problem about several settings names the others too.
    /// </summary>
    private sealed class Found : List<SettingsProblem>
    {
        /// <summary>Adds the problem <paramref name="text"/>, about the setting its first word names and any of <paramref name="others"/>.</summary>
        public void Add(string text, params string[] others)
        {
            var space = text.IndexOf(' ');
            Add(new SettingsProblem(text, [space > 0 ? text[..space] : text, .. others]));
        }
    }
}
