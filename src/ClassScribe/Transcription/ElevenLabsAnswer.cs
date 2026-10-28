using System.Net;
using System.Text;
using System.Text.Json;

namespace ClassScribe.Transcription;

/// <summary>
/// Reads ElevenLabs' speech-to-text answer: the one the client has just received, and one saved beside a recording
/// earlier, which a rebuild lays out again without sending anything. Both go through this one reader, so a rebuilt
/// transcript starts from just what the upload gave. It is the adapter of <see cref="ITranscriptAnswers"/>, the saved
/// answers read and kept for the use cases.
/// </summary>
public sealed class ElevenLabsAnswer : ITranscriptAnswers
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads the fields this app uses out of the service's JSON answer, and keeps the JSON exactly as it is. An answer
    /// that cannot be read keeps <paramref name="status"/> and the JSON in the exception, so that the caller can keep a
    /// transcript that has been paid for.
    /// </summary>
    /// <param name="json">The answer, exactly as ElevenLabs sent it.</param>
    /// <param name="status">The HTTP status it came with; a saved answer came with a success status.</param>
    /// <exception cref="TranscriptionException">The answer is not JSON of the form ElevenLabs sends, or holds no transcript.</exception>
    public static TranscriptionResult Parse(string json, HttpStatusCode status)
    {
        SpeechToTextResponse? response;
        try
        {
            response = JsonSerializer.Deserialize(json, ElevenLabsJson.Default.SpeechToTextResponse);
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException("ElevenLabs answered in a form this app cannot read.", status, json, ex);
        }

        if (response?.Text is null)
            throw new TranscriptionException("ElevenLabs answered without a transcript.", status, json);

        // An empty item in the word list is skipped rather than allowed to fail the whole answer.
        var words = (response.Words ?? [])
            .OfType<SpeechToTextWord>()
            .Select(w => new TranscriptWord(w.Text ?? "", w.Start, w.End, w.Type, w.SpeakerId))
            .ToList();

        return new TranscriptionResult(response.Text, response.LanguageCode, response.LanguageProbability, response.AudioDurationSecs, response.TranscriptionId, words, json);
    }

    /// <inheritdoc />
    public async Task<TranscriptionResult> ReadAsync(string path, CancellationToken cancellationToken) =>
        Parse(await File.ReadAllTextAsync(path, cancellationToken), HttpStatusCode.OK);

    /// <inheritdoc />
    public void WriteNew(string path, string json)
    {
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write), Utf8NoBom);
        writer.Write(json);
    }
}
