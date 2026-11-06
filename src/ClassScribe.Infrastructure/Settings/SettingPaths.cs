namespace ClassScribe.Settings;

/// <summary>A path setting as the adapters use it: the settings check and the searches for yt-dlp and Chrome.</summary>
internal static class SettingPaths
{
    /// <summary>A path setting as it is used: environment variables such as %LOCALAPPDATA% expanded, and the quotes and spaces around it gone.</summary>
    internal static string Expanded(string? path) => Environment.ExpandEnvironmentVariables(path ?? "").Trim().Trim('"').Trim();
}
