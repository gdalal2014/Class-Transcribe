namespace ClassScribe.YouTube;

/// <summary>A downloaded video's audio is not as long as the video: the download is incomplete, and nothing of it became a recording.</summary>
public sealed class IncompleteAudioException(string message) : Exception(message);
