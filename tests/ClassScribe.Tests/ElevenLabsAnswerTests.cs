using System.Net;
using System.Text.Json;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// The parser of ElevenLabs' answer, which the client uses for an answer that has just arrived and a rebuild for one
/// saved earlier: what it reads from a full answer, and how it refuses one it cannot read.
/// </summary>
[TestFixture]
public class ElevenLabsAnswerTests
{
    /// <summary>
    /// A full answer in the form ElevenLabs sends it: words, spacing and a sound with their times and speakers, and the
    /// fields this app does not read (log probabilities, characters, the channel, additional formats), which must not
    /// get in the way.
    /// </summary>
    private const string Golden = """
        {
          "language_code": "eng",
          "language_probability": 0.9871,
          "text": "Today we read al-Ghazālī. (laughter)",
          "words": [
            { "text": "Today", "start": 0.119, "end": 0.48, "type": "word", "speaker_id": "speaker_0", "logprob": -0.031,
              "characters": [ { "text": "T", "start": 0.119, "end": 0.2 }, { "text": "oday", "start": 0.2, "end": 0.48 } ] },
            { "text": " ", "start": 0.48, "end": 0.52, "type": "spacing", "speaker_id": "speaker_0", "logprob": 0.0 },
            { "text": "we", "start": 0.52, "end": 0.66, "type": "word", "speaker_id": "speaker_0", "logprob": -0.004 },
            { "text": " ", "start": 0.66, "end": 0.7, "type": "spacing", "speaker_id": "speaker_0", "logprob": 0.0 },
            { "text": "read", "start": 0.7, "end": 0.98, "type": "word", "speaker_id": "speaker_0", "logprob": -0.012 },
            { "text": " ", "start": 0.98, "end": 1.02, "type": "spacing", "speaker_id": "speaker_0", "logprob": 0.0 },
            { "text": "al-Ghazālī.", "start": 1.02, "end": 1.74, "type": "word", "speaker_id": "speaker_0", "logprob": -0.21 },
            { "text": " ", "start": 1.74, "end": 3.1, "type": "spacing", "speaker_id": "speaker_0", "logprob": 0.0 },
            { "text": "(laughter)", "start": 3.1, "end": 3.9, "type": "audio_event", "speaker_id": "speaker_1", "logprob": 0.0 }
          ],
          "channel_index": null,
          "additional_formats": null,
          "transcription_id": "kQ2vX8b1nWm4Zr7Tp0Ls",
          "audio_duration_secs": 4.21
        }
        """;

    /// <summary>The words of <see cref="Golden"/>, as the app reads them.</summary>
    private static readonly TranscriptWord[] GoldenWords =
    [
        new("Today", 0.119, 0.48, "word", "speaker_0"),
        new(" ", 0.48, 0.52, "spacing", "speaker_0"),
        new("we", 0.52, 0.66, "word", "speaker_0"),
        new(" ", 0.66, 0.7, "spacing", "speaker_0"),
        new("read", 0.7, 0.98, "word", "speaker_0"),
        new(" ", 0.98, 1.02, "spacing", "speaker_0"),
        new("al-Ghazālī.", 1.02, 1.74, "word", "speaker_0"),
        new(" ", 1.74, 3.1, "spacing", "speaker_0"),
        new("(laughter)", 3.1, 3.9, "audio_event", "speaker_1")
    ];

