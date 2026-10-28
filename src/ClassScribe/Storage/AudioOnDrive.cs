namespace ClassScribe.Storage;

/// <summary>
/// A recording's audio that is on Google Drive, checked against Drive's MD5, and no longer on this laptop: freed to make
/// room (ClassScribe drive free-space), or deleted by hand after it went up. Its transcripts, answers, marks and log stay on
/// the laptop, so it can still be read and rebuilt; transcribing it again needs the audio back.
/// </summary>
/// <param name="Length">The length of the recording, its longest track, as measured when its audio went up.</param>
/// <param name="HasMicrophone">True when the microphone's track was recorded with the class.</param>
/// <param name="Location">Where it is on Drive, as Gulzar finds it there: My Drive &gt; ClassScribe &gt; Mantiq &gt; 2026-10-17.</param>
/// <param name="Link">Its folder on Drive in a browser; null when it is not known.</param>
/// <param name="AllOfIt">True when none of the recording's audio is left on this laptop; false when only part of it is gone.</param>
public sealed record AudioOnDrive(TimeSpan Length, bool HasMicrophone, string Location, string? Link, bool AllOfIt);
