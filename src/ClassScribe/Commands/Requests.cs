using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;

namespace ClassScribe.Commands;

/// <summary>
/// Where the class's sound comes from: the output device it plays on, found by ClassScribe; one output device, chosen by
/// part of its name; or one app's sound. These three kinds and no others. Written down, each kind is named by "kind":
/// automatic, device or app.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Automatic), "automatic")]
[JsonDerivedType(typeof(Device), "device")]
[JsonDerivedType(typeof(App), "app")]
public abstract record ClassSource
{
    /// <summary>Only the kinds declared here derive from it.</summary>
    private ClassSource()
    {
    }

    /// <summary>The output device the class plays on, found with Windows' level meters: --device auto, or no device or app named in the settings.</summary>
    public sealed record Automatic : ClassSource;

    /// <summary>The output device whose name contains <paramref name="NamePart"/>, in any case, and no other: --device "Pixel Buds".</summary>
    /// <param name="NamePart">Part of the device's name, as Windows shows it, such as Pixel Buds.</param>
    public sealed record Device(string NamePart) : ClassSource;

    /// <summary>One app's sound, whatever device it plays through: --app chrome.</summary>
    /// <param name="ProcessName">The name Windows gives the app's process, such as chrome or msedge.</param>
    public sealed record App(string ProcessName) : ClassSource;
}

/// <summary>Which microphone to record, and with the class, which app's microphone use its track follows.</summary>
/// <param name="NamePart">
/// Part of the microphone's name, in any case; null for Recording:Microphone, and when that is empty too, Windows' default
/// microphone, never a Bluetooth one.
/// </param>
/// <param name="Follows">
/// The app whose microphone use the track follows, from --mic-follows; null for the app the class is recorded from in app
/// mode, and otherwise Recording:MicrophoneFollows. In person no app is followed, whatever this says.
/// </param>
public sealed record MicrophoneChoice(string? NamePart, FollowRule? Follows = null);

/// <summary>Everything a run records.</summary>
/// <param name="Mode">The class alone, the class and your microphone, or your microphone alone in person.</param>
/// <param name="Class">Where the class's sound comes from; null only in person (<see cref="RecordingMode.Microphone"/>).</param>
/// <param name="Microphone">Which microphone to record; null when the class is recorded alone (<see cref="RecordingMode.Class"/>).</param>
public sealed record SourcePlan(RecordingMode Mode, ClassSource? Class, MicrophoneChoice? Microphone);

/// <summary>What to do when a recording of the same name stopped recently without a transcript.</summary>
public enum ContinueChoice
{
    /// <summary>
    /// Neither --continue nor --new: the menu asks (C continues, N starts a new recording, Esc goes back). The command line
    /// never asks: it starts a new recording and says how to continue the other instead.
    /// </summary>
    Ask,

    /// <summary>Continue it as its next part (--continue); when there is none to continue, say so and start a new one.</summary>
    Continue,

    /// <summary>Start a new recording without a word (--new).</summary>
    New
}

/// <summary>Where a run was started from, which decides what is asked and what is shown.</summary>
public enum StartOrigin
{
    /// <summary>Menu choice 1 or 2.</summary>
    Menu,

    /// <summary>record or record-only on the command line, which never asks anything.</summary>
    CommandLine,

    /// <summary>C in the report at the start, continuing a recording that ended unexpectedly.</summary>
    StartupReport,

    /// <summary>A window continuing the recording by itself, because its recorder ended unexpectedly while it showed it.</summary>
    AutoContinue,

    /// <summary>
    /// The same from a window the recorder opened by itself (attach --alert): the continued run is shown with its keys
    /// waiting for Enter, as that window showed the lost one, and never records in that window.
    /// </summary>
    AutoContinueFromAlert
}

