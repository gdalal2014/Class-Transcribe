using Microsoft.Extensions.Configuration;

namespace ClassScribe.Settings;

/// <summary>
/// appsettings.json, as far as it passed the check: the settings source that stands where the file's own source
/// would. It holds only values that <see cref="SettingsReloader"/> has accepted, and it never reads the disk itself,
/// so neither an edit nor a reload can bring an unchecked value into the app.
/// </summary>
public sealed class CheckedSettingsSource : IConfigurationSource
{
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, string?> _values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The values accepted last, by setting name, such as Recording:FlushSeconds.</summary>
    public IReadOnlyDictionary<string, string?> Values
    {
        get { lock (_gate) return _values; }
    }

    /// <summary>Takes <paramref name="values"/> in place of those accepted before. The settings see them at their next reload.</summary>
    public void Accept(IReadOnlyDictionary<string, string?> values)
    {
        var copy = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        lock (_gate) _values = copy;
    }

    /// <summary>Builds the provider that hands the accepted values to the settings.</summary>
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(this);

    /// <summary>Copies the accepted values each time the settings are loaded.</summary>
    private sealed class Provider(CheckedSettingsSource source) : ConfigurationProvider
    {
        /// <summary>Takes the values accepted last.</summary>
        public override void Load() => Data = new Dictionary<string, string?>(source.Values, StringComparer.OrdinalIgnoreCase);
    }
}
