using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Link;

/// <summary>
/// The link between a recorder and the windows that show its run: one named pipe per run, ClassScribe-&lt;run id&gt;, on
/// which a window says hello, passes on the recording keys and says bye, and the recorder answers with a welcome, the
/// lines of its log, the run's state and, at the end, how the run ended. Each message is one line of UTF-8 JSON, such as
/// {"type":"log","body":{...}} (see <see cref="LineChannel"/>). Both ends pass over the message types and the fields they
/// do not know, so a later build may add new ones and keep the version; any other change needs a new
/// <see cref="Version"/>, and a recorder refuses a window that speaks another one.
/// </summary>
public static class LinkProtocol
{
    /// <summary>The version of the protocol this build speaks (see <see cref="LinkVersion"/>, which every run file records).</summary>
    public const int Version = LinkVersion.Current;

    /// <summary>The longest line either end takes, in bytes, not counting its line break: 64 KB. A longer line ends the connection.</summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>How many windows may show one run at once.</summary>
    public const int MaxWindows = 4;

    /// <summary>How the name of a run's pipe begins; the run's id follows.</summary>
    public const string PipePrefix = "ClassScribe-";

    /// <summary>
    /// Which build of ClassScribe this is, as a hello and a welcome name it: the version the project gives, with the commit
    /// it was built from when the build knew it, such as 0.4.0 (f5f2fb0), the same string the run files carry (see
    /// <see cref="BuildInfo.Build"/>). A window that finds a recorder of another build with the same protocol shows the run
    /// all the same, and may say so.
    /// </summary>
    public static string Build => BuildInfo.Build;

    /// <summary>
    /// The name of the pipe of run <paramref name="runId"/>, such as ClassScribe-20261015-230002Z-1f3a9c07. Throws an
    /// <see cref="ArgumentException"/> for anything but a run id as ClassScribe makes them (see <see cref="RunIds"/>).
    /// </summary>
    public static string PipeNameFor(string runId)
    {
        if (!RunIds.IsWellFormed(runId)) throw new ArgumentException($"\"{runId}\" is not a ClassScribe run id.", nameof(runId));

        return PipePrefix + runId;
    }

    /// <summary>
    /// True when <paramref name="name"/> is the name of a run's pipe as <see cref="PipeNameFor"/> makes them, and nothing
    /// more. A name read from a run file is checked with this before a window opens a pipe by it, so that a file changed by
    /// hand cannot point a window at anything else.
    /// </summary>
    public static bool IsPipeName([NotNullWhen(true)] string? name) =>
        name is not null && name.StartsWith(PipePrefix, StringComparison.Ordinal) && RunIds.IsWellFormed(name[PipePrefix.Length..]);
}

/// <summary>The type of each message, as the "type" of its line names it.</summary>
public static class LinkTypes
{
    /// <summary>A window to the recorder, first: who it is and the last line of the log it has (see <see cref="Link.Hello"/>).</summary>
    public const string Hello = "hello";

    /// <summary>A window to the recorder: a recording key pressed in the window (see <see cref="KeyPress"/>).</summary>
    public const string Key = "key";

    /// <summary>A window to the recorder, last: the window stops showing the run (see <see cref="Link.Bye"/>).</summary>
    public const string Bye = "bye";

    /// <summary>The recorder to a window, in answer to its hello: the run and its state (see <see cref="Link.Welcome"/>).</summary>
    public const string Welcome = "welcome";

    /// <summary>The recorder to a window, in answer to a hello in another protocol (see <see cref="Link.Refused"/>).</summary>
    public const string Refused = "refused";

    /// <summary>The recorder to a window: a line of its log (see <see cref="LogLine"/>).</summary>
    public const string Log = "log";

    /// <summary>The recorder to a window: the run's state, on every change and once a second (see <see cref="StateUpdate"/>).</summary>
    public const string State = "state";

    /// <summary>The recorder to a window, last: how the run ended (see <see cref="Link.End"/>).</summary>
    public const string End = "end";
}

/// <summary>
/// The keys a window passes on to the recorder, by the fixed names the link knows them by. Nothing else a window types
/// crosses the link, and the recorder passes over any other name.
/// </summary>
public static class LinkKeys
{
    /// <summary>Q: pressed twice within 5 seconds, it stops the recording.</summary>
    public const string Q = "Q";

    /// <summary>P: pauses or resumes the recording.</summary>
    public const string P = "P";

    /// <summary>M: marks the moment in the recording.</summary>
    public const string M = "M";

    /// <summary>V: switches the microphone off or on.</summary>
    public const string V = "V";

    /// <summary>Ctrl+C: pressed twice within 5 seconds while recording, it stops; after the stop, it cancels the transcription.</summary>
    public const string CtrlC = "CtrlC";

