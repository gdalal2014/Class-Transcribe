using System.Net;
using System.Text.RegularExpressions;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.Tests;

/// <summary>The ElevenLabs request and response handling, through the Refit interface and a stub HTTP handler.</summary>
[TestFixture]
public class ElevenLabsClientTests
{
    private const string Answer = """
        {
          "language_code": "en",
          "language_probability": 0.98,
          "text": "Hello world.",
          "audio_duration_secs": 1.5,
          "transcription_id": "abc123",
          "words": [
            { "text": "Hello", "start": 0.0, "end": 0.5, "type": "word", "speaker_id": "speaker_0", "logprob": -0.1 },
            { "text": " ", "start": 0.5, "end": 0.6, "type": "spacing", "speaker_id": "speaker_0", "logprob": 0.0 },
            { "text": "world.", "start": 0.6, "end": 1.0, "type": "word", "speaker_id": "speaker_0", "logprob": -0.2 }
          ]
        }
        """;

    private TempFolder _folder = null!;
    private StubHttpHandler _handler = null!;
    private string _audioPath = null!;

    /// <summary>Creates a temporary folder with a small audio file in it, and the stub transport.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _handler = new StubHttpHandler();
        _audioPath = _folder.File("upload.wav");
        File.WriteAllBytes(_audioPath, new byte[2048]);
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Creates the client under test: the Refit interface on top of the stub transport.</summary>
    private ElevenLabsClient CreateClient(ElevenLabsOptions? options = null)
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") };
        var api = RestService.ForGenerated<IElevenLabsApi>(http, ElevenLabsJson.Default, ElevenLabsApiSettings.Create());
        return new ElevenLabsClient(api, Options.Create(options ?? new ElevenLabsOptions { ApiKey = "test-key", RetryDelaySeconds = 0 }), NullLogger<ElevenLabsClient>.Instance);
    }

    /// <summary>Returns the values of every form field with the given name, in the order they were sent. The name may be quoted or bare.</summary>
    private static List<string> FieldValues(string body, string name) =>
        Regex.Matches(body, $"name=(?:\"{Regex.Escape(name)}\"|{Regex.Escape(name)})(?=[;\r])[^\r\n]*(?:\r\n[^\r\n]+)*\r\n\r\n(?<value>.*?)\r\n--", RegexOptions.Singleline)
            .Select(m => m.Groups["value"].Value)
            .ToList();

    /// <summary>Every field ElevenLabs needs is sent once, each glossary term as its own field, and the audio streams with a known length.</summary>
    [Test]
    public async Task The_request_carries_every_field_ElevenLabs_needs()
    {
        _handler.Enqueue(HttpStatusCode.OK, Answer);

        await CreateClient().TranscribeAsync(_audioPath, ["kalām", "wājib al-wujūd"], CancellationToken.None);

        var request = _handler.Requests.Single();
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri!.AbsoluteUri, Is.EqualTo("https://api.elevenlabs.io/v1/speech-to-text"));
        Assert.That(request.ApiKey, Is.EqualTo("test-key"));
        Assert.That(request.ContentType, Does.StartWith("multipart/form-data; boundary="));
        Assert.That(request.ContentLength, Is.GreaterThan(2048), "the length is known before sending, so the file is not sent in chunks");
        Assert.That(FieldValues(request.Body, "model_id"), Is.EqualTo(new[] { "scribe_v2" }));
        Assert.That(FieldValues(request.Body, "tag_audio_events"), Is.EqualTo(new[] { "false" }));
        Assert.That(FieldValues(request.Body, "timestamps_granularity"), Is.EqualTo(new[] { "word" }));
        Assert.That(FieldValues(request.Body, "keyterms"), Is.EqualTo(new[] { "kalām", "wājib al-wujūd" }));
        Assert.That(FieldValues(request.Body, "language_code"), Is.Empty);
        Assert.That(FieldValues(request.Body, "no_verbatim"), Is.Empty);
        Assert.That(request.Body, Does.Match("name=\"?file\"?; filename=\"?audio\\.wav\"?"));
    }

    /// <summary>Settings that are off by default are sent once they are switched on.</summary>
    [Test]
    public async Task Optional_settings_are_sent_when_configured()
    {
        _handler.Enqueue(HttpStatusCode.OK, Answer);
        var options = new ElevenLabsOptions { ApiKey = "test-key", LanguageCode = "en", NoVerbatim = true, TagAudioEvents = true, ModelId = "scribe_v2" };

        await CreateClient(options).TranscribeAsync(_audioPath, [], CancellationToken.None);

        var body = _handler.Requests.Single().Body;
        Assert.That(FieldValues(body, "language_code"), Is.EqualTo(new[] { "en" }));
        Assert.That(FieldValues(body, "no_verbatim"), Is.EqualTo(new[] { "true" }));
        Assert.That(FieldValues(body, "tag_audio_events"), Is.EqualTo(new[] { "true" }));
        Assert.That(FieldValues(body, "keyterms"), Is.Empty);
    }

    /// <summary>The fields the app uses are read, and the JSON is kept exactly as it arrived.</summary>
    [Test]
    public async Task The_answer_is_parsed_and_kept_verbatim()
    {
        _handler.Enqueue(HttpStatusCode.OK, Answer);

        var result = await CreateClient().TranscribeAsync(_audioPath, [], CancellationToken.None);

        Assert.That(result.Text, Is.EqualTo("Hello world."));
        Assert.That(result.LanguageCode, Is.EqualTo("en"));
        Assert.That(result.AudioDurationSecs, Is.EqualTo(1.5));
        Assert.That(result.TranscriptionId, Is.EqualTo("abc123"));
        Assert.That(result.Words, Has.Count.EqualTo(3));
        Assert.That(result.Words[2], Is.EqualTo(new TranscriptWord("world.", 0.6, 1.0, "word", "speaker_0")));
        Assert.That(result.RawJson, Is.EqualTo(Answer));
    }

    /// <summary>A server error is temporary, so the upload is tried again.</summary>
    [Test]
    public async Task A_server_error_is_retried()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError, "{}");
        _handler.Enqueue(HttpStatusCode.OK, Answer);

        var result = await CreateClient().TranscribeAsync(_audioPath, [], CancellationToken.None);

        Assert.That(result.Text, Is.EqualTo("Hello world."));
        Assert.That(_handler.Requests, Has.Count.EqualTo(2));
    }

    /// <summary>A broken connection is temporary, so the upload is tried again.</summary>
    [Test]
    public async Task A_network_failure_is_retried()
    {
        _handler.EnqueueFailure(new HttpRequestException("Connection reset."));
        _handler.Enqueue(HttpStatusCode.OK, Answer);

        var result = await CreateClient().TranscribeAsync(_audioPath, [], CancellationToken.None);

        Assert.That(result.Text, Is.EqualTo("Hello world."));
        Assert.That(_handler.Requests, Has.Count.EqualTo(2));
    }

    /// <summary>Temporary failures are retried only as often as configured, then reported with their status.</summary>
    [Test]
    public void It_gives_up_after_the_configured_number_of_attempts()
    {
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, "busy");
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, "busy");
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, "busy");
        var client = CreateClient(new ElevenLabsOptions { ApiKey = "test-key", MaxAttempts = 3, RetryDelaySeconds = 0 });

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(_handler.Requests, Has.Count.EqualTo(3));
    }

    /// <summary>A rejected key will not work on a second try either, and the message says what to check.</summary>
    [Test]
    public void A_rejected_key_is_not_retried_and_says_what_to_check()
    {
        _handler.Enqueue(HttpStatusCode.Unauthorized, """{"detail":{"status":"invalid_api_key"}}""");
        var client = CreateClient();

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(error.Message, Does.Contain("ELEVENLABS_API_KEY"));
        Assert.That(error.Message, Does.Contain("invalid_api_key"));
        Assert.That(_handler.Requests, Has.Count.EqualTo(1));
    }

    /// <summary>The app's own time limit is a failure with a clear message, not a cancellation, and is not retried.</summary>
    [Test]
    public void Running_out_of_time_is_reported_as_a_failure_not_as_a_cancellation()
    {
        // This is how HttpClient reports its own timeout: a cancellation nobody asked for.
        _handler.EnqueueFailure(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout elapsing.", new TimeoutException()));
        _handler.Enqueue(HttpStatusCode.OK, Answer);
        var client = CreateClient();

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("did not answer"));
        Assert.That(_handler.Requests, Has.Count.EqualTo(1), "not sent again: the same wait would follow");
    }

    /// <summary>Ctrl+C during the upload surfaces as a cancellation, so the app can report it as one.</summary>
    [Test]
    public async Task A_cancellation_by_the_user_stays_a_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        _handler.EnqueueFailure(() =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        });
        var client = CreateClient();

        OperationCanceledException? caught = null;
        try
        {
            await client.TranscribeAsync(_audioPath, [], cancellation.Token);
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }

        Assert.That(caught, Is.Not.Null);
        Assert.That(_handler.Requests, Has.Count.EqualTo(1));
    }

    /// <summary>Without a key the request could only fail, so it is not sent.</summary>
    [Test]
    public void Without_a_key_nothing_is_sent()
    {
        var client = CreateClient(new ElevenLabsOptions { ApiKey = "  " });

        Assert.ThrowsAsync<InvalidOperationException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));
        Assert.That(_handler.Requests, Is.Empty);
    }

    /// <summary>A successful answer without a transcript is an error that keeps the answer for diagnosis.</summary>
    [Test]
    public void An_answer_without_text_is_an_error()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"message":"queued"}""");
        var client = CreateClient();

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(error!.ResponseBody, Does.Contain("queued"));
    }
}
