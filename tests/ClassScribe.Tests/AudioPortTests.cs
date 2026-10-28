using System.Net;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Transcription;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of <see cref="IAudioFileFacts"/> and <see cref="ITranscriptAnswers"/> over their real adapters, in a
/// temporary folder, and of the capture's processing on <see cref="IAudioCapture"/>: a WAV file's length read to its end,
/// a file the recorder did not write telling nothing, a saved answer read back and an answer kept under a new name never
/// over another, and the microphone tap taking the processing a capture reports.
/// </summary>
[TestFixture]
public class AudioPortTests
{
    private TempFolder _folder = null!;

    /// <summary>Creates a temporary folder.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The length is read to the end of the file, whatever the header's sizes say, and a file the recorder did not write gives null; a missing file is the file system's error.</summary>
    [Test]
    public void Reads_a_wav_files_length_to_its_end()
    {
        IAudioFileFacts facts = new AudioFileFacts();
        var ours = _folder.File("ours.wav");
        using (var writer = new WavSegmentWriter(ours, 10)) writer.Write(new byte[10 * 2 * 90]);
        using (var grown = new FileStream(ours, FileMode.Append)) grown.Write(new byte[10 * 2 * 30]);
        var theirs = _folder.File("theirs.wav");
        using (var other = new WaveFileWriter(theirs, new WaveFormat(16_000, 16, 2))) other.Write(new byte[16_000 * 4], 0, 16_000 * 4);
        File.WriteAllText(_folder.File("notes.txt"), "not audio");

        Assert.Multiple(() =>
        {
            Assert.That(facts.AudioLength(ours), Is.EqualTo(TimeSpan.FromSeconds(120)), "the audio after the header, to the end of the file");
            Assert.That(facts.AudioLength(theirs), Is.Null, "a stereo WAV from another tool is not ClassScribe's");
            Assert.That(facts.AudioLength(_folder.File("notes.txt")), Is.Null);
            Assert.Throws<FileNotFoundException>(() => facts.AudioLength(_folder.File("missing.wav")));
        });
    }

    /// <summary>A saved answer is read back through the same parser the client uses, and one that is not an answer is refused with its JSON kept.</summary>
    [Test]
    public async Task Reads_a_saved_answer_back()
    {
        ITranscriptAnswers answers = new ElevenLabsAnswer();
        var saved = _folder.File("Kalam_2026-10-15_20-55-27Z.json");
        File.WriteAllText(saved, """{ "text": "Today we read.", "language_code": "eng", "words": [ { "text": "Today", "start": 0.1, "end": 0.4, "type": "word" } ] }""");
        File.WriteAllText(_folder.File("broken.json"), "{ not json");

        var result = await answers.ReadAsync(saved, CancellationToken.None);
        var refused = Assert.ThrowsAsync<TranscriptionException>(() => answers.ReadAsync(_folder.File("broken.json"), CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("Today we read."));
            Assert.That(result.Words.Select(word => word.Text), Is.EqualTo(new[] { "Today" }));
            Assert.That(result.RawJson, Is.EqualTo(File.ReadAllText(saved)), "the JSON exactly as saved");
            Assert.That(refused!.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a saved answer came with a success status");
            Assert.That(refused.ResponseBody, Is.EqualTo("{ not json"));
            Assert.ThrowsAsync<FileNotFoundException>(() => answers.ReadAsync(_folder.File("missing.json"), CancellationToken.None));
        });
    }

    /// <summary>An answer is kept under a new name in UTF-8 without a byte order mark, and never over a file that is there.</summary>
    [Test]
    public void Keeps_an_answer_under_a_new_name_only()
    {
        ITranscriptAnswers answers = new ElevenLabsAnswer();
        var kept = _folder.File("Kalam_2026-10-15_20-55-27Z.unread.json");

        answers.WriteNew(kept, """{ "text": "al-Ghazālī" }""");

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(kept).Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "no byte order mark");
            Assert.That(File.ReadAllText(kept), Is.EqualTo("""{ "text": "al-Ghazālī" }"""));
            Assert.Throws<IOException>(() => answers.WriteNew(kept, "{}"), "never over a file that is there");
            Assert.That(File.ReadAllText(kept), Is.EqualTo("""{ "text": "al-Ghazālī" }"""), "the first answer stays");
        });
    }

    /// <summary>The microphone tap takes the processing a capture reports on the port, and the processing asked for when a capture reports none.</summary>
    [Test]
    public void The_tap_takes_the_processing_a_capture_reports()
    {
        var provider = new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array);
        var tap = new MicrophoneProviderTap(provider);
        var format = new AudioFormat(48000, 2, 32, true);

        provider.Prepare(new FakeAudioCapture(format, LaptopMicrophones.Array.Name) { Processing = MicrophoneProcessing.Default });
        using (tap.CreateCapture(LaptopMicrophones.Array, MicrophoneProcessing.Raw))
            Assert.That(tap.Last?.Processing, Is.EqualTo(MicrophoneProcessing.Default), "what the capture reports wins over what was asked");

        provider.Prepare(new FakeAudioCapture(format, LaptopMicrophones.Array.Name));
        using (tap.CreateCapture(LaptopMicrophones.Array, MicrophoneProcessing.Raw))
            Assert.That(tap.Last?.Processing, Is.EqualTo(MicrophoneProcessing.Raw), "a capture that reports none leaves what was asked");
    }
}
