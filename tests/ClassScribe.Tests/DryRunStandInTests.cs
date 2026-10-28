using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using ClassScribe.Audio;
using ClassScribe.DryRun;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// The dry-run kit's stand-in for ElevenLabs (tools\dry-run\ElevenLabsStub.cs, compiled into the tests): it finds where an
/// upload has sound, places a canned text's words there, and answers in ElevenLabs' shape, which ClassScribe's own reader
/// (<see cref="ElevenLabsAnswer"/>) must read. Its server listens on 127.0.0.1 only; nothing leaves the computer.
/// </summary>
[TestFixture]
public class DryRunStandInTests
{
    /// <summary>How far a word may stand outside a stretch of sound: one frame of the energy gate.</summary>
    private const double Frame = 0.05;

    private TempFolder _folder = null!;

    /// <summary>Creates a temporary folder for the recordings and the stand-in's log.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Silence of <paramref name="seconds"/> at <paramref name="sampleRate"/>.</summary>
    private static short[] Silence(int sampleRate, double seconds) => new short[(int)(sampleRate * seconds)];

    /// <summary>A tone loud enough to be a lecture, of <paramref name="seconds"/> at <paramref name="sampleRate"/>.</summary>
    private static short[] Sound(int sampleRate, double seconds) => TestAudio.Tone(sampleRate, seconds, frequency: 440, amplitude: 0.2);

    /// <summary>Writes a mono 16-bit WAV with the recorder's own writer and returns its bytes.</summary>
    private byte[] Wav(int sampleRate, params short[][] pieces)
    {
        var path = _folder.File($"{Guid.NewGuid():N}.wav");
        TestAudio.WriteWav(path, sampleRate, [.. pieces.SelectMany(piece => piece)]);
        return File.ReadAllBytes(path);
    }

    /// <summary>The words of a transcript, without the spacing between them.</summary>
    private static List<TranscriptWord> WordsOf(TranscriptionResult result) => [.. result.Words.Where(word => word.Type == "word")];

    /// <summary>Asserts that every word starts and ends inside one of <paramref name="stretches"/>, give or take one frame.</summary>
    private static void AssertInside(IEnumerable<TranscriptWord> words, params (double Start, double End)[] stretches)
    {
        foreach (var word in words)
        {
            Assert.That(word.Start, Is.Not.Null, $"\"{word.Text}\" has a start");
            Assert.That(word.End, Is.Not.Null, $"\"{word.Text}\" has an end");
            Assert.That(stretches.Any(s => word.Start >= s.Start - Frame && word.End <= s.End + Frame), Is.True,
                $"\"{word.Text}\" ({word.Start:0.00} to {word.End:0.00} s) falls where the upload has sound");
        }
    }

    /// <summary>
    /// An upload with sound from 1 to 4 s and from 6 to 8 s, silent elsewhere: the stand-in finds those two stretches, and
    /// its answer, read by ClassScribe's own reader, has words in both, none in the silence, in time order, with spacing
    /// between them, the audio's length, and a transcription id.
    /// </summary>
    [Test]
    public void Words_fall_only_where_the_upload_has_sound()
    {
        var wav = Wav(16000, Silence(16000, 1), Sound(16000, 3), Silence(16000, 2), Sound(16000, 2), Silence(16000, 1));

        var audio = SoundFinder.Read(wav);
        var result = ElevenLabsAnswer.Parse(StandInTranscript.Answer(audio, "dry-run-1"), HttpStatusCode.OK);

        Assert.That(audio.Stretches.Select(s => (Math.Round(s.Start, 1), Math.Round(s.End, 1))), Is.EqualTo(new[] { (1.0, 4.0), (6.0, 8.0) }));
        var words = WordsOf(result);
        Assert.That(words.Count(w => w.Start < 5), Is.GreaterThanOrEqualTo(4), "the first stretch has words");
        Assert.That(words.Count(w => w.Start > 5), Is.GreaterThanOrEqualTo(3), "the second stretch has words");
        AssertInside(words, (1, 4), (6, 8));
        Assert.That(result.Words.Select(w => w.Start), Is.Ordered, "the items are in time order");
        Assert.That(result.Words.Where((w, i) => i % 2 == 1).Select(w => w.Type), Is.All.EqualTo("spacing"), "words and spacing alternate");
        Assert.That(result.Text, Is.EqualTo(string.Join(" ", words.Select(w => w.Text))));
        Assert.That((result.AudioDurationSecs, result.TranscriptionId, result.LanguageCode), Is.EqualTo((9.0, "dry-run-1", "eng")));
    }