    /// <summary>True when <paramref name="key"/> is one of the names above, written exactly so.</summary>
    public static bool IsKnown([NotNullWhen(true)] string? key) => key is Q or P or M or V or CtrlC;
}

/// <summary>Why a window says bye.</summary>
public static class ByeReasons
{
    /// <summary>Esc: the window left the view of the run, which carries on.</summary>
    public const string Esc = "Esc";

    /// <summary>The window is closing.</summary>
    public const string Closed = "Closed";

    /// <summary>True when <paramref name="reason"/> is one of the reasons above, written exactly so.</summary>
    public static bool IsKnown([NotNullWhen(true)] string? reason) => reason is Esc or Closed;
}

/// <summary>One line of the link as it is read: the message's type, and its body, still as JSON until the type says what it holds.</summary>
/// <param name="Type">What the message is (see <see cref="LinkTypes"/>).</param>
/// <param name="Body">The message itself; undefined when the line has none, as a message type of a later build may not.</param>
public sealed record Envelope(string Type, JsonElement Body = default);

/// <summary>A window's first message: who it is, and the last line of the log it has already.</summary>
/// <param name="Protocol">The version of the protocol the window speaks (see <see cref="LinkProtocol.Version"/>). It stays first, and its name never changes, so that a recorder of any build can read it.</param>
/// <param name="Build">The window's build (see <see cref="LinkProtocol.Build"/>).</param>
/// <param name="Since">The number of the last line of the log the window has shown; 0 when it has shown none. The recorder sends the lines after it.</param>
/// <param name="Interactive">True when a person can type into the window: its input is not redirected.</param>
/// <param name="Alert">True for a window the recorder opened by itself, which ignores every key until Enter, and never opens the results.</param>
/// <param name="Owner">
/// True for the window that started the run. At the end, the owner opens the folder and the transcript when it is still
/// there; when it is not, the last window a person can type into does. Optional, so false when a window does not say.
/// </param>
public sealed record Hello(int Protocol, string Build, long Since, bool Interactive, bool Alert, bool Owner = false);

/// <summary>The recorder's answer to a window's hello in its own protocol: the run, its state now, and how far back the log goes.</summary>
/// <param name="Protocol">The version of the protocol the recorder speaks.</param>
/// <param name="Build">The recorder's build.</param>
/// <param name="ExePath">The ClassScribe.exe that records the run.</param>
/// <param name="Run">The run, as its run file says.</param>
/// <param name="State">The run's state now.</param>
/// <param name="OldestSeq">
/// The number of the oldest line the recorder still holds. When it is beyond the line after the window's
/// <see cref="Hello.Since"/>, the lines between are no longer held, and the window can say so.
/// </param>
public sealed record Welcome(int Protocol, string Build, string ExePath, RunRecord Run, RecorderState State, long OldestSeq);

/// <summary>The recorder's answer to a window that speaks another protocol: the window is not shown the run, and is told where to look instead.</summary>
/// <param name="Reason">Why, in words, such as which protocols the two speak.</param>
/// <param name="ExePath">The ClassScribe.exe that records the run, which can show it.</param>
public sealed record Refused(string Reason, string ExePath);

/// <summary>A line of the recorder's log, as the console would show it.</summary>
/// <param name="Seq">The line's number: 1 for the first line of the run's log, and one more for each line after it.</param>
/// <param name="Utc">When the line was written.</param>
/// <param name="Level">How much the line matters.</param>
/// <param name="Text">The line as the console formatter writes it, without a line break at its end; a line about a failure goes on over one more line for each exception.</param>
public sealed record LogLine(long Seq, DateTimeOffset Utc, LogLevel Level, string Text);

/// <summary>The run's state, for the window's title, its taskbar button and its view of the run.</summary>
/// <param name="State">The state.</param>
public sealed record StateUpdate(RecorderState State);

/// <summary>How the run ended: the last message a window gets.</summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Open">
/// True only for the one window that is to open the folder and the transcript, as the settings say: the window that
/// started the run, or, when it has gone, the last window a person can type into that attached, never a window the
/// recorder opened by itself. False for every other window, and for all of them with --no-open.
/// </param>
public sealed record End(RunOutcome Outcome, bool Open);

/// <summary>A recording key pressed in a window.</summary>
/// <param name="Key">The key, by its name in <see cref="LinkKeys"/>.</param>
public sealed record KeyPress(string Key);

/// <summary>A window's last message: it stops showing the run, which carries on.</summary>
/// <param name="Reason">Why, by its name in <see cref="ByeReasons"/>.</param>
public sealed record Bye(string Reason);
