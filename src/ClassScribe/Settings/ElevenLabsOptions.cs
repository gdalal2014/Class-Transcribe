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

    /// <summary>The longest file the settings take, in hours: <see cref="LongestFileHours"/> is from 1.</summary>
    public const double ShortestLongestFileHours = 1;

    /// <summary>The longest file the settings take, in hours: <see cref="LongestFileHours"/> is at most a day.</summary>
    public const double LargestLongestFileHours = 24;

    /// <summary>
    /// Base address of the API: an https address, or http to 127.0.0.1 or localhost only, for a local stub in a dry run, since
    /// the key travels in a header of every request.
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.elevenlabs.io/";

    /// <summary>
    /// The version of ElevenLabs' API the requests go to, the first part of their path, as v1 in /v1/speech-to-text: v and one
    /// to three digits. One other than the version this build's requests and answers were written for
    /// (Transcription.ElevenLabsApi.WrittenFor) is warned about, as ElevenLabs may refuse them or answer in another shape:
    /// moving to a new version is still a change of the code. A release that moves the default adds a
    /// <see cref="DefaultChanges"/> row.
    /// </summary>
    public string ApiVersion { get; init; } = "v1";

    /// <summary>Transcription model.</summary>
    public string ModelId { get; init; } = "scribe_v2";

    /// <summary>ISO language code. Empty lets ElevenLabs detect the language.</summary>
    public string LanguageCode { get; init; } = "";

    /// <summary>Tag sounds such as laughter in the transcript.</summary>
    public bool TagAudioEvents { get; init; }

    /// <summary>Ask the model to drop filler words and false starts.</summary>
    public bool NoVerbatim { get; init; }

    /// <summary>
    /// The glossary of every recording no saved class names one for, one term per line: none by default, as ClassScribe ships
    /// none (architecture 8.2). Environment variables such as %APPDATA% are expanded, quotes around the path are ignored, and
    /// a relative path is taken from the folder of the settings in force and from nowhere else (see
    /// <see cref="SettingsPlaces.ResolveFile(string)"/>). Empty, or none, sends no glossary. A recording's glossary is decided
    /// as it starts and kept with it (see <see cref="Transcription.GlossaryChoice"/>).
    /// </summary>
    public string KeytermsFile { get; init; } = "";

    /// <summary>
    /// The most terms one request's glossary sends; the rest of a longer glossary is left out, with a warning. ElevenLabs takes
    /// 1,000. From 1 to 10,000: one above ElevenLabs' own is warned about, since ElevenLabs refuses the whole request over it.
    /// </summary>
    public int MaxKeyterms { get; init; } = 1000;

    /// <summary>
    /// A term of the glossary must be shorter than this many characters, or it is left out, with a warning. ElevenLabs takes
    /// terms shorter than 50. From 2 to 1,000: one above ElevenLabs' own is warned about.
    /// </summary>
    public int KeytermCharacterLimit { get; init; } = 50;

    /// <summary>
    /// The most words in a term of the glossary; a longer term is left out, with a warning. ElevenLabs takes 5. From 1 to 50:
    /// one above ElevenLabs' own is warned about.
    /// </summary>
    public int MaxKeytermWords { get; init; } = 5;

    /// <summary>
    /// The most audio one file to ElevenLabs may hold, in hours: ElevenLabs takes 10. ClassScribe sends at most half an hour
    /// less, refusing a longer recording before anything is prepared; the safety stop (Recording:StopAfterHours) comes there
    /// at the latest, and a YouTube video's recording (YouTube:LongestVideoHours) an hour less (see
    /// <see cref="Transcription.TranscriptionLimits"/>). From 1 to 24: one above ElevenLabs' own is warned about.
    /// </summary>
    public double LongestFileHours { get; init; } = 10;

    /// <summary>
    /// The least time one request is given, covering the upload and the wait for the transcript. A longer upload gets
    /// more, as <see cref="SlowestUploadMbps"/> allows (see <see cref="Transcription.UploadTimeLimit"/>). From 1 to 1440.
    /// </summary>
    public int TimeoutMinutes { get; init; } = 60;

    /// <summary>
    /// The slowest upload speed, in megabits per second, that the time limit of a request allows for, so that a long
    /// recording sent over a slow line gets the time its upload needs. The limit never falls below
    /// <see cref="TimeoutMinutes"/>. From 0.1 to 1000.
    /// </summary>
    public double SlowestUploadMbps { get; init; } = 1;

    /// <summary><see cref="TimeoutMinutes"/> as a time span, at least one minute.</summary>
    public TimeSpan Timeout => TimeSpan.FromMinutes(Math.Max(1, TimeoutMinutes));

    /// <summary>How many times a request is tried when the failure looks temporary.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Base pause between attempts. The pause grows with each attempt.</summary>
    public int RetryDelaySeconds { get; init; } = 5;

    /// <summary>
    /// What ElevenLabs charges for an hour of audio through the API, in US dollars, for the estimate logged after each
    /// transcript: Scribe v2 is listed at 0.22 (https://elevenlabs.io/pricing/api). It is used for the estimate alone; the
    /// account's own numbers come from <see cref="ReadAccountUsage"/>. 0 logs the audio's length without a price. From 0 to 100.
    /// </summary>
    public double PricePerHourUsd { get; init; } = 0.22;

    /// <summary>
    /// What keyterm prompting adds for an hour of audio, in US dollars, counted in the estimate when a glossary is sent:
    /// 0.05 on the API price list (the API reference says 20% of the base price, 0.044). From 0 to 100.
    /// </summary>
    public double KeytermsPricePerHourUsd { get; init; } = 0.05;

    /// <summary>
    /// Read the account's credits from ElevenLabs before and after each transcript (GET /v1/user/subscription), when the
    /// key is allowed to (the User permission), and log what the transcript cost in credits. false asks nothing; the
    /// estimate from the price is logged either way.
    /// </summary>
    public bool ReadAccountUsage { get; init; } = true;
}