    /// <summary>An upload that is silent, or holds only the hiss of a quiet room, gets an empty transcript that still reads.</summary>
    [Test]
    public void A_silent_or_hissing_upload_gets_an_empty_transcript_that_still_reads()
    {
        var hiss = Enumerable.Range(0, 16000 * 3).Select(i => (short)(i % 7 * 3 - 9)).ToArray();
        foreach (var wav in new[] { Wav(16000, Silence(16000, 4)), Wav(16000, hiss) })
        {
            var audio = SoundFinder.Read(wav);
            var result = ElevenLabsAnswer.Parse(StandInTranscript.Answer(audio, "dry-run-2"), HttpStatusCode.OK);

            Assert.That(audio.Stretches, Is.Empty);
            Assert.That((result.Text, result.Words.Count), Is.EqualTo(("", 0)));
            Assert.That(result.AudioDurationSecs, Is.EqualTo(audio.Seconds).Within(0.001));
        }
    }

    /// <summary>A pause shorter than the gate's bridge stays inside one stretch; a click shorter than a word is left out.</summary>
    [Test]
    public void A_short_pause_is_bridged_and_a_click_is_left_out()
    {
        var wav = Wav(16000, Sound(16000, 1), Silence(16000, 0.3), Sound(16000, 1), Silence(16000, 2), Sound(16000, 0.05), Silence(16000, 1));

        var audio = SoundFinder.Read(wav);

        Assert.That(audio.Stretches.Select(s => (Math.Round(s.Start, 1), Math.Round(s.End, 1))), Is.EqualTo(new[] { (0.0, 2.3) }));
    }

    /// <summary>A file that is not a WAV the stand-in can read is refused with a reason, which the server answers as ElevenLabs would.</summary>
    [Test]
    public void A_file_that_is_not_a_wav_is_refused_with_a_reason()
    {
        Assert.That(() => SoundFinder.Read(Encoding.ASCII.GetBytes("ID3 not a wave file at all")), Throws.InstanceOf<InvalidDataException>());
    }

