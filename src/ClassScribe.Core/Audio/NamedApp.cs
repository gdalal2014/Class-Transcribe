namespace ClassScribe.Core.Audio;

/// <summary>
/// The app a name was given for (--app, Recording:App, a saved class's App, --mic-follows, Recording:MicrophoneFollows),
/// and which processes are its: a whole name beats a part. A process whose name is the name given, in any case, is the
/// app's. One whose name only contains it is the app's too, so that "edge" finds msedge and "teams" ms-teams, but only
/// until a process of exactly that name has been seen: from then on the name is that app's alone, for as long as this
/// object lives, which is one run. So "msedge" is Edge and never msedgewebview2, Edge's engine inside other apps (the new
/// Teams, WhatsApp, Windows' search), whose name only begins as Edge's does; and once Edge has been seen, a namesake is
/// not taken in its place when it closes. Until the app itself is first seen nothing tells a whole name from a part, so a
/// namesake counts until then.
/// </summary>
/// <remarks>
/// It reads nothing itself: whoever looks tells it the names each look found (<see cref="See"/>) and then asks
/// (<see cref="Is"/>). Only what a look finds now counts as seen, never a record of the past, in which a program long
/// uninstalled would make a part someone still relies on into a whole name. Not for two threads at once: the chooser asks
/// from the session's look, the microphone watch with its lock held.
/// </remarks>
/// <param name="name">The name as it was given, without .exe: a process's whole name, or part of one.</param>
public sealed class NamedApp(string name)
{
    /// <summary>The name as it was given.</summary>
    public string Name => name;

    /// <summary>True once a process of exactly the name given has been seen: the name is that app's alone from then on.</summary>
    public bool IsWholeName { get; private set; }

    /// <summary>
    /// Takes note of the process names one look found, null for a process that could not be named: one that is the name
    /// given, in any case, makes the name that app's alone, from this look on. Call it before <see cref="Is"/>, at each look.
    /// </summary>
    public void See(IEnumerable<string?> processNames)
    {
        if (IsWholeName) return;

        IsWholeName = processNames.Any(IsNamedInFull);
    }

    /// <summary>
    /// True when the process named <paramref name="processName"/>, without .exe, is the app's, as far as the looks so far
    /// tell: its name is the name given, or, while no process of exactly that name has been seen, contains it; in any case.
    /// </summary>
    public bool Is(string processName) => IsWholeName ? IsNamedInFull(processName) : processName.Contains(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="processName"/> is the name given, in any case; false for a process that could not be named.</summary>
    private bool IsNamedInFull(string? processName) => string.Equals(processName, name, StringComparison.OrdinalIgnoreCase);
}
