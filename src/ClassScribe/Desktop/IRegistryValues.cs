namespace ClassScribe.Desktop;

/// <summary>
/// Reads values under HKEY_CURRENT_USER, where Lenovo Vantage keeps its microphone mode and Windows keeps the ducking
/// preference. A key or value that does not exist reads as null; nothing is ever written.
/// </summary>
public interface IRegistryValues
{
    /// <summary>
    /// The value <paramref name="name"/> under <paramref name="currentUserKey"/> (a path below HKEY_CURRENT_USER, such as
    /// Software\Lenovo\SmartEngine\modules\F4006) as text, whatever its kind: a string as it is, a number written out.
    /// Null when the key or the value does not exist.
    /// </summary>
    string? ReadString(string currentUserKey, string name);

    /// <summary>
    /// The value <paramref name="name"/> under <paramref name="currentUserKey"/> when it is a number (REG_DWORD). Null when
    /// the key or the value does not exist, or the value is of another kind.
    /// </summary>
    int? ReadDword(string currentUserKey, string name);
}
