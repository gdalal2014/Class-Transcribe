namespace ClassScribe.Settings;

/// <summary>Settings for the ElevenLabs speech-to-text call.</summary>
public sealed class ElevenLabsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ElevenLabs";

    /// <summary>Environment variable that holds the API key. It is the name ElevenLabs' own samples use.</summary>
    public const string ApiKeyVariable = "ELEVENLABS_API_KEY";

    /// <summary>API key. Leave it out of appsettings.json and set the environment variable instead.</summary>
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

    /// <summary>Glossary file, one term per line. Relative paths are resolved next to the executable.</summary>
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
