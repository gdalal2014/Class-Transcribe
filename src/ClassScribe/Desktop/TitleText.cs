using System.Globalization;
using System.Text;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;

namespace ClassScribe.Desktop;

/// <summary>
/// The window's title and taskbar button for a run's state (design 1.13): what Gulzar sees at a glance on the title
/// bar, the terminal's tab and the taskbar while a class records, uploads and ends.
/// </summary>
/// <remarks>
/// <para>Titles, each ending with "| ClassScribe", and "(in this window)" before that for a run recording in the
/// window rather than in the background:</para>
/// <list type="bullet">
/// <item>recording: <c>REC 01:12:40 Kalam</c>, with <c>MIC OFF</c> before the name while the microphone is switched
/// off, <c>MIC MUTED</c> while the class app it follows is not listening or Windows mutes it, and <c>MIC WAITING</c>
/// while that app has not opened it, uses another, or cannot be read; paused: <c>PAUSED 01:12:40 Kalam</c>; a green
/// bar, or a yellow one while paused;</item>
/// <item>a Notice: its words in brackets after the name, <c>REC 01:12:40 Kalam (no sound 2 min)</c>; a yellow bar;</item>
/// <item>an Alert: "!" and its words before the name, <c>! NOT RECORDING (waiting for "chrome") Kalam</c>; a red bar;</item>
/// <item>uploading: <c>UPLOADING 40% Kalam</c>, a green bar at 40%; transcribing: <c>TRANSCRIBING Kalam</c>, a running bar;</item>
/// <item>at the end: <c>DONE Kalam</c> with no bar, <c>! FAILED Kalam</c> with a red one, <c>CANCELLED Kalam</c> with no bar.</item>
/// </list>
/// <para>So an <see cref="Attention.Short"/> reads as upper-case words for an Alert, such as <c>CANNOT WRITE</c>, and as
/// a few lower-case words for a Notice, such as <c>no sound 2 min</c>. A run that has ended shows how it ended, whatever
/// needed attention before. While nothing can be recorded and nothing needs attention yet, the title stays REC, as it
/// does while a device is replaced; the Alert takes over after 15 seconds.</para>
/// </remarks>
public static class TitleText
{
    /// <summary>The name every title ends with, after a bar: "| ClassScribe".</summary>
    public const string AppName = "ClassScribe";

    /// <summary>What a run recording in the window, rather than in the background, adds to its title: closing that window ends it.</summary>
    public const string InWindowNote = "(in this window)";

    /// <summary>The title for <paramref name="state"/>, such as <c>REC 01:12:40 Kalam | ClassScribe</c>.</summary>
    public static string For(RecorderState state)
    {
        var attention = HasEnded(state.Phase) ? Attention.None : AttentionOf(state);
        var words = Clean(attention.Short);
        var name = Clean(state.Name);

        List<string> title = attention.Level switch
        {
            AttentionLevel.Alert => ["!", words.Length > 0 ? words : PhaseWords(state), name],
            AttentionLevel.Notice when words.Length > 0 => [PhaseWords(state), name, $"({words})"],
            _ => [PhaseWords(state), name]
        };

        title.RemoveAll(part => part.Length == 0);
        if (StopsAt(state) is { } stopsAt) title.Add($"(stops {stopsAt})");
        if (state.InWindow) title.Add(InWindowNote);
        title.Add("|");
        title.Add(AppName);
        return string.Join(' ', title);
    }

    /// <summary>
    /// When the run stops by itself, as the title says it while the class records (--minutes, or the safety stop near):
    /// the time of day the run wrote for it; null once the recording has stopped, or when it does not stop by itself.
    /// </summary>
    private static string? StopsAt(RecorderState state) =>
        state.Phase is RecorderPhase.Recording or RecorderPhase.NotRecording or RecorderPhase.Paused && Clean(state.StopsAtText) is { Length: > 0 } stopsAt
            ? stopsAt
            : null;

