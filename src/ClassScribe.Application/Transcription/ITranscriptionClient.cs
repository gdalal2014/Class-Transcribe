using System.Net;

namespace ClassScribe.Transcription;

/// <summary>Sends audio to a speech-to-text service.</summary>
public interface ITranscriptionClient
{
    /// <summary>Uploads the file and returns its transcript.</summary>
    /// <param name="audioPath">Audio or video file to transcribe.</param>
    /// <param name="keyterms">Glossary terms the service should spell as given.</param>
    /// <param name="cancellationToken">Cancels the upload or the wait for the transcript.</param>
    /// <exception cref="TranscriptionException">The service refused the request, did not answer in time, or answered with something unusable.</exception>
    Task<TranscriptionResult> TranscribeAsync(string audioPath, IReadOnlyList<string> keyterms, CancellationToken cancellationToken);
}

/// <summary>The transcription service refused the request or answered with something unusable.</summary>
public sealed class TranscriptionException(
    string message,
    HttpStatusCode? statusCode = null,
    string? responseBody = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>
    /// HTTP status the service answered with, when there was an answer. A success status means the answer
    /// arrived, and was charged for, but could not be read; <see cref="ResponseBody"/> then holds it.
    /// </summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>Body of the service's answer, when there was one.</summary>
    public string? ResponseBody { get; } = responseBody;
}