    /// <summary>
    /// The request exactly as ClassScribe's Refit interface builds it, taken off the wire by a handler: the stand-in finds the
    /// audio, the model and each glossary term in it, and counts the terms.
    /// </summary>
    [Test]
    public async Task The_stand_in_reads_the_request_ClassScribe_sends()
    {
        var wav = Wav(16000, Sound(16000, 1));
        var handler = new WireTap();
        var api = RestService.ForGenerated<IElevenLabsApi>(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9/") }, ElevenLabsJson.Default, ElevenLabsApiSettings.Create());

        using var response = await api.TranscribeAsync("dry-run-key", ElevenLabsApi.WrittenFor, "scribe_v2", "false", "word", null, "ar", ["kalām", "wājib al-wujūd", "tawḥīd"],
            new StreamPart(new MemoryStream(wav), "audio.wav", "application/octet-stream"), CancellationToken.None);
        var upload = StandInUpload.Read(handler.ContentType!, handler.Body!);

        Assert.That(upload.Audio.ToArray(), Is.EqualTo(wav));
        Assert.That((upload.Keyterms, upload.Field("model_id"), upload.Field("language_code"), upload.Field("no_verbatim")), Is.EqualTo((3, "scribe_v2", "ar", (string?)null)));
    }

    /// <summary>A request without a file part is refused with a reason.</summary>
    [Test]
    public void A_request_without_a_file_is_refused_with_a_reason()
    {
        var body = "--b\r\nContent-Disposition: form-data; name=model_id\r\n\r\nscribe_v2\r\n--b--\r\n"u8.ToArray();

        Assert.That(() => StandInUpload.Read("multipart/form-data; boundary=b", body), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("file"));
        Assert.That(() => StandInUpload.Read("application/json", body), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("multipart"));
    }

    /// <summary>
    /// The whole way, on this computer only: a recording at 48 kHz is prepared for upload by ClassScribe's own preparer, sent by
    /// ClassScribe's own client to the stand-in's server on 127.0.0.1, and the transcript's words fall where the recording has
    /// sound. The account reads as ElevenLabs' would, its credits grown by the audio's seconds. A path ElevenLabs has but the
    /// stand-in does not is answered 404 and logged as unexpected. The log gives sizes and the number of terms, never the key.
    /// </summary>
    [Test]
    public async Task The_stand_in_answers_ClassScribe_over_http_on_this_computer()
    {
        var recording = _folder.File("Dry run_2026-10-20_20-00-00Z.wav");
        TestAudio.WriteWav(recording, 48000, [.. Silence(48000, 1), .. Sound(48000, 2), .. Silence(48000, 1.5), .. Sound(48000, 1), .. Silence(48000, 0.5)]);
        var upload = _folder.File("Dry run_2026-10-20_20-00-00Z.upload.wav");
        new AudioPreparer(NullLogger<AudioPreparer>.Instance).Prepare([recording], upload, CancellationToken.None);
        var log = _folder.File("stand-in.log");

        await using (var server = StandInServer.Start(0, log, echo: false))
        {
            Assert.That((server.Address.Scheme, server.Address.Host, server.Address.Port > 0), Is.EqualTo(("http", "127.0.0.1", true)));
            using var http = new HttpClient { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(30) };
            var api = RestService.ForGenerated<IElevenLabsApi>(http, ElevenLabsJson.Default, ElevenLabsApiSettings.Create());
            var options = TestOptions.Create(new ElevenLabsOptions { ApiKey = "dry-run-dummy-key", RetryDelaySeconds = 0, MaxAttempts = 1 });
            var account = new ElevenLabsAccount(api, options, TimeProvider.System, NullLogger<ElevenLabsAccount>.Instance);
            var client = new ElevenLabsClient(api, options, new TranscriptionProgress(), TimeProvider.System, NullLogger<ElevenLabsClient>.Instance);

            var before = await account.ReadAsync(CancellationToken.None);
            var result = await client.TranscribeAsync(upload, ["kalām", "tawḥīd"], CancellationToken.None);
            var after = await account.ReadAsync(CancellationToken.None);
            using var unknown = await http.GetAsync("v1/voices?page=2");

            AssertInside(WordsOf(result), (1, 3), (4.5, 5.5));
            Assert.That(WordsOf(result), Has.Count.GreaterThanOrEqualTo(4));
            Assert.That(result.AudioDurationSecs, Is.EqualTo(6).Within(0.01));
            Assert.That((before.Kind, after.Kind), Is.EqualTo((AccountReadingKind.Read, AccountReadingKind.Read)));
            Assert.That(after.CharacterCount - before.CharacterCount, Is.EqualTo(6), "the credits grow by the audio's seconds");
            Assert.That(after.CharacterLimit, Is.GreaterThan(after.CharacterCount));
            Assert.That(after.NextReset, Is.GreaterThan(DateTimeOffset.UtcNow));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        var lines = File.ReadAllLines(log);
        Assert.That(lines.Count(line => line.Contains("POST /v1/speech-to-text", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(lines.Count(line => line.Contains("GET /v1/user/subscription", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That(lines.Single(line => line.Contains("POST /v1/speech-to-text", StringComparison.Ordinal)),
            Does.Contain("2 keyterms").And.Contain("model scribe_v2").And.Contain("2 stretches with sound").And.Contain("answered 200"));
        Assert.That(lines.Single(line => line.Contains(StandInServer.Unexpected, StringComparison.Ordinal)), Does.Contain("GET /v1/voices").And.Contain("404").And.Not.Contain("page=2"));
        Assert.That(string.Join("\n", lines), Does.Not.Contain("dry-run-dummy-key").And.Not.Contain("xi-api-key").And.Not.Contain("kalām"));
    }

    /// <summary>A port already taken by another listener is not used: the stand-in fails to start and says so.</summary>
    [Test]
    public async Task A_port_already_taken_is_refused()
    {
        await using var first = StandInServer.Start(0, _folder.File("first.log"), echo: false);

        Assert.That(() => StandInServer.Start(first.Address.Port, _folder.File("second.log"), echo: false), Throws.InstanceOf<HttpListenerException>());
    }

    /// <summary>
    /// Started with --parent, as the dry run starts it, the stand-in serves while that process runs and ends by itself once
    /// it ends, as when the dry run's window is closed with its X: here a hidden cmd.exe of the test's own plays the dry
    /// run, and ends when told to. Its log says why the stand-in stopped.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_stand_in_ends_with_the_process_that_started_it()
    {
        using var parent = StartWaitingCmd();
        using var stop = new CancellationTokenSource();
        var log = _folder.File("stand-in.log");
        var ready = _folder.File("stand-in.ready");
        try
        {
            var run = StandInProgram.RunAsync(["0", log, "--ready", ready, "--delay", "0", "--parent", parent.Id.ToString(CultureInfo.InvariantCulture)], stop.Token);
            var port = await ReadyPortAsync(ready, run);
            using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) })
            using (var account = await http.GetAsync("v1/user/subscription"))
            {
                Assert.That(account.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }

            Assert.That(run.IsCompleted, Is.False, "the stand-in serves while the process that started it runs");

            await parent.StandardInput.WriteLineAsync("exit");
            var code = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.That(code, Is.EqualTo(0));
            Assert.That(File.ReadAllText(log), Does.Contain($"The process that started the stand-in ({parent.Id}) ended, so the stand-in stops").And.Contain("The stand-in stopped"));
        }
        finally
        {
            // Whatever happened above, nothing of the test's is left running: the stand-in is stopped, and so is the cmd.exe.
            await stop.CancelAsync();
            if (!parent.HasExited) parent.Kill();
        }
    }

    /// <summary>
    /// A --parent that has ended already starts no stand-in (exit code 4, no ready file), since nothing would stop it; a
    /// --parent that is not a process id is a mistake in the arguments (exit code 2).
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task A_parent_that_has_ended_starts_no_stand_in()
    {
        int ended;
        using (var gone = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 0") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            await gone.WaitForExitAsync();
            ended = gone.Id;
        }

        var ready = _folder.File("stand-in.ready");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Assert.That(await StandInProgram.RunAsync(["0", _folder.File("a.log"), "--ready", ready, "--parent", ended.ToString(CultureInfo.InvariantCulture)], limit.Token), Is.EqualTo(4));
        Assert.That(await StandInProgram.RunAsync(["0", _folder.File("b.log"), "--parent", "me"], limit.Token), Is.EqualTo(2));
        Assert.That(File.Exists(ready), Is.False);
    }

    /// <summary>A hidden cmd.exe of the test's own, which waits for commands on its input and ends on "exit".</summary>
    private static Process StartWaitingCmd() =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /q")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        })!;

    /// <summary>
    /// Waits for the stand-in's ready file ("&lt;process id&gt; &lt;port&gt;") and returns the port; fails when the stand-in
    /// ends first or the file has not come after 30 seconds. A file another program holds for a moment just after it
    /// appears, as a virus scanner does, is read again, as the script reads it.
    /// </summary>
    private static async Task<int> ReadyPortAsync(string ready, Task<int> run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(ready))
        {
            Assert.That(run.IsCompleted, Is.False, "the stand-in ended before it was ready");
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the stand-in was ready within 30 seconds");
            await Task.Delay(50);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return int.Parse((await File.ReadAllTextAsync(ready)).Trim().Split(' ')[1], CultureInfo.InvariantCulture);
            }
            catch (IOException) when (attempt < 20)
            {
                // Deliberately not rethrown: another program holds the new file for a moment; it is read again shortly.
                await Task.Delay(100);
            }
        }
    }

    /// <summary>A handler that keeps the request's body and content type, and answers it with an empty transcript.</summary>
    private sealed class WireTap : HttpMessageHandler
    {
        /// <summary>The content type the request was sent with, boundary included.</summary>
        public string? ContentType { get; private set; }

        /// <summary>The request's body, byte for byte.</summary>
        public byte[]? Body { get; private set; }

        /// <summary>Keeps the request and answers it.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ContentType = request.Content!.Headers.ContentType!.ToString();
            Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"","words":[]}""") };
        }
    }
}
