using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// Reads values through Windows' registry: under HKEY_CURRENT_USER, where Lenovo Vantage keeps its microphone mode, Windows
/// keeps the ducking preference and the user's PATH; and under HKEY_LOCAL_MACHINE, where Windows keeps the machine's PATH.
/// Keys are opened read-only and closed after each read; nothing is ever written.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryValues(ILogger<RegistryValues> logger) : IRegistryValues
{
    /// <inheritdoc />
    public string? ReadString(string currentUserKey, string name) =>
        Read(Registry.CurrentUser, currentUserKey, name) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <inheritdoc />
    public int? ReadDword(string currentUserKey, string name) => Read(Registry.CurrentUser, currentUserKey, name) as int?;

    /// <inheritdoc />
    public string? ReadLocalMachineString(string localMachineKey, string name) =>
        Read(Registry.LocalMachine, localMachineKey, name) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>The value as the registry holds it under <paramref name="hive"/>, or null when the key or the value does not exist. A read Windows refuses is logged and rethrown.</summary>
    private object? Read(RegistryKey hive, string key, string name)
    {
        try
        {
            using var subKey = hive.OpenSubKey(key, writable: false);
            return subKey?.GetValue(name);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(error, "Could not read {Name} under {Hive}\\{Key}", name, hive.Name, key);
            throw;
        }
    }
}
