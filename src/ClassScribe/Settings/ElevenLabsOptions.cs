namespace ClassScribe.Settings;

/// <summary>Settings for the ElevenLabs speech-to-text call.</summary>
public sealed class ElevenLabsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ElevenLabs";

    /// <summary>Environment variable that holds the API key. It is the name ElevenLabs' own samples use.</summary>
    public const string ApiKeyVariable = "ELEVENLABS_API_KEY";

    /// <summary>The setting the API key is bound from. Only <see cref="ApiKeyFromEnvironment"/> fills it.</summary>
    public const string ApiKeySetting = $"{SectionName}:{nameof(ApiKey)}";

    /// <summary>
    /// API key, taken from the <see cref="ApiKeyVariable"/> environment variable and nowhere else (see
    /// <see cref="ApiKeyFromEnvironment"/>). A key written into appsettings.json is ignored, with a warning.
    /// </summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Base address of the API.</summary>
    public string BaseUrl { get; init; } = "https://api.elevenlabs.io/";

    /// <summary>Transcription model.</summary>
    public string ModelId { get; init; } = "scribe_v2";

    /// <summary>ISO language code. Empty lets ElevenLabs detect the language.</summary>
    public string LanguageCode { get; init; } = "";

    /// <summary>Tag sounds such as laughter in the transcript.</summary>
    public bool TagAudioEvents { get; init; }

    /// <summary>Ask the model to drop filler words and false starts.</summary>
    public bool NoVerbatim { get; init; }

    /// <summary>
    /// Glossary file, one term per line. Environment variables such as %USERPROFILE% are expanded, quotes around
    /// the path are ignored, and a relative path is taken next to the executable. An empty value turns the
    /// glossary off (see <see cref="Transcription.KeytermLoader.ResolvePath"/>).
    /// </summary>
    public string KeytermsFile { get; init; } = "keyterms.txt";

    /// <summary>Longest time one request may take, covering the upload and the wait for the transcript.</summary>
    public int TimeoutMinutes { get; init; } = 60;

    /// <summary><see cref="TimeoutMinutes"/> as a time span, at least one minute.</summary>
    public TimeSpan Timeout => TimeSpan.FromMinutes(Math.Max(1, TimeoutMinutes));

    /// <summary>How many times a request is tried when the failure looks temporary.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Base pause between attempts. The pause grows with each attempt.</summary>
    public int RetryDelaySeconds { get; init; } = 5;
}
