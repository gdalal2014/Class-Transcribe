using ClassScribe.Desktop;

namespace ClassScribe.YouTube;

/// <summary>A video's window as the batch opened it: Chrome's browser process, the root of the tree a video's run records.</summary>
/// <param name="Process">The process, with its id, its start time and its exit code once it has ended.</param>
public sealed record OpenedWindow(StartedProcess Process);

/// <summary>What opening a video's window came to: the window, or why there is none.</summary>
/// <param name="Window">The window; null when it did not open.</param>
/// <param name="Problem">Why it did not open, in words Gulzar reads; null when it did.</param>
/// <param name="HandedOff">True when Chrome handed the video to a window of ClassScribe's left open from before: Enter tries again once it is closed.</param>
public sealed record WindowOpening(OpenedWindow? Window, string? Problem, bool HandedOff = false);
