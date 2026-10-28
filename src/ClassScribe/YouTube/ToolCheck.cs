namespace ClassScribe.YouTube;

/// <summary>What the check of yt-dlp and its helpers found.</summary>
/// <param name="Paths">The tools' paths, when all four were found and said their versions; null otherwise.</param>
/// <param name="YtDlpVersion">yt-dlp's version, such as 2026.08.19, when it said one.</param>
/// <param name="Lines">One line per tool found: its name, version and path.</param>
/// <param name="Problems">What keeps a download from running: a tool missing, with the winget command, or one that would not say its version.</param>
/// <param name="Warning">That yt-dlp is more than 90 days old, with the update command; null when it is not.</param>
public sealed record ToolCheck(ToolPaths? Paths, string? YtDlpVersion, IReadOnlyList<string> Lines, IReadOnlyList<string> Problems, string? Warning);
