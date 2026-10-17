using ClassScribe.Audio;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// Mixing the class and the microphone into the one track that is uploaded: both tracks summed from their common start,
/// the shorter padded, and a microphone that stayed silent left out, so that the upload is then exactly today's.
/// </summary>
[TestFixture]
public class UploadMixTests
{
    /// <summary>The line the log carries when the microphone's track is left out of the upload.</summary>
    private const string StayedSilent = "Your microphone stayed silent; only the class is sent.";

    private TempFolder _folder = null!;
    private RecordingLogger<AudioPreparer> _log = null!;
    private AudioPreparer _preparer = null!;

    /// <summary>Creates a temporary folder, a logger that keeps every line, and the preparer under test.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _log = new RecordingLogger<AudioPreparer>();
        _preparer = new AudioPreparer(_log);
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The RMS level of 16-bit samples, where 1 is full scale.</summary>
    private static double Rms(short[] samples) => Math.Sqrt(samples.Average(sample => sample / 32767.0 * (sample / 32767.0)));

    /// <summary>Writes a mono 16-bit file of the recorder's own under <paramref name="name"/> and returns its path.</summary>
    private string Wav(string name, int sampleRate, short[] samples)
    {
        var path = _folder.File(name);
        TestAudio.WriteWav(path, sampleRate, samples);
        return path;
    }

    /// <summary>A run of <paramref name="count"/> samples, every one at <paramref name="level"/>.</summary>
    private static short[] Steady(short level, int count) => [.. Enumerable.Repeat(level, count)];

    /// <summary>Mixes <paramref name="main"/> and <paramref name="microphone"/> into a new upload file and returns its samples.</summary>
    private short[] Mixed(IReadOnlyList<string> main, IReadOnlyList<string> microphone)
    {
        var output = _folder.File($"upload-{Guid.NewGuid():N}.wav");
        _preparer.Prepare(new UploadTracks(main, microphone), output, CancellationToken.None);
        return TestAudio.ReadWav(output).Samples;
    }

    /// <summary>
    /// A class at 48 kHz and a microphone at 44.1 kHz, the microphone half as long, come out as one 16 kHz mono 16-bit
    /// file as long as the class: both are heard while both last, and the class alone after the microphone's track ends.
    /// </summary>
    [Test]
    public void Two_tracks_of_different_rates_and_lengths_are_mixed_and_the_shorter_is_padded()
    {
        var main = Wav("class.wav", 48000, TestAudio.Tone(48000, seconds: 2, frequency: 1000, amplitude: 0.3));
        var microphone = Wav("class.mic.wav", 44100, TestAudio.Tone(44100, seconds: 1, frequency: 2000, amplitude: 0.3));
        var output = _folder.File("upload.wav");

        _preparer.Prepare(new UploadTracks([main], [microphone]), output, CancellationToken.None);

        var wav = TestAudio.ReadWav(output);
        Assert.That(wav.SampleRate, Is.EqualTo(16000));
        Assert.That(wav.Channels, Is.EqualTo(1));
        Assert.That(wav.BitsPerSample, Is.EqualTo(16));
        Assert.That(wav.Samples.Length / 16000.0, Is.EqualTo(2.0).Within(0.05), "as long as the longer track");
        // Two tones of different pitch add in power: together they measure 0.3, one alone 0.3 over the square root of 2.
        Assert.That(Rms(wav.Samples[1600..14400]), Is.EqualTo(0.3).Within(0.02), "both tracks in the first second");
        Assert.That(Rms(wav.Samples[17600..30400]), Is.EqualTo(0.3 / Math.Sqrt(2)).Within(0.02), "the class alone in the second");
    }

    /// <summary>At 16 kHz nothing is resampled, so the mix is the sum of the two tracks sample for sample, the shorter padded with zeros.</summary>
    [Test]
    public void The_tracks_are_summed_sample_for_sample_from_their_common_start()
    {
        var main = Wav("class.wav", 16000, [1000, 1000, 1000, 1000]);
        var microphone = Wav("class.mic.wav", 16000, [500, 500]);

        Assert.That(Mixed([main], [microphone]), Is.EqualTo(new short[] { 1500, 1500, 1000, 1000 }));
    }

    /// <summary>A microphone track longer than the class is heard to its end, with the class padded.</summary>
    [Test]
    public void A_microphone_track_longer_than_the_class_is_heard_to_its_end()
    {
        var main = Wav("class.wav", 16000, [1000]);
        var microphone = Wav("class.mic.wav", 16000, [500, 500, 500]);

        Assert.That(Mixed([main], [microphone]), Is.EqualTo(new short[] { 1500, 500, 500 }));
    }

