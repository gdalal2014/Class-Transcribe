using Microsoft.Extensions.Configuration;

namespace ClassScribe.Settings;

/// <summary>
/// The settings source that fills ElevenLabs:ApiKey from the ELEVENLABS_API_KEY environment variable,
/// or leaves it empty when the variable is not set. Added after every other source, it wins over them,
/// so a key written into appsettings.json, or set as an ElevenLabs__ApiKey variable, is never used.
/// </summary>
public sealed class ApiKeyFromEnvironment(string? apiKey) : IConfigurationSource
{
    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(apiKey?.Trim() ?? "");

    /// <summary>
    /// True when a source other than this one holds a key, such as appsettings.json: a key that is not
    /// used and should be taken out, so it cannot end up in git or be shared by mistake.
    /// </summary>
    public static bool IsSetElsewhere(IConfiguration configuration) =>
        configuration is IConfigurationRoot root
        && root.Providers.Any(provider => provider is not Provider
            && provider.TryGet(ElevenLabsOptions.ApiKeySetting, out var key)
            && !string.IsNullOrWhiteSpace(key));

    /// <summary>Holds the one setting.</summary>
    private sealed class Provider(string apiKey) : ConfigurationProvider
    {
        /// <inheritdoc />
        public override void Load() => Data[ElevenLabsOptions.ApiKeySetting] = apiKey;
    }
}

/// <summary>Adds <see cref="ApiKeyFromEnvironment"/> to the settings.</summary>
public static class ApiKeyFromEnvironmentExtensions
{
    /// <summary>Adds the key from ELEVENLABS_API_KEY as a source. Add it last, so that it wins over every key added before it.</summary>
    public static IConfigurationBuilder AddApiKeyFromEnvironment(this IConfigurationBuilder builder, string? apiKey) =>
        builder.Add(new ApiKeyFromEnvironment(apiKey));
}