    /// <summary>Every field the app uses is read from a full answer, and the answer itself is kept exactly as it was, spacing and all.</summary>
    [Test]
    public void A_full_answer_is_read_into_every_field_the_app_uses()
    {
        var result = ElevenLabsAnswer.Parse(Golden, HttpStatusCode.OK);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Text, Is.EqualTo("Today we read al-Ghazālī. (laughter)"));
            Assert.That(result.LanguageCode, Is.EqualTo("eng"));
            Assert.That(result.LanguageProbability, Is.EqualTo(0.9871));
            Assert.That(result.AudioDurationSecs, Is.EqualTo(4.21));
            Assert.That(result.TranscriptionId, Is.EqualTo("kQ2vX8b1nWm4Zr7Tp0Ls"));
            Assert.That(result.Words, Is.EqualTo(GoldenWords));
            Assert.That(result.RawJson, Is.EqualTo(Golden));
        }
    }

    /// <summary>
    /// The client reads the answer it receives with this same parser: for the same answer, what the client gives and
    /// what the parser gives agree in every field, so a rebuild from a saved answer starts from what the upload gave.
    /// </summary>
    [Test]
    public async Task The_client_gives_what_the_parser_gives()
    {
        using var folder = new TempFolder();
        var audio = folder.File("upload.wav");
        File.WriteAllBytes(audio, new byte[256]);
        var handler = new StubHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, Golden);
        var api = RestService.ForGenerated<IElevenLabsApi>(new HttpClient(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") }, ElevenLabsJson.Default, ElevenLabsApiSettings.Create());
        var client = new ElevenLabsClient(api, TestOptions.Create(new ElevenLabsOptions { ApiKey = "dummy-key-for-a-local-test" }), new AudioFileFacts(), new TranscriptionProgress(), TimeProvider.System,
            NullLogger<ElevenLabsClient>.Instance);

        var received = await client.TranscribeAsync(audio, [], CancellationToken.None);
        var parsed = ElevenLabsAnswer.Parse(Golden, HttpStatusCode.OK);

        Assert.That(received with { Words = [] }, Is.EqualTo(parsed with { Words = [] }));
        Assert.That(received.Words, Is.EqualTo(parsed.Words));
    }

    /// <summary>
    /// An answer that is not JSON at all is refused with the status it came with and the answer itself, so a caller can
    /// keep a transcript that was paid for; the JSON error is the inner exception.
    /// </summary>
    [Test]
    public void An_answer_that_is_not_json_is_refused_with_its_status_and_its_text()
    {
        const string answer = "<html>Service busy</html>";

        var error = Assert.Throws<TranscriptionException>(() => ElevenLabsAnswer.Parse(answer, HttpStatusCode.OK));

        Assert.That(error!.Message, Is.EqualTo("ElevenLabs answered in a form this app cannot read."));
        Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(error.ResponseBody, Is.EqualTo(answer));
        Assert.That(error.InnerException, Is.InstanceOf<JsonException>());
    }

    /// <summary>An answer in JSON of another shape, here a speaker given as a number, is refused the same way, and is not taken for one without text.</summary>
    [Test]
    public void An_answer_of_another_shape_is_refused_with_its_status_and_its_text()
    {
        const string answer = """{"text":"Hello world.","words":[{"text":"Hello","speaker_id":3}]}""";

        var error = Assert.Throws<TranscriptionException>(() => ElevenLabsAnswer.Parse(answer, HttpStatusCode.OK));

        Assert.That(error!.Message, Is.EqualTo("ElevenLabs answered in a form this app cannot read."));
        Assert.That(error.ResponseBody, Is.EqualTo(answer));
    }

    /// <summary>An answer without a transcript is refused, with the answer kept for whoever looks into it.</summary>
    [Test]
    public void An_answer_without_text_is_refused_with_its_text()
    {
        const string answer = """{"message":"queued"}""";

        var error = Assert.Throws<TranscriptionException>(() => ElevenLabsAnswer.Parse(answer, HttpStatusCode.OK));

        Assert.That(error!.Message, Is.EqualTo("ElevenLabs answered without a transcript."));
        Assert.That(error.ResponseBody, Is.EqualTo(answer));
    }

    /// <summary>The status kept with a refused answer is the one the caller gives, whatever it is.</summary>
    [Test]
    public void The_status_kept_with_a_refused_answer_is_the_one_given()
    {
        var error = Assert.Throws<TranscriptionException>(() => ElevenLabsAnswer.Parse("{}", HttpStatusCode.Created));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    /// <summary>An empty item in the word list is skipped instead of failing the whole answer.</summary>
    [Test]
    public void An_empty_item_in_the_word_list_is_skipped()
    {
        var result = ElevenLabsAnswer.Parse("""{"text":"Hello.","words":[null,{"text":"Hello.","start":0.0,"end":0.5,"type":"word"}]}""", HttpStatusCode.OK);

        Assert.That(result.Words, Is.EqualTo(new[] { new TranscriptWord("Hello.", 0.0, 0.5, "word", null) }));
    }

    /// <summary>An answer without a word list gives a transcript without words, which the formatter lays out as plain text.</summary>
    [Test]
    public void An_answer_without_words_gives_a_transcript_without_words()
    {
        var result = ElevenLabsAnswer.Parse("""{"text":"Hello."}""", HttpStatusCode.OK);

        Assert.That(result.Text, Is.EqualTo("Hello."));
        Assert.That(result.Words, Is.Empty);
    }
}