    /// <summary>
    /// The tracks are mixed a second at a time, and a microphone track that ends part-way through a second is padded
    /// with silence for the rest of it, not with what its last second left in the buffer: two seconds of class against
    /// a second and a half of microphone give the sum for the first second and a half and the class alone after that.
    /// </summary>
    [Test]
    public void A_microphone_track_that_ends_part_way_through_a_second_is_padded_with_silence()
    {
        var main = Wav("class.wav", 16000, Steady(1000, 32000));
        var microphone = Wav("class.mic.wav", 16000, Steady(500, 24000));

        var mixed = Mixed([main], [microphone]);

        Assert.That(mixed.Length, Is.EqualTo(32000));
        Assert.That(mixed[..24000], Is.All.EqualTo(1500), "both tracks while both last");
        Assert.That(mixed[24000..], Is.All.EqualTo(1000), "the class alone, not the microphone's last second again");
    }

    /// <summary>The same the other way round: a class that ends part-way through a second leaves the microphone alone, not the class's last second again.</summary>
    [Test]
    public void A_class_that_ends_part_way_through_a_second_is_padded_with_silence()
    {
        var main = Wav("class.wav", 16000, Steady(1000, 24000));
        var microphone = Wav("class.mic.wav", 16000, Steady(500, 32000));

        var mixed = Mixed([main], [microphone]);

        Assert.That(mixed.Length, Is.EqualTo(32000));
        Assert.That(mixed[..24000], Is.All.EqualTo(1500), "both tracks while both last");
        Assert.That(mixed[24000..], Is.All.EqualTo(500), "the microphone alone, not the class's last second again");
    }

    /// <summary>Where the two tracks together go past full scale, the sum is clamped, never wrapped round.</summary>
    [Test]
    public void The_sum_is_clamped_at_full_scale()
    {
        var main = Wav("class.wav", 16000, [26214, -26214, 1000]);
        var microphone = Wav("class.mic.wav", 16000, [26214, -26214, 2000]);

        Assert.That(Mixed([main], [microphone]), Is.EqualTo(new short[] { 32767, -32767, 3000 }));
    }

    /// <summary>
    /// A microphone that never rose above the sound threshold (100 of 32,767) is left out: the upload is byte for byte
    /// what the class alone gives, and the log says so.
    /// </summary>
    [Test]
    public void A_microphone_that_never_rose_above_the_threshold_is_left_out_and_the_upload_is_todays()
    {
        var main = Wav("class.wav", 48000, TestAudio.Tone(48000, seconds: 1, frequency: 1000, amplitude: 0.5));
        var quiet = Enumerable.Range(0, 48000).Select(i => (short)(i % 3 == 0 ? 100 : i % 3 == 1 ? -100 : 37)).ToArray();
        var microphone = Wav("class.mic.wav", 48000, quiet);
        var alone = _folder.File("alone.wav");
        var mixed = _folder.File("mixed.wav");

        _preparer.Prepare([main], alone, CancellationToken.None);
        _preparer.Prepare(new UploadTracks([main], [microphone]), mixed, CancellationToken.None);

        Assert.That(File.ReadAllBytes(mixed), Is.EqualTo(File.ReadAllBytes(alone)));
        Assert.That(_log.Entries, Has.Some.EqualTo((LogLevel.Information, StayedSilent)));
    }

    /// <summary>One sample just above the threshold is enough for the microphone to be mixed in, and no silence is reported.</summary>
    [Test]
    public void A_microphone_heard_just_above_the_threshold_is_mixed_in()
    {
        var main = Wav("class.wav", 16000, [1000, 1000, 1000, 1000]);
        var microphone = Wav("class.mic.wav", 16000, [0, 0, 101]);

        Assert.That(Mixed([main], [microphone]), Is.EqualTo(new short[] { 1000, 1000, 1101, 1000 }));
        Assert.That(_log.Messages, Has.None.EqualTo(StayedSilent));
    }

    /// <summary>With no microphone files at all, as in person or with the class alone, the main track is prepared as today and nothing is said about a microphone.</summary>
    [Test]
    public void Without_microphone_files_the_main_track_is_prepared_as_today()
    {
        var main = Wav("class.wav", 48000, TestAudio.Tone(48000, seconds: 1, frequency: 1000, amplitude: 0.5));
        var alone = _folder.File("alone.wav");
        var tracks = _folder.File("tracks.wav");

        _preparer.Prepare([main], alone, CancellationToken.None);
        _preparer.Prepare(new UploadTracks([main], []), tracks, CancellationToken.None);

        Assert.That(File.ReadAllBytes(tracks), Is.EqualTo(File.ReadAllBytes(alone)));
        Assert.That(_log.Messages, Has.None.EqualTo(StayedSilent));
    }