    /// <summary>
    /// What the taskbar button shows for <paramref name="state"/>: red for an Alert or a failure, yellow for a Notice or a
    /// pause, green while recording and uploading, a running bar while the run waits on work whose progress is not known,
    /// and nothing once it is done or cancelled.
    /// </summary>
    public static TaskbarState TaskbarFor(RecorderState state)
    {
        switch (state.Phase)
        {
            case RecorderPhase.Done or RecorderPhase.Cancelled:
                return TaskbarState.None;
            case RecorderPhase.Failed:
                return TaskbarState.Error;
        }

        return AttentionOf(state).Level switch
        {
            AttentionLevel.Alert => TaskbarState.Error,
            AttentionLevel.Notice => TaskbarState.Paused,
            _ => state.Phase switch
            {
                RecorderPhase.Recording or RecorderPhase.NotRecording => TaskbarState.Normal,
                RecorderPhase.Paused => TaskbarState.Paused,
                RecorderPhase.Uploading when state.UploadPercent is not null => TaskbarState.Normal,
                _ => TaskbarState.Indeterminate
            }
        };
    }

    /// <summary>
    /// How far the taskbar button's bar is filled for <paramref name="state"/>, from 0 to 100: the upload's progress while
    /// uploading, 100 for any other state with a bar, so that its colour fills the button, and 0 for a state without one.
    /// </summary>
    public static int TaskbarPercentFor(RecorderState state) => TaskbarFor(state) switch
    {
        TaskbarState.None or TaskbarState.Indeterminate => 0,
        _ when state.Phase == RecorderPhase.Uploading && state.UploadPercent is { } percent => Math.Clamp(percent, 0, 100),
        _ => 100
    };

    /// <summary>
    /// <paramref name="text"/> fit for a title: each control character, which could ring (a BEL) or start a sequence (an
    /// escape), becomes a space, runs of spaces become one, and none is left at either end.
    /// </summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var clean = new StringBuilder(text.Length);
        var space = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                space = clean.Length > 0;
                continue;
            }

            if (space) clean.Append(' ');
            space = false;
            clean.Append(character);
        }

        return clean.ToString();
    }

    /// <summary>
    /// What needs attention in <paramref name="state"/>; nothing when the state carries no attention at all, as a state
    /// read from JSON without one does (System.Text.Json leaves a missing member null, whatever its type says).
    /// </summary>
    internal static Attention AttentionOf(RecorderState state) => state.Attention ?? Attention.None;

    /// <summary>True for the phases a run ends in.</summary>
    private static bool HasEnded(RecorderPhase phase) => phase is RecorderPhase.Done or RecorderPhase.Failed or RecorderPhase.Cancelled;

    /// <summary>The words for the phase itself, before the name, such as <c>REC 01:12:40</c> or <c>UPLOADING 40%</c>.</summary>
    private static string PhaseWords(RecorderState state) => state.Phase switch
    {
        RecorderPhase.Starting => "STARTING",
        RecorderPhase.Recording or RecorderPhase.NotRecording => $"REC {Durations.Format(state.Position)}{MicrophoneOff(state)}",
        RecorderPhase.Paused => $"PAUSED {Durations.Format(state.Position)}{MicrophoneOff(state)}",
        RecorderPhase.Stopping => "STOPPING",
        RecorderPhase.Preparing => "PREPARING",
        RecorderPhase.Uploading => state.UploadPercent is { } percent
            ? string.Create(CultureInfo.InvariantCulture, $"UPLOADING {Math.Clamp(percent, 0, 100)}%")
            : "UPLOADING",
        RecorderPhase.Transcribing => "TRANSCRIBING",
        RecorderPhase.Done => "DONE",
        RecorderPhase.Failed => "! FAILED",
        RecorderPhase.Cancelled => "CANCELLED",
        _ => state.Phase.ToString().ToUpperInvariant()
    };

    /// <summary>
    /// The microphone's words before the name: " MIC OFF" while it is switched off with V, or not yet switched on where the
    /// class app's mute cannot be seen; " MIC MUTED" while the app is not listening or Windows mutes the microphone;
    /// " MIC WAITING" while the app has not opened the microphone, uses another, or its sessions cannot be read; empty
    /// while your voice is recorded, when no app is followed, and when the run has no microphone.
    /// </summary>
    private static string MicrophoneOff(RecorderState state) => state.Microphone switch
    {
        null => "",
        { On: false } => " MIC OFF",
        { Gate: MicrophoneGateState.NotListening or MicrophoneGateState.MutedInWindows } => " MIC MUTED",
        { Gate: MicrophoneGateState.NotOpened or MicrophoneGateState.OtherMicrophone or MicrophoneGateState.Unreadable } => " MIC WAITING",
        _ => ""
    };
}
