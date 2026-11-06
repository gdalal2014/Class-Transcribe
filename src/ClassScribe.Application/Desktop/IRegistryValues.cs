namespace ClassScribe.Desktop;

/// <summary>
/// Reads values under HKEY_CURRENT_USER, where Lenovo Vantage keeps its microphone mode, Windows keeps the ducking preference
/// and the user's PATH, and under HKEY_LOCAL_MACHINE, where Windows keeps the machine's PATH, which yt-dlp's tools are looked
/// for on. A key or value that does not exist reads as null; nothing is ever written.
/// </summary>
public interface IRegistryValues
{
    /// <summary>
    /// The value <paramref name="name"/> under <paramref name="currentUserKey"/> (a path below HKEY_CURRENT_USER, such as
    /// Software\Lenovo\SmartEngine\modules\F4006) as text, whatever its kind: a string as it is, with the environment
    /// variables of an expandable one expanded, a number written out. Null when the key or the value does not exist.
    /// </summary>
    string? ReadString(string currentUserKey, string name);

    /// <summary>
    /// The value <paramref name="name"/> under <paramref name="currentUserKey"/> when it is a number (REG_DWORD). Null when
    /// the key or the value does not exist, or the value is of another kind.
    /// </summary>
    int? ReadDword(string currentUserKey, string name);

    /// <summary>
    /// The value <paramref name="name"/> under <paramref name="localMachineKey"/> (a path below HKEY_LOCAL_MACHINE, such as
    /// SYSTEM\CurrentControlSet\Control\Session Manager\Environment) as text, as <see cref="ReadString"/> gives it. Null when
    /// the key or the value does not exist.
    /// </summary>
    string? ReadLocalMachineString(string localMachineKey, string name);
}