    /// <summary>A microphone track whose files are empty or damaged is left out like a silent one, with the damaged file reported.</summary>
    [Test]
    public void A_microphone_track_of_empty_and_damaged_files_is_left_out()
    {
        var main = Wav("class.wav", 16000, [1000, 1000]);
        var empty = _folder.File("class.mic.wav");
        var damaged = _folder.File("class_part2.mic.wav");
        File.WriteAllBytes(empty, []);
        File.WriteAllText(damaged, "This is not audio at all, although the name says so.");

        Assert.That(Mixed([main], [empty, damaged]), Is.EqualTo(new short[] { 1000, 1000 }));
        Assert.That(_log.Messages, Has.Some.EqualTo(StayedSilent));
        Assert.That(_log.Problems, Has.Some.Contains("class_part2.mic.wav"));
    }

    /// <summary>The parts of each track are joined in order before the two are mixed, so a part boundary on one track moves nothing on the other.</summary>
    [Test]
    public void The_parts_of_each_track_are_joined_in_order()
    {
        var main1 = Wav("class.wav", 16000, [1000, 1000]);
        var main2 = Wav("class_part2.wav", 16000, [2000, 2000]);
        var microphone1 = Wav("class.mic.wav", 16000, [300]);
        var microphone2 = Wav("class_part2.mic.wav", 16000, [400, 400]);

        Assert.That(Mixed([main1, main2], [microphone1, microphone2]), Is.EqualTo(new short[] { 1300, 1400, 2400, 2000 }));
    }

    /// <summary>
    /// A damaged part of the microphone's track is skipped with a warning, and the parts around it are still mixed in.
    /// The sound check meets the file first and reports it; the mix then passes over it without a second warning,
    /// so the log names one damaged file, not two.
    /// </summary>
    [Test]
    public void A_damaged_part_of_the_microphone_track_is_skipped_and_the_rest_is_mixed()
    {
        var main = Wav("class.wav", 16000, [1000, 1000, 1000]);
        var damaged = _folder.File("class.mic.wav");
        File.WriteAllText(damaged, "This is not audio at all, although the name says so.");
        var microphone = Wav("class_part2.mic.wav", 16000, [500, 500]);

        Assert.That(Mixed([main], [damaged, microphone]), Is.EqualTo(new short[] { 1500, 1500, 1000 }));
        Assert.That(_log.Problems, Has.Exactly(1).EqualTo("Skipping class.mic.wav: it is not a readable WAV file"));
    }

    /// <summary>
    /// A damaged part the sound check never opened, because the microphone was heard a whole second before it, is
    /// still skipped with a warning by the mix, and once.
    /// </summary>
    [Test]
    public void A_damaged_part_after_the_microphone_was_heard_is_skipped_and_reported_once()
    {
        var main = Wav("class.wav", 16000, Steady(1000, 16002));
        var microphone = Wav("class.mic.wav", 16000, Steady(500, 16001));
        var damaged = _folder.File("class_part2.mic.wav");
        File.WriteAllText(damaged, "This is not audio at all, although the name says so.");
        var more = Wav("class_part3.mic.wav", 16000, [600]);

        var mixed = Mixed([main], [microphone, damaged, more]);

        Assert.That(mixed.Length, Is.EqualTo(16002));
        Assert.That(mixed[..16001], Is.All.EqualTo(1500));
        Assert.That(mixed[16001], Is.EqualTo(1600), "the part after the damaged one follows it straight on");
        Assert.That(_log.Problems, Has.Exactly(1).EqualTo("Skipping class_part2.mic.wav: it is not a readable WAV file"));
    }

    /// <summary>A damaged part of the class is skipped as today, and the microphone is mixed with what is left of the class.</summary>
    [Test]
    public void A_damaged_part_of_the_class_is_skipped_and_the_rest_is_mixed()
    {
        var main1 = Wav("class.wav", 16000, [1000]);
        var damaged = _folder.File("class_part2.wav");
        File.WriteAllText(damaged, "This is not audio at all, although the name says so.");
        var main3 = Wav("class_part3.wav", 16000, [2000]);
        var microphone = Wav("class.mic.wav", 16000, [300, 400]);

        Assert.That(Mixed([main1, damaged, main3], [microphone]), Is.EqualTo(new short[] { 1300, 2400 }));
        Assert.That(_log.Problems, Has.Some.Contains("class_part2.wav"));
    }

