namespace ClassScribe.Settings;

/// <summary>
/// The bytes of appsettings.json as read at one moment, where the file is, and which file it was (see
/// <see cref="SettingsOrigin"/>). <see cref="Bytes"/> is null when there is no file.
/// </summary>
public sealed record SettingsText(string Path, byte[]? Bytes, SettingsOrigin Origin = SettingsOrigin.BesideTheProgram);
