namespace ClassScribe.Settings;

/// <summary>
/// The defaults that only the settings file used to hold: the Logging section. Every other value in the shipped
/// appsettings.json is a default of its options class already, so the file can be a template that an update replaces, and
/// a key missing from the user's own copy keeps the shipped default. AppHost adds these as the lowest settings source, so
/// the file's values win per key, and the staged check sees them too. A test keeps the shipped file equal to the code.
/// </summary>
public static class DefaultSettings
{
    /// <summary>
    /// The Logging keys and their defaults: ClassScribe's own lines at Information, the Microsoft and System categories at
    /// Warning, which keeps the HttpClient's chatter out of the window, and the plain one-line console format.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Values { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft"] = "Warning",
        ["Logging:LogLevel:System"] = "Warning",
        ["Logging:Console:FormatterName"] = PlainConsoleFormatter.FormatterName
    };
}
