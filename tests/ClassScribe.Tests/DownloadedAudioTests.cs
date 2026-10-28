using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// From the WAV FFmpeg made to ClassScribe's own (design 3.7, with Gulzar's answer 5): any WAV, stereo, float or with extra
/// chunks, becomes ClassScribe's mono 16-bit WAV at the source's own rate, which every reader measures, split at
/// Recording:MaxSegmentMegabytes; written as .partial files that are never listed, renamed only once whole and of the
/// video's length; and what a crash left half done is cleaned up by the next batch. Audio is written at low rates, so the
/// files stay small.
/// </summary>
[TestFixture]
public class DownloadedAudioTests
{
    private TestRuntime _runtime = null!;
    private TempFolder _recordings = null!;
    private RecordingFiles _target = null!;
    private RecordingLogger<DownloadedAudio> _log = null!;
    private TestOptions<RecordingOptions> _options = null!;

    /// <summary>A test's own ClassScribe folder, a recordings folder with a video's recording to write, and a log.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _recordings = new TempFolder();
        _target = RecordingFiles.ForNew(Path.Combine(_recordings.Path, "Risala", "2026-10-18"), "Risala", new DateTimeOffset(2026, 10, 18, 14, 2, 11, TimeSpan.Zero));
        Directory.CreateDirectory(_target.Folder);
        _log = new RecordingLogger<DownloadedAudio>();
        _options = TestOptions.Create(new RecordingOptions());
    }

    /// <summary>Deletes the folders; a file left open fails that.</summary>
    [TearDown]
    public void TearDown()
    {
        _recordings.Dispose();
        _runtime.Dispose();
    }

    /// <summary>The conversion over the test's folders and settings.</summary>
    private DownloadedAudio Audio() => new(new YouTubeFolders(_runtime.Folder), _options, _runtime.Time, _log);

    /// <summary>Writes a WAV as FFmpeg might, with NAudio's writer: <paramref name="format"/>, <paramref name="seconds"/> long, a steady tone, with a LIST chunk after the audio when asked.</summary>
    private string Staged(WaveFormat format, double seconds, bool listChunk = false)
    {
        var path = _runtime.Temp.File("abcdefghijk.wav");
        using (var writer = new WaveFileWriter(path, format))
        {
            var frames = (int)(format.SampleRate * seconds);
            var samples = new float[frames * format.Channels];
            for (var i = 0; i < samples.Length; i++) samples[i] = 0.25f * MathF.Sin(i / 10f);
            writer.WriteSamples(samples, 0, samples.Length);
        }

        if (listChunk)
        {
            using var file = new FileStream(path, FileMode.Append);
            file.Write("LIST"u8);
            file.Write(BitConverter.GetBytes(14));
            file.Write("INFOISFT"u8);
            file.Write(BitConverter.GetBytes(2));
            file.Write("x\0"u8);
        }

        return path;
    }

    /// <summary>
    /// A stereo WAV with a LIST chunk after its audio becomes ClassScribe's own mono 16-bit WAV at the same rate, YouTube's
    /// own, which ClassScribe's readers measure to the length of the video.
    /// </summary>
    [Test]
    public void A_stereo_wav_with_extra_chunks_becomes_classscribes_own_at_its_rate()
    {
        var staged = Staged(new WaveFormat(8000, 16, 2), seconds: 3, listChunk: true);

        var length = Audio().Convert(staged, _target, TimeSpan.FromSeconds(3), "The creed, part 1", CancellationToken.None);

        Assert.That(length, Is.EqualTo(TimeSpan.FromSeconds(3)));
        Assert.That(_target.FindSegments(), Is.EqualTo([_target.Segment(1)]));
        Assert.That(ClassFolders.MeasureSegment(_target.Segment(1)), Is.EqualTo(TimeSpan.FromSeconds(3)));
        var header = File.ReadAllBytes(_target.Segment(1)).AsSpan(0, WavHeader.Size);
        Assert.That(WavHeader.TryRead(header, out var rate) ? rate : 0, Is.EqualTo(8000), "the source's own rate is kept");
        Assert.That(File.Exists(staged), Is.True, "the staged file is the batch's to delete");
    }

    /// <summary>A float WAV, as FFmpeg writes some, becomes 16-bit mono with the same sound.</summary>
    [Test]
    public void A_float_wav_becomes_sixteen_bit_mono()
    {
        var staged = Staged(WaveFormat.CreateIeeeFloatWaveFormat(8000, 1), seconds: 2);

        Audio().Convert(staged, _target, TimeSpan.FromSeconds(2), "The creed, part 1", CancellationToken.None);

        using var reader = new WaveFileReader(_target.Segment(1));
        Assert.That((reader.WaveFormat.Encoding, reader.WaveFormat.BitsPerSample, reader.WaveFormat.Channels), Is.EqualTo((WaveFormatEncoding.Pcm, 16, 1)));
        var samples = new byte[400];
        reader.ReadExactly(samples);
        Assert.That(samples.Any(value => value != 0), Is.True, "the tone came through");
    }

    /// <summary>Long audio is split into parts at Recording:MaxSegmentMegabytes, as the recorder splits, and together they hold the whole video.</summary>
    [Test]
    public void Long_audio_is_split_into_parts()
    {
        _options.Set(new RecordingOptions { MaxSegmentMegabytes = 1 });
        var staged = Staged(new WaveFormat(8000, 16, 1), seconds: 100);

        var length = Audio().Convert(staged, _target, TimeSpan.FromSeconds(100), "The creed, part 1", CancellationToken.None);

        Assert.That(_target.FindSegments(), Is.EqualTo([_target.Segment(1), _target.Segment(2)]));
        Assert.That(_target.FindSegments().Sum(segment => ClassFolders.MeasureSegment(segment).TotalSeconds), Is.EqualTo(100).Within(0.001));
        Assert.That(length, Is.EqualTo(TimeSpan.FromSeconds(100)));
    }

    /// <summary>
    /// Audio more than 2 seconds shorter than the video is an incomplete download: nothing of it becomes the recording's, no
    /// .partial is left, and the staged file is kept for a look.
    /// </summary>
    [Test]
    public void Audio_shorter_than_the_video_is_refused_and_nothing_is_left()
    {
        var staged = Staged(new WaveFormat(8000, 16, 1), seconds: 3);

        var refused = Assert.Throws<IncompleteAudioException>(() => Audio().Convert(staged, _target, TimeSpan.FromSeconds(10), "The creed, part 1", CancellationToken.None));

        Assert.That(refused!.Message, Does.Contain("00:00:03").And.Contain("00:00:10"));
        Assert.That(Directory.GetFiles(_target.Folder), Is.Empty);
        Assert.That(File.Exists(staged), Is.True);
        Assert.That(File.Exists(new YouTubeFolders(_runtime.Folder).ConvertingNote), Is.False);
    }

    /// <summary>Within 2 seconds of the video's length is whole: yt-dlp's length is rounded, and the audio's ends differ a little.</summary>
    [Test]
    public void Within_two_seconds_of_the_videos_length_is_whole()
    {
        var staged = Staged(new WaveFormat(8000, 16, 1), seconds: 3);

        Assert.DoesNotThrow(() => Audio().Convert(staged, _target, TimeSpan.FromSeconds(4.9), "The creed, part 1", CancellationToken.None));
    }

    /// <summary>An unfinished file is never a recording's: a .partial is not taken for any recording's file.</summary>
    [Test]
    public void A_partial_file_is_never_a_recordings() =>
        Assert.That(RecordingFiles.TryParse(_target.Segment(1) + DownloadedAudio.PartialEnding), Is.Null);

    /// <summary>
    /// A conversion a crash cut short leaves its note and its files: the next batch deletes the files the note names, the
    /// unfinished parts and any part already renamed, says so, and removes the note; the source file and the log stay.
    /// </summary>
    [Test]
    public void What_a_crash_left_half_done_is_cleaned_up()
    {
        var folders = new YouTubeFolders(_runtime.Folder);
        Directory.CreateDirectory(folders.Root);
        File.WriteAllText(folders.ConvertingNote, $$"""{"videoId":"abcdefghijk","title":"The creed, part 1","folder":{{System.Text.Json.JsonSerializer.Serialize(_target.Folder)}},"baseName":"{{_target.BaseName}}"}""");
        File.WriteAllBytes(_target.Segment(1), [1]);
        File.WriteAllBytes(_target.Segment(2) + DownloadedAudio.PartialEnding, [1]);
        File.WriteAllText(_target.Source, "{}");
        File.WriteAllText(_target.Log, "log");

        Audio().CleanUp();

        Assert.That(Directory.GetFiles(_target.Folder).Select(Path.GetFileName), Is.EquivalentTo([Path.GetFileName(_target.Source), Path.GetFileName(_target.Log)]));
        Assert.That(File.Exists(folders.ConvertingNote), Is.False);
        Assert.That(_log.Messages.Single(), Does.StartWith("Deleted the unfinished conversion of \"The creed, part 1\" left by an earlier batch:"));
    }

    /// <summary>A staging folder untouched for more than 7 days is deleted; a newer one is kept, for its download to go on from where it stopped.</summary>
    [Test]
    public void Staging_folders_older_than_seven_days_are_pruned()
    {
        var folders = new YouTubeFolders(_runtime.Folder);
        var old = folders.StagingOf("abcdefghijk");
        var recent = folders.StagingOf("bcdefghijkl");
        foreach (var (folder, age) in new[] { (old, TimeSpan.FromDays(8)), (recent, TimeSpan.FromDays(2)) })
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "x.part");
            File.WriteAllBytes(file, [1]);
            File.SetLastWriteTimeUtc(file, (_runtime.Time.GetUtcNow() - age).UtcDateTime);
            Directory.SetLastWriteTimeUtc(folder, (_runtime.Time.GetUtcNow() - age).UtcDateTime);
        }

        Audio().CleanUp();

        Assert.That((Directory.Exists(old), Directory.Exists(recent)), Is.EqualTo((false, true)));
    }
}
