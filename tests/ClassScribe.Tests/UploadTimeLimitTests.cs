using System.Buffers.Binary;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// The time a request to ElevenLabs may take: at least ElevenLabs:TimeoutMinutes, and more for a long recording on a
/// slow line, worked out from the upload's size and the length of its audio.
/// </summary>
[TestFixture]
public class UploadTimeLimitTests
{
    /// <summary>Bytes in an hour of the upload copy: 16,000 samples a second, two bytes each.</summary>
    private const long BytesPerHour = 16000L * 2 * 3600;

    private TempFolder _folder = null!;

    /// <summary>Creates a temporary folder.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Writes <paramref name="length"/> of audio with the recorder's own writer, at ten samples a second, so that hours of
    /// it take less than a megabyte. Its size then says nothing about the upload, which a test gives on its own.
    /// </summary>
    private static void WriteAudio(string path, TimeSpan length)
    {
        using var writer = new WavSegmentWriter(path, 10);
        writer.Write(new byte[(int)Math.Round(length.TotalSeconds * 10) * 2]);
    }

    /// <summary>
    /// The limits the design names: an hour of class keeps the 60 minutes of ElevenLabs:TimeoutMinutes, two hours
    /// (230.4 MB) get 10 + 30.7 + 30 = 71 minutes, four hours 131 and six hours 192, at the default 1 megabit per second.
    /// </summary>
    [TestCase(1, 60)]
    [TestCase(2, 71)]
    [TestCase(4, 131)]
    [TestCase(6, 192)]
    public void The_limit_grows_with_the_length_of_the_class(int hours, int minutes)
    {
        var upload = _folder.File("Kalam.upload.wav");
        WriteAudio(upload, TimeSpan.FromHours(hours));

        var limit = UploadTimeLimit.For(upload, WavSegmentWriter.HeaderSize + hours * BytesPerHour, new ElevenLabsOptions());

        Assert.That(limit, Is.EqualTo(TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>The limit is never shorter than ElevenLabs:TimeoutMinutes, which is the least time any request is given.</summary>
    [TestCase(70, 71)]
    [TestCase(72, 72)]
    [TestCase(240, 240)]
    public void The_limit_is_never_shorter_than_TimeoutMinutes(int timeoutMinutes, int minutes)
    {
        var upload = _folder.File("Kalam.upload.wav");
        WriteAudio(upload, TimeSpan.FromHours(2));

        var limit = UploadTimeLimit.For(upload, 2 * BytesPerHour, new ElevenLabsOptions { TimeoutMinutes = timeoutMinutes });

        Assert.That(limit, Is.EqualTo(TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>
    /// The slower the line ElevenLabs:SlowestUploadMbps allows for, the longer the upload of two hours may take: 61.4
    /// minutes at half a megabit per second, 3.1 at ten. TimeoutMinutes is set low here, so the sum shows on its own.
    /// </summary>
    [TestCase(0.5, 101)]
    [TestCase(1, 71)]
    [TestCase(10, 43)]
    public void A_slower_line_is_allowed_a_longer_upload(double megabitsPerSecond, int minutes)
    {
        var upload = _folder.File("Kalam.upload.wav");
        WriteAudio(upload, TimeSpan.FromHours(2));

        var limit = UploadTimeLimit.For(upload, 2 * BytesPerHour, new ElevenLabsOptions { TimeoutMinutes = 1, SlowestUploadMbps = megabitsPerSecond });

        Assert.That(limit, Is.EqualTo(TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>
    /// For a file uploaded as it is, such as an MP3, the length of its audio is not known, so the time its upload takes
    /// stands in for it: 691.2 MB take 92.2 minutes at 1 megabit per second, and a quarter of that is added, with the
    /// 10 minutes, giving 125.
    /// </summary>
    [Test]
    public void For_any_other_file_the_upload_time_stands_in_for_the_length_of_its_audio()
    {
        var audio = _folder.File("lecture.mp3");
        File.WriteAllBytes(audio, [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0]);

        var limit = UploadTimeLimit.For(audio, 6 * BytesPerHour, new ElevenLabsOptions());

        Assert.That(limit, Is.EqualTo(TimeSpan.FromMinutes(125)));
    }

    /// <summary>A WAV file another program wrote counts as any other file, whatever its header says it holds.</summary>
    [Test]
    public void A_wav_file_another_program_wrote_counts_as_any_other_file()
    {
        var audio = _folder.File("lecture.wav");
        ForeignWav.Write(audio, new WaveFormat(16000, 16, 1), TestAudio.Shorts(1000, -1000));
        ForeignWav.LeaveUnfinished(audio, dataBytes: (uint)(6 * BytesPerHour));

        var limit = UploadTimeLimit.For(audio, 6 * BytesPerHour, new ElevenLabsOptions());

        Assert.That(limit, Is.EqualTo(TimeSpan.FromMinutes(125)));
    }

    /// <summary>The length of a WAV file the recorder wrote is the audio written to it, at whatever rate it was recorded.</summary>
    [TestCase(16000)]
    [TestCase(48000)]
    public void The_length_of_a_wav_file_ClassScribe_wrote_is_the_audio_in_it(int sampleRate)
    {
        var segment = _folder.File("Kalam.wav");
        TestAudio.WriteWav(segment, sampleRate, TestAudio.Tone(sampleRate, seconds: 0.5, frequency: 440, amplitude: 0.5));

        Assert.That(UploadTimeLimit.AudioLength(segment), Is.EqualTo(TimeSpan.FromSeconds(0.5)));
    }

    /// <summary>
    /// The length runs to the end of the file, as the preparer reads it, even when the header is behind, as a recorder
    /// that stopped without finishing its file leaves it.
    /// </summary>
    [Test]
    public void The_length_runs_to_the_end_of_the_file_whatever_its_header_says()
    {
        var segment = _folder.File("Kalam.wav");
        TestAudio.WriteWav(segment, 16000, TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5));
        var bytes = File.ReadAllBytes(segment);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 36);   // the sizes as the file's first moment left them
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 0);
        File.WriteAllBytes(segment, bytes);

        Assert.That(UploadTimeLimit.AudioLength(segment), Is.EqualTo(TimeSpan.FromSeconds(0.5)));
    }

    /// <summary>A file too short to hold a header, a WAV file another program wrote and a file of another kind have no length known from a header.</summary>
    [Test]
    public void Only_a_wav_file_ClassScribe_wrote_has_a_length_known_from_its_header()
    {
        var tiny = _folder.File("tiny.wav");
        File.WriteAllBytes(tiny, [1, 2, 3]);
        var foreign = _folder.File("foreign.wav");
        ForeignWav.Write(foreign, new WaveFormat(16000, 16, 1), TestAudio.Shorts(1000, -1000));
        var mp3 = _folder.File("lecture.mp3");
        File.WriteAllBytes(mp3, new byte[100]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(UploadTimeLimit.AudioLength(tiny), Is.Null);
            Assert.That(UploadTimeLimit.AudioLength(foreign), Is.Null);
            Assert.That(UploadTimeLimit.AudioLength(mp3), Is.Null);
        }
    }
}