/// <summary>
/// One run, decided before it starts: everything its recorder needs but the settings, which travel with it (see
/// <see cref="Recorder.RunRequestFile"/>), and the API key, which only the environment carries.
/// </summary>
/// <param name="RunId">The run's id (see <see cref="RunIds"/>).</param>
/// <param name="Name">The recording's name, such as Kalam.</param>
/// <param name="Transcribe">True to transcribe once the recording stops (record, menu choice 2); false to record only.</param>
/// <param name="Source">What to record.</param>
/// <param name="OutputRoot">The recordings folder for this run, as a full path: --output, or Recording:OutputFolder.</param>
/// <param name="Continue">What to do when a recording of the same name stopped recently without a transcript.</param>
/// <param name="ContinueBaseName">
/// The recording this run continues as its next part, by base name, such as Kalam_2026-10-15_23-00-02Z, found in the folder
/// of <paramref name="Name"/> under <paramref name="OutputRoot"/> or in one of its date folders (see
/// <see cref="Storage.ClassFolders.OpenToContinue"/>); null for a new recording.
/// </param>
/// <param name="Minutes">Stop by itself after this many minutes (--minutes), counted on the steady clock from the start; null to record until stopped.</param>
/// <param name="Open">False to open nothing at the end (--no-open); otherwise the settings decide.</param>
/// <param name="Video">
/// The YouTube video the run is for, when it is one of a batch's (see <see cref="VideoRequest"/>); null for a class. It is
/// last and has a default, so that a window of an older build still reads a run file that has it, and one that lacks it;
/// a class's run file is written without it, as before videos.
/// </param>
public sealed record RecordRequest(string RunId, string Name, bool Transcribe, SourcePlan Source, string OutputRoot,
    ContinueChoice Continue, string? ContinueBaseName, double? Minutes, bool Open,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VideoRequest? Video = null);

/// <summary>
/// The YouTube video a run is for, one of a batch's (design 4.5): the video's own facts, how its audio is obtained, and where
/// the batch keeps its recordings. A run that carries one is never a class: it is never offered to be continued, and the
/// class machinery passes it over. On the download path it is carried by each transcript's run record, which has no recorder.
/// </summary>
/// <param name="Url">The video's link, rebuilt from its ID.</param>
/// <param name="VideoId">The video's ID.</param>
/// <param name="Title">Its title, or its ID when none is known.</param>
/// <param name="Channel">Its channel's name, when known.</param>
/// <param name="Obtained">How its audio is obtained: downloaded or recorded (see <see cref="YouTube.VideoSource"/>).</param>
/// <param name="Folder">The batch's date folder, where its recordings go; null for the batch's first, which takes its date folder as a class does.</param>
/// <param name="BrowserProcessId">The process of ClassScribe's video window, on the record path; null on the download path.</param>
/// <param name="BrowserStartedUtc">When that process started, so that a reused process id is never taken for it; null on the download path.</param>
/// <param name="Position">The video's place in the batch, from 1.</param>
/// <param name="Count">How many videos the batch takes.</param>
/// <param name="BatchId">The batch's id, made as a run's is (see <see cref="RunIds"/>).</param>
public sealed record VideoRequest(string Url, string VideoId, string Title, string? Channel, string Obtained, string? Folder,
    int? BrowserProcessId, DateTimeOffset? BrowserStartedUtc, int Position, int Count, string BatchId);

/// <summary>
/// Run ids. One names a run in its files, its pipe and its stop event, and on the command line (--recorder, attach --alert):
/// the run's start time in UTC and 8 random hexadecimal digits, such as 20261015-230002Z-1f3a9c07. Ids sort by start
/// time, and hold nothing that could lead out of the runs folder or change the name of a pipe or an event.
/// </summary>
public static partial class RunIds
{
    /// <summary>A new run id for a run that starts at <paramref name="startedUtc"/>.</summary>
    public static string New(DateTimeOffset startedUtc) =>
        $"{startedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";

    /// <summary>
    /// True when <paramref name="text"/> is a run id as <see cref="New"/> makes them, and nothing more. A run id read from
    /// the command line or a file is checked with this before it names a file, a pipe or an event.
    /// </summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? text) => text is not null && Shape().IsMatch(text);

    /// <summary>
    /// The shape of a run id. Digits are 0 to 9 only (\d would take other scripts' digits too), and \z ends the match at
    /// the very end, where $ would also let a line break through.
    /// </summary>
    [GeneratedRegex(@"^[0-9]{8}-[0-9]{6}Z-[0-9a-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
