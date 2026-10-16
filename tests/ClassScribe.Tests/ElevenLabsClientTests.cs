using System.Net;
using System.Text.RegularExpressions;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
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

    /// <summary>Creates the client under test: the Refit interface on top of the stub transport, with the system clock unless another is given.</summary>
    private ElevenLabsClient CreateClient(ElevenLabsOptions? options = null, TimeProvider? time = null, ILogger<ElevenLabsClient>? logger = null)
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") };
        var api = RestService.ForGenerated<IElevenLabsApi>(http, ElevenLabsJson.Default, ElevenLabsApiSettings.Create());
        return new ElevenLabsClient(
            api,
            Options.Create(options ?? new ElevenLabsOptions { ApiKey = "test-key", RetryDelaySeconds = 0 }),
            time ?? TimeProvider.System,
            logger ?? NullLogger<ElevenLabsClient>.Instance);
    }

    /// <summary>Returns the values of every form field with the given name, in the order they were sent. The name may be quoted or bare.</summary>
    private static List<string> FieldValues(string body, string name) =>
        Regex.Matches(body, $"name=(?:\"{Regex.Escape(name)}\"|{Regex.Escape(name)})(?=[;\r])[^\r\n]*(?:\r\n[^\r\n]+)*\r\n\r\n(?<value>.*?)\r\n--", RegexOptions.Singleline)
            .Select(m => m.Groups["value"].Value)
            .ToList();

    /// <summary>Every field ElevenLabs needs is sent once, each glossary term as its own field, and the request has a known length.</summary>
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

    /// <summary>The log of a received transcript gives its transcription ID at ElevenLabs, by which the transcript can be found there again.</summary>
    [Test]
    public async Task The_transcription_id_is_logged_with_the_transcript()
    {
        var log = new RecordingLogger<ElevenLabsClient>();
        _handler.Enqueue(HttpStatusCode.OK, Answer);

        await CreateClient(logger: log).TranscribeAsync(_audioPath, [], CancellationToken.None);

        Assert.That(log.Messages, Has.Some.StartsWith("Transcript received").And.EndsWith("transcription ID abc123"));
    }

    /// <summary>An answer that carries no transcription ID is logged as received all the same, without one.</summary>
    [Test]
    public async Task An_answer_without_a_transcription_id_is_logged_without_one()
    {
        var log = new RecordingLogger<ElevenLabsClient>();
        _handler.Enqueue(HttpStatusCode.OK, """{"text":"Hello.","words":[]}""");

        await CreateClient(logger: log).TranscribeAsync(_audioPath, [], CancellationToken.None);

        Assert.That(log.Messages, Has.Some.StartsWith("Transcript received").And.Not.Contains("transcription ID"));
    }

    /// <summary>
    /// A request that timed out at the server (408), a rate or concurrency limit (429) and a server error (5xx) are
    /// all temporary, so the upload is tried again.
    /// </summary>
    [TestCase(HttpStatusCode.RequestTimeout)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public async Task A_temporary_refusal_is_retried(HttpStatusCode status)
    {
        _handler.Enqueue(status, "{}");
        _handler.Enqueue(HttpStatusCode.OK, Answer);

        var result = await CreateClient().TranscribeAsync(_audioPath, [], CancellationToken.None);

        Assert.That(result.Text, Is.EqualTo("Hello world."));
        Assert.That(_handler.Requests, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// The pause before a new attempt grows with each attempt: RetryDelaySeconds before the second, twice that before
    /// the third, and the next attempt does not start a moment sooner. The pauses run on the client's clock.
    /// </summary>
    [Test]
    public async Task Each_new_attempt_waits_longer_than_the_one_before()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var log = new RecordingLogger<ElevenLabsClient>();
        _handler.Enqueue(HttpStatusCode.TooManyRequests, """{"detail":{"status":"concurrent_limit_exceeded"}}""");
        _handler.Enqueue(HttpStatusCode.RequestTimeout, "{}");
        _handler.Enqueue(HttpStatusCode.OK, Answer);
        var client = CreateClient(new ElevenLabsOptions { ApiKey = "test-key", MaxAttempts = 3, RetryDelaySeconds = 5 }, time, log);

        var request = client.TranscribeAsync(_audioPath, [], CancellationToken.None);

        await PauseAfterAttemptAsync(1, time, log);
        time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.That(_handler.Requests, Has.Count.EqualTo(1), "the second attempt waits the full 5 seconds");
        time.Advance(TimeSpan.FromTicks(1));
        await Wait.UntilAsync(() => _handler.Requests.Count == 2);

        await PauseAfterAttemptAsync(2, time, log);
        time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.That(_handler.Requests, Has.Count.EqualTo(2), "the third attempt waits the full 10 seconds");
        time.Advance(TimeSpan.FromTicks(1));

        var result = await request.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(result.Text, Is.EqualTo("Hello world."));
        Assert.That(_handler.Requests, Has.Count.EqualTo(3));
        Assert.That(log.Problems, Has.Some.EndsWith("trying again in 5 seconds"));
        Assert.That(log.Problems, Has.Some.EndsWith("trying again in 10 seconds"));
    }

    /// <summary>
    /// Waits until the client has reported the failure of <paramref name="attempt"/> and begun its pause. By then the
    /// attempt's own time limit is gone, so the pause is the only timer on the clock.
    /// </summary>
    private static Task PauseAfterAttemptAsync(int attempt, ManualTimeProvider time, RecordingLogger<ElevenLabsClient> log) =>
        Wait.UntilAsync(() => log.Problems.Any(problem => problem.StartsWith($"Attempt {attempt} of ")) && time.ActiveTimers == 1);

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

    /// <summary>
    /// The audio streams from disk: none of it has been read when the request reaches the transport, so a long
    /// class is never held in memory first.
    /// </summary>
    [Test]
    public async Task The_audio_is_streamed_rather_than_read_into_memory_first()
    {
        var log = new RecordingLogger<ElevenLabsClient>();
        IReadOnlyList<string> loggedBeforeSending = [];
        _handler.OnSend = () => loggedBeforeSending = log.Messages;
        _handler.Enqueue(HttpStatusCode.OK, Answer);

        await CreateClient(logger: log).TranscribeAsync(_audioPath, [], CancellationToken.None);

        Assert.That(loggedBeforeSending, Has.Some.StartWith("Uploading"), "the upload had begun");
        Assert.That(loggedBeforeSending, Has.None.StartWith("Uploaded "), "no audio had been read yet");
        Assert.That(loggedBeforeSending, Has.None.StartWith("Upload finished"), "no audio had been read yet");
        Assert.That(log.Messages, Has.Some.StartWith("Upload finished"), "the audio was read as it was sent");
    }

    /// <summary>
    /// The time limit covers the whole answer: a body that stops arriving after the headers ends in a clear
    /// failure once the limit is reached, rather than a wait without end.
    /// </summary>
    [Test]
    public async Task A_body_that_stops_arriving_runs_into_the_time_limit()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _handler.EnqueueStalledBody(HttpStatusCode.OK);
        var client = CreateClient(new ElevenLabsOptions { ApiKey = "test-key", TimeoutMinutes = 5, RetryDelaySeconds = 0 }, time);

        var request = client.TranscribeAsync(_audioPath, [], CancellationToken.None);
        await Wait.UntilAsync(() => _handler.Requests.Count == 1);
        time.Advance(TimeSpan.FromMinutes(5));

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await request.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.That(error!.Message, Does.Contain("did not answer in time"));
        Assert.That(_handler.Requests, Has.Count.EqualTo(1), "not sent again: the same wait would follow");
    }

    /// <summary>
    /// A successful answer in a form the app cannot read is an error that keeps the answer and its status, so the
    /// transcript that was paid for can be saved; it is not sent again, which would be paid for again.
    /// </summary>
    [Test]
    public void An_answer_in_an_unexpected_form_keeps_the_answer_and_its_status()
    {
        const string odd = """{"text":"Hello world.","words":[{"text":"Hello","speaker_id":3}]}""";
        _handler.Enqueue(HttpStatusCode.OK, odd);
        var client = CreateClient();

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(error.ResponseBody, Is.EqualTo(odd));
        Assert.That(error.Message, Does.Not.Contain("not valid JSON"), "the answer is valid JSON, only of another shape");
        Assert.That(_handler.Requests, Has.Count.EqualTo(1));
    }

    /// <summary>An empty item in the word list is skipped instead of failing the whole answer.</summary>
    [Test]
    public async Task An_empty_item_in_the_word_list_is_skipped()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"text":"Hello.","words":[null,{"text":"Hello.","start":0.0,"end":0.5,"type":"word"}]}""");

        var result = await CreateClient().TranscribeAsync(_audioPath, [], CancellationToken.None);

        Assert.That(result.Words, Is.EqualTo(new[] { new TranscriptWord("Hello.", 0.0, 0.5, "word", null) }));
    }

    /// <summary>Running out of credits is named as such, as ElevenLabs reports it with 402, and is not retried: a second try would fail the same way.</summary>
    [Test]
    public void Running_out_of_credits_is_named_and_not_retried()
    {
        _handler.Enqueue(HttpStatusCode.PaymentRequired, """{"detail":{"status":"payment_required","code":"insufficient_credits"}}""");
        var client = CreateClient();

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(error!.Message, Does.StartWith("The ElevenLabs account does not have enough credits"));
        Assert.That(_handler.Requests, Has.Count.EqualTo(1));
    }

    /// <summary>A 429 is reported as a rate or concurrency limit only: ElevenLabs reports used-up credits with 402.</summary>
    [Test]
    public void A_rate_limit_is_not_reported_as_a_used_up_quota()
    {
        _handler.Enqueue(HttpStatusCode.TooManyRequests, """{"detail":{"status":"concurrent_limit_exceeded"}}""");
        var client = CreateClient(new ElevenLabsOptions { ApiKey = "test-key", MaxAttempts = 1 });

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("rate limiting"));
        Assert.That(error.Message, Does.Not.Contain("quota"));
    }

    /// <summary>
    /// When the connection breaks on every try, the log says that a request ElevenLabs refuses before reading all of
    /// it looks just the same, so the key and the credits get checked before yet another upload.
    /// </summary>
    [Test]
    public void A_connection_that_breaks_on_every_try_ends_with_a_hint_about_an_early_refusal()
    {
        var log = new RecordingLogger<ElevenLabsClient>();
        for (var attempt = 0; attempt < 3; attempt++)
            _handler.EnqueueFailure(new HttpRequestException("Error while copying content to a stream.", new IOException("An existing connection was forcibly closed by the remote host.")));
        var client = CreateClient(new ElevenLabsOptions { ApiKey = "test-key", MaxAttempts = 3, RetryDelaySeconds = 0 }, logger: log);

        Assert.ThrowsAsync<HttpRequestException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(log.Problems, Has.Some.Contains("turning the request down"));
    }

    /// <summary>
    /// A connection that could not be made at all, as when the computer is offline, never reached ElevenLabs, so it
    /// gets no hint about the key and the credits; the error itself is still logged.
    /// </summary>
    [TestCase(HttpRequestError.NameResolutionError)]
    [TestCase(HttpRequestError.ConnectionError)]
    [TestCase(HttpRequestError.SecureConnectionError)]
    [TestCase(HttpRequestError.ProxyTunnelError)]
    public void A_connection_that_cannot_be_made_gets_no_hint_about_an_early_refusal(HttpRequestError failure)
    {
        var log = new RecordingLogger<ElevenLabsClient>();
        _handler.EnqueueFailure(new HttpRequestException(failure, "The connection to api.elevenlabs.io:443 could not be made."));
        var client = CreateClient(new ElevenLabsOptions { ApiKey = "test-key", MaxAttempts = 1 }, logger: log);

        Assert.ThrowsAsync<HttpRequestException>(async () => await client.TranscribeAsync(_audioPath, [], CancellationToken.None));

        Assert.That(log.Problems, Has.Some.StartsWith("The ElevenLabs request for upload.wav failed"));
        Assert.That(log.Problems, Has.None.Contains("turning the request down"));
    }
}
