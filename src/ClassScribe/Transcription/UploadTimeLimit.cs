using ClassScribe.Audio;
using ClassScribe.Settings;

namespace ClassScribe.Transcription;

/// <summary>
/// How long one request to ElevenLabs may take, the upload and the wait for the transcript together. A long recording on
/// a slow line needs more time than a short one, so the limit allows 10 minutes, plus the upload at
/// ElevenLabs:SlowestUploadMbps, plus a quarter of the audio's length for ElevenLabs to transcribe it, rounded to the
/// minute, and never less than ElevenLabs:TimeoutMinutes. At the defaults a two-hour class (230.4 MB) gets
/// 10 + 30.7 + 30 = 71 minutes, four hours get 131 and six hours 192, while an hour keeps the 60 of TimeoutMinutes.
/// </summary>
public static class UploadTimeLimit
{
    /// <summary>Bits in a megabit, as upload speeds count them.</summary>
    private const double BitsPerMegabit = 1_000_000;

    /// <summary>The slowest upload speed allowed for, in megabits per second, as SettingsCheck allows it, should a slower one reach here.</summary>
    private const double SlowestMbps = 0.1;

    /// <summary>The time every request is given before its upload and its transcription: for connecting, ElevenLabs' queue and the answer.</summary>
    private static readonly TimeSpan Base = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The time limit for uploading <paramref name="uploadBytes"/> bytes from <paramref name="audioPath"/> and waiting for
    /// the transcript. The audio's length is known when the file is a WAV file ClassScribe wrote, as the upload copy
    /// always is (see <see cref="AudioLength"/>); for any other file, such as an MP3 uploaded as it is, the time its
    /// upload takes stands in for it.
    /// </summary>
    public static TimeSpan For(string audioPath, long uploadBytes, ElevenLabsOptions settings)
    {
        var upload = TimeSpan.FromSeconds(uploadBytes * 8 / (Math.Max(SlowestMbps, settings.SlowestUploadMbps) * BitsPerMegabit));
        var audio = AudioLength(audioPath) ?? upload;
        var minutes = Math.Round((Base + upload + audio / 4).TotalMinutes, MidpointRounding.AwayFromZero);
        return TimeSpan.FromMinutes(Math.Max(settings.Timeout.TotalMinutes, minutes));
    }

    /// <summary>
    /// The length of the audio in a WAV file ClassScribe wrote, or null for any other file. Its header tells the file
    /// apart and gives the rate; the audio is read to the end of the file, as the preparer reads it, whatever the sizes
    /// in the header say after a crash.
    /// </summary>
    public static TimeSpan? AudioLength(string path)
    {
        Span<byte> header = stackalloc byte[WavHeader.Size];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !WavHeader.TryRead(header, out var sampleRate))
            return null;

        // Two bytes to a sample: the recorder writes mono 16-bit audio.
        return TimeSpan.FromSeconds((file.Length - WavHeader.Size) / 2.0 / sampleRate);
    }
}
