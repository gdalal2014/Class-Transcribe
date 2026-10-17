using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// Reads values under HKEY_CURRENT_USER through Windows' registry, where Lenovo Vantage keeps its microphone mode and
/// Windows keeps the ducking preference. Keys are opened read-only and closed after each read; nothing is ever written.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryValues(ILogger<RegistryValues> logger) : IRegistryValues
{
    /// <inheritdoc />
    public string? ReadString(string currentUserKey, string name) =>
        Read(currentUserKey, name) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <inheritdoc />
    public int? ReadDword(string currentUserKey, string name) => Read(currentUserKey, name) as int?;

    /// <summary>The value as the registry holds it, or null when the key or the value does not exist. A read Windows refuses is logged and rethrown.</summary>
    private object? Read(string key, string name)
    {
        try
        {
            using var subKey = Registry.CurrentUser.OpenSubKey(key, writable: false);
            return subKey?.GetValue(name);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(error, "Could not read {Name} under HKEY_CURRENT_USER\\{Key}", name, key);
            throw;
        }
    }
}