    /// <summary>A class of digital silence with a microphone that was heard is still sent: the upload is then the microphone over the class's silence.</summary>
    [Test]
    public void A_silent_class_with_a_heard_microphone_is_sent_as_the_microphone()
    {
        var main = Wav("class.wav", 16000, new short[16000]);
        var microphone = Wav("class.mic.wav", 16000, [0, 300, -300]);

        var mixed = Mixed([main], [microphone]);

        Assert.That(mixed.Length, Is.EqualTo(16000));
        Assert.That(mixed[..4], Is.EqualTo(new short[] { 0, 300, -300, 0 }));
    }

    /// <summary>A cancel that comes while the tracks are being mixed stops the mix at once and leaves no unfinished upload file.</summary>
    [Test]
    public void A_cancel_during_the_mix_leaves_no_file()
    {
        using var cancellation = new CancellationTokenSource();
        var main = _folder.File("class.wav");
        ForeignWav.Write(main, new WaveFormat(16000, 16, 2), TestAudio.Shorts(1000, 3000, -1000, -3000, 500, 1500));
        ForeignWav.LeaveUnfinished(main, dataBytes: 0);   // reported with a warning as the class track is opened, mid-mix
        var microphone = Wav("class.mic.wav", 16000, [500, 500]);
        var preparer = new AudioPreparer(new ActingLogger(onWarning: cancellation.Cancel));
        var output = _folder.File("upload.wav");

        Assert.Throws<OperationCanceledException>(() => preparer.Prepare(new UploadTracks([main], [microphone]), output, cancellation.Token));

        Assert.That(File.Exists(output), Is.False);
    }

    /// <summary>A token cancelled before the mix starts stops it before anything is written.</summary>
    [Test]
    public void A_cancel_before_the_mix_leaves_no_file()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var main = Wav("class.wav", 16000, [1000, 1000]);
        var microphone = Wav("class.mic.wav", 16000, [500, 500]);
        var output = _folder.File("upload.wav");

        Assert.Throws<OperationCanceledException>(() => _preparer.Prepare(new UploadTracks([main], [microphone]), output, cancellation.Token));

        Assert.That(File.Exists(output), Is.False);
        Assert.That(_log.Messages, Has.Some.EqualTo("Stopped preparing the upload file: cancelled"));
    }

    /// <summary>A file already where the upload file should go is never overwritten by a mix either.</summary>
    [Test]
    public void A_file_already_at_the_upload_path_is_not_touched_by_a_mix()
    {
        var main = Wav("class.wav", 16000, [1000, 1000]);
        var microphone = Wav("class.mic.wav", 16000, [500, 500]);
        var output = _folder.File("upload.wav");
        File.WriteAllText(output, "precious");

        Assert.Throws<IOException>(() => _preparer.Prepare(new UploadTracks([main], [microphone]), output, CancellationToken.None));

        Assert.That(File.ReadAllText(output), Is.EqualTo("precious"));
        Assert.That(_log.Problems, Has.Some.Contains("upload.wav"));
    }

    /// <summary>The length of the mix is logged like the length of a class alone, and the line says that the microphone is in it.</summary>
    [Test]
    public void The_length_mixed_is_logged_with_the_microphone_named()
    {
        var main = Wav("class.wav", 16000, TestAudio.Tone(16000, seconds: 2.5, frequency: 440, amplitude: 0.5));
        var microphone = Wav("class.mic.wav", 16000, TestAudio.Tone(16000, seconds: 1, frequency: 880, amplitude: 0.2));

        _preparer.Prepare(new UploadTracks([main], [microphone]), _folder.File("upload.wav"), CancellationToken.None);

        Assert.That(_log.Entries, Has.Some.EqualTo((LogLevel.Information, "Prepared 00:00:02 of audio for upload: the class and your microphone mixed into one track")));
    }

    /// <summary>The sound threshold is the one the design gives: 100 of 32,767.</summary>
    [Test]
    public void The_sound_threshold_is_100_of_32767() => Assert.That(AudioPreparer.MicrophoneSoundThreshold, Is.EqualTo(100));

    /// <summary>A logger that acts the moment a warning is logged, so a test can cancel while a file is being opened.</summary>
    private sealed class ActingLogger(Action onWarning) : ILogger<AudioPreparer>
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) onWarning();
        }
    }
}
