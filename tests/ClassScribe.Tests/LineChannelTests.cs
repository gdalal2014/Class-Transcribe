using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// The link's framing: each message is one line of UTF-8 JSON naming its type and holding its body, read back as it was
/// sent; fields and types a reader does not know are passed over; a line over 64 KB, or one that is not a message, is
/// refused; lines arrive whole however the stream cuts them.
/// </summary>
[TestFixture]
public class LineChannelTests
{
    private DuplexStreamPair _pair = null!;
    private LineChannel _recorder = null!;
    private LineChannel _window = null!;
    private CancellationTokenSource _patience = null!;

    /// <summary>A connection with a channel at each end, and a limit on how long any step may take.</summary>
    [SetUp]
    public void SetUp()
    {
        _pair = new DuplexStreamPair();
        _recorder = new LineChannel(_pair.RecorderEnd);
        _window = new LineChannel(_pair.WindowEnd);
        _patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    }

    /// <summary>Closes both channels.</summary>
    [TearDown]
    public async Task TearDown()
    {
        await _recorder.DisposeAsync();
        await _window.DisposeAsync();
        _pair.Dispose();
        _patience.Dispose();
    }

    /// <summary>A token cancelled once the test has run too long, so that a wait that never ends fails the test.</summary>
    private CancellationToken Token => _patience.Token;

    /// <summary>The body of <paramref name="envelope"/>, after checking that it is a message of <paramref name="type"/>.</summary>
    private static T Body<T>(Envelope? envelope, string type, JsonTypeInfo<T> info)
    {
        Assert.That(envelope, Is.Not.Null, "a message was expected, not the end of the stream");
        Assert.That(envelope!.Type, Is.EqualTo(type));
        return envelope.Body.Deserialize(info)!;
    }

    /// <summary>Writes <paramref name="text"/> to the recorder's end as raw UTF-8, as another program might.</summary>
    private async Task WriteRawToRecorderAsync(string text) => await _pair.WindowEnd.WriteAsync(Encoding.UTF8.GetBytes(text), Token);

    /// <summary>A run as the recorder knows it, with every kind of field filled.</summary>
    private static RunRecord Run() =>
        TestRuns.Record(TestRuns.Id(), RunState.Recording) with { BaseName = "Kalam_2026-10-15_23-00-02Z", Folder = @"D:\Classes\Kalam", Position = TimeSpan.FromMinutes(42) };

    /// <summary>A state with every kind of field filled.</summary>
    private static RecorderState State() =>
        new(RecorderPhase.Recording, "Kalam", TimeSpan.FromSeconds(2533), "Speakers (Realtek(R) Audio)", null,
            new MicrophoneState("Microphone Array", On: true, Capturing: true, Problem: null), UploadPercent: null,
            StopsAtUtc: TestRuns.Start.AddMinutes(90), new Attention(AttentionLevel.Notice, "no sound 2 min", "No sound for 2 minutes", 3), InWindow: false);

    /// <summary>Every message the link has reads back at the other end as it was sent, in order.</summary>
    [Test]
    public async Task Every_message_reads_back_as_it_was_sent()
    {
        var hello = new Hello(LinkProtocol.Version, "1.0.0+test", Since: 42, Interactive: true, Alert: false, Owner: true);
        var key = new KeyPress(LinkKeys.Q);
        var bye = new Bye(ByeReasons.Esc);
        var welcome = new Welcome(LinkProtocol.Version, "1.0.0+test", @"C:\Programs\ClassScribe\ClassScribe.exe", Run(), State(), OldestSeq: 7);
        var refused = new Refused("The recorder speaks protocol 1; this window speaks 2.", @"C:\Programs\ClassScribe\ClassScribe.exe");
        var line = new LogLine(43, TestRuns.Start.AddSeconds(5), LogLevel.Warning, "19:00:07 WARN  No sound for 60 seconds");
        var state = new StateUpdate(State());
        var end = new End(new RunOutcome(OutcomeKind.Transcribed, 0, "Saved the transcript", @"D:\Classes\Kalam\Kalam.md", @"D:\Classes\Kalam", null), Open: true);

        await _window.SendAsync(LinkTypes.Hello, hello, LinkJson.Default.Hello, Token);
        await _window.SendAsync(LinkTypes.Key, key, LinkJson.Default.KeyPress, Token);
        await _window.SendAsync(LinkTypes.Bye, bye, LinkJson.Default.Bye, Token);
        await _recorder.SendAsync(LinkTypes.Welcome, welcome, LinkJson.Default.Welcome, Token);
        await _recorder.SendAsync(LinkTypes.Refused, refused, LinkJson.Default.Refused, Token);
        await _recorder.SendAsync(LinkTypes.Log, line, LinkJson.Default.LogLine, Token);
        await _recorder.SendAsync(LinkTypes.State, state, LinkJson.Default.StateUpdate, Token);
        await _recorder.SendAsync(LinkTypes.End, end, LinkJson.Default.End, Token);

        Assert.Multiple(async () =>
        {
            Assert.That(Body(await _recorder.ReceiveAsync(Token), LinkTypes.Hello, LinkJson.Default.Hello), Is.EqualTo(hello));
            Assert.That(Body(await _recorder.ReceiveAsync(Token), LinkTypes.Key, LinkJson.Default.KeyPress), Is.EqualTo(key));
            Assert.That(Body(await _recorder.ReceiveAsync(Token), LinkTypes.Bye, LinkJson.Default.Bye), Is.EqualTo(bye));
            Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.Welcome, LinkJson.Default.Welcome), Is.EqualTo(welcome));
            Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.Refused, LinkJson.Default.Refused), Is.EqualTo(refused));
            Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.Log, LinkJson.Default.LogLine), Is.EqualTo(line));
            Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.State, LinkJson.Default.StateUpdate), Is.EqualTo(state));
            Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.End, LinkJson.Default.End), Is.EqualTo(end));
        });
    }

    /// <summary>A run of each source kind, with each continue choice, reads back whole inside a welcome.</summary>
    [Test]
    public async Task A_run_of_every_source_kind_reads_back_whole_in_a_welcome()
    {
        ClassSource[] sources = [new ClassSource.Automatic(), new ClassSource.Device("Pixel Buds"), new ClassSource.App("chrome")];
        foreach (var source in sources)
        {
            var run = Run() with
            {
                Request = TestRuns.Request(TestRuns.Id()) with
                {
                    Source = new SourcePlan(RecordingMode.ClassAndMicrophone, source, new MicrophoneChoice("OBSBOT")),
                    Continue = ContinueChoice.Continue, ContinueBaseName = "Kalam_2026-10-15_23-00-02Z", Minutes = 1.5, Open = false
                }
            };
            var welcome = new Welcome(LinkProtocol.Version, "b", "exe", run, State(), 1);

            await _recorder.SendAsync(LinkTypes.Welcome, welcome, LinkJson.Default.Welcome, Token);

            Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.Welcome, LinkJson.Default.Welcome), Is.EqualTo(welcome), source.ToString());
        }
    }

    /// <summary>A message is one line of UTF-8 JSON, its type and its body, with names in camelCase, ended by a line feed.</summary>
    [Test]
    public async Task A_message_is_one_line_of_JSON_naming_its_type_and_holding_its_body()
    {
        await _window.SendAsync(LinkTypes.Key, new KeyPress(LinkKeys.CtrlC), LinkJson.Default.KeyPress, Token);
        await _window.DisposeAsync();

        using var reader = new StreamReader(_pair.RecorderEnd, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(Token);

        Assert.That(text, Is.EqualTo("{\"type\":\"key\",\"body\":{\"key\":\"CtrlC\"}}\n"));
    }

    /// <summary>Line breaks inside a message, as in a line about a failure with its exception, stay inside its one line and read back as they were.</summary>
    [Test]
    public async Task Line_breaks_inside_a_message_stay_inside_its_line()
    {
        var line = new LogLine(1, TestRuns.Start, LogLevel.Error, "19:00:02 ERROR Upload failed\r\n  IOException: The network went away\nand a second line");

        await _recorder.SendAsync(LinkTypes.Log, line, LinkJson.Default.LogLine, Token);
        await _recorder.SendAsync(LinkTypes.Log, line with { Seq = 2 }, LinkJson.Default.LogLine, Token);

        Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.Log, LinkJson.Default.LogLine), Is.EqualTo(line));
        Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.Log, LinkJson.Default.LogLine), Is.EqualTo(line with { Seq = 2 }));
    }

    /// <summary>Letters beyond English, such as the IJMES letters of a class's name, cross the link as they were.</summary>
    [Test]
    public async Task Letters_beyond_English_read_back_as_they_were()
    {
        var line = new LogLine(1, TestRuns.Start, LogLevel.Information, "Recording \"Uṣūl al-Fiqh\" (أصول الفقه)");

        await _recorder.SendAsync(LinkTypes.Log, line, LinkJson.Default.LogLine, Token);

        Assert.That(Body(await _window.ReceiveAsync(Token), LinkTypes.Log, LinkJson.Default.LogLine).Text, Is.EqualTo(line.Text));
    }

    /// <summary>Fields the reader does not know, in the envelope or in the body, as a later build may add, are passed over.</summary>
    [Test]
    public async Task Fields_a_reader_does_not_know_are_passed_over()
    {
        await WriteRawToRecorderAsync("{\"type\":\"key\",\"sentAt\":\"later\",\"body\":{\"key\":\"M\",\"repeat\":2,\"label\":{\"text\":\"x\"}}}\n");

        var key = Body(await _recorder.ReceiveAsync(Token), LinkTypes.Key, LinkJson.Default.KeyPress);

        Assert.That(key, Is.EqualTo(new KeyPress(LinkKeys.M)));
    }

    /// <summary>A hello without the optional field a later version of this build added reads with that field's default.</summary>
    [Test]
    public async Task A_hello_without_the_owner_field_reads_as_no_owner()
    {
        await WriteRawToRecorderAsync("{\"type\":\"hello\",\"body\":{\"protocol\":1,\"build\":\"b\",\"since\":5,\"interactive\":true,\"alert\":false}}\n");

        var hello = Body(await _recorder.ReceiveAsync(Token), LinkTypes.Hello, LinkJson.Default.Hello);

        Assert.That(hello, Is.EqualTo(new Hello(1, "b", 5, Interactive: true, Alert: false, Owner: false)));
    }

    /// <summary>A message of a type this build does not know still reads as a message, with or without a body; what to do with it is up to the reader.</summary>
    [Test]
    public async Task A_message_of_a_type_the_reader_does_not_know_still_reads()
    {
        await WriteRawToRecorderAsync("{\"type\":\"ping\"}\n{\"type\":\"pong\",\"body\":{\"n\":1}}\n");

        var first = await _recorder.ReceiveAsync(Token);
        var second = await _recorder.ReceiveAsync(Token);

        Assert.That(first?.Type, Is.EqualTo("ping"));
        Assert.That(first?.Body.ValueKind, Is.EqualTo(JsonValueKind.Undefined));
        Assert.That(second?.Type, Is.EqualTo("pong"));
        Assert.That(second?.Body.GetProperty("n").GetInt32(), Is.EqualTo(1));
    }

    /// <summary>A line of exactly 64 KB is taken.</summary>
    [Test]
    public async Task A_line_of_exactly_64_KB_is_taken()
    {
        const string start = "{\"type\":\"log\",\"body\":{\"pad\":\"";
        const string finish = "\"}}";
        var line = start + new string('x', LinkProtocol.MaxLineBytes - start.Length - finish.Length) + finish;
        Assert.That(Encoding.UTF8.GetByteCount(line), Is.EqualTo(LinkProtocol.MaxLineBytes));

        var receiving = _recorder.ReceiveAsync(Token).AsTask();
        await WriteRawToRecorderAsync(line + "\n");

        Assert.That((await receiving)?.Type, Is.EqualTo("log"));
    }

    /// <summary>A line one byte over 64 KB is refused before its end has come, so a stream that never sends a line break cannot fill the reader's memory.</summary>
    [Test]
    public async Task A_line_over_64_KB_is_refused()
    {
        var receiving = _recorder.ReceiveAsync(Token).AsTask();
        var writing = WriteRawToRecorderAsync(new string('x', LinkProtocol.MaxLineBytes + 1));

        var refused = Assert.ThrowsAsync<InvalidDataException>(async () => await receiving);
        Assert.That(refused!.Message, Does.Contain("64 KB"));
        await writing;
    }

    /// <summary>A message too long for a line is not sent at all: the sender is told, and the other end reads nothing of it.</summary>
    [Test]
    public async Task A_message_too_long_for_a_line_is_not_sent()
    {
        var line = new LogLine(1, TestRuns.Start, LogLevel.Information, new string('x', LinkProtocol.MaxLineBytes));

        var refused = Assert.ThrowsAsync<ArgumentException>(async () => await _recorder.SendAsync(LinkTypes.Log, line, LinkJson.Default.LogLine, Token));
        await _recorder.DisposeAsync();

        Assert.That(refused!.Message, Does.Contain("64 KB"));
        Assert.That(await _window.ReceiveAsync(Token), Is.Null);
    }

    /// <summary>A line that is not a message of the link is refused: text that is not JSON, JSON without a type, or with a type that is null.</summary>
    [TestCase("not JSON at all")]
    [TestCase("{\"body\":{\"key\":\"Q\"}}")]
    [TestCase("{\"type\":null,\"body\":{}}")]
    [TestCase("[1,2,3]")]
    [TestCase("{\"type\":\"key\",\"body\":")]
    public async Task A_line_that_is_not_a_message_is_refused(string line)
    {
        await WriteRawToRecorderAsync(line + "\n");

        Assert.ThrowsAsync<InvalidDataException>(async () => await _recorder.ReceiveAsync(Token));
    }

    /// <summary>Blank lines, including a line break written as CR LF, are passed over.</summary>
    [Test]
    public async Task Blank_lines_are_passed_over()
    {
        await WriteRawToRecorderAsync("\n\r\n   \n{\"type\":\"key\",\"body\":{\"key\":\"P\"}}\r\n");

        Assert.That(Body(await _recorder.ReceiveAsync(Token), LinkTypes.Key, LinkJson.Default.KeyPress), Is.EqualTo(new KeyPress(LinkKeys.P)));
    }

    /// <summary>The end of the stream reads as null, and so does every receive after it.</summary>
    [Test]
    public async Task The_end_of_the_stream_reads_as_null()
    {
        await _window.DisposeAsync();

        Assert.That(await _recorder.ReceiveAsync(Token), Is.Null);
        Assert.That(await _recorder.ReceiveAsync(Token), Is.Null);
    }

    /// <summary>A line cut off by the end of the stream, as when the other end's process ends while writing it, is dropped: the stream reads as ended.</summary>
    [Test]
    public async Task A_line_cut_off_by_the_end_of_the_stream_is_dropped()
    {
        await WriteRawToRecorderAsync("{\"type\":\"key\",\"body\":{\"key\":\"P\"}}\n{\"type\":\"key\",\"bo");
        await _window.DisposeAsync();

        Assert.That((await _recorder.ReceiveAsync(Token))?.Type, Is.EqualTo(LinkTypes.Key));
        Assert.That(await _recorder.ReceiveAsync(Token), Is.Null);
    }

    /// <summary>A line split over many reads, and a read holding several lines, both come through as whole messages.</summary>
    [Test]
    public async Task Lines_come_through_whole_however_the_stream_cuts_them()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"key\",\"body\":{\"key\":\"Q\"}}\n");
        var receiving = _recorder.ReceiveAsync(Token).AsTask();
        foreach (var b in bytes) await _pair.WindowEnd.WriteAsync(new[] { b }, Token);
        await WriteRawToRecorderAsync("{\"type\":\"key\",\"body\":{\"key\":\"P\"}}\n{\"type\":\"key\",\"body\":{\"key\":\"M\"}}\n");

        Assert.That(Body(await receiving, LinkTypes.Key, LinkJson.Default.KeyPress).Key, Is.EqualTo(LinkKeys.Q));
        Assert.That(Body(await _recorder.ReceiveAsync(Token), LinkTypes.Key, LinkJson.Default.KeyPress).Key, Is.EqualTo(LinkKeys.P));
        Assert.That(Body(await _recorder.ReceiveAsync(Token), LinkTypes.Key, LinkJson.Default.KeyPress).Key, Is.EqualTo(LinkKeys.M));
    }

    /// <summary>Messages sent at once from several threads never mix: each arrives whole, and each thread's arrive in its order.</summary>
    [Test]
    public async Task Messages_sent_at_once_from_several_threads_never_mix()
    {
        const int threads = 4;
        const int each = 250;
        var reading = Task.Run(async () =>
        {
            List<LogLine> lines = [];
            while (lines.Count < threads * each)
                lines.Add(Body(await _window.ReceiveAsync(Token), LinkTypes.Log, LinkJson.Default.LogLine));
            return lines;
        });

        await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(async () =>
        {
            for (var n = 0; n < each; n++)
                await _recorder.SendAsync(LinkTypes.Log, new LogLine(thread * each + n, TestRuns.Start, LogLevel.Information, $"thread {thread} line {n}"), LinkJson.Default.LogLine, Token);
        })));
        var lines = await reading;

        Assert.That(lines, Has.Count.EqualTo(threads * each));
        for (var thread = 0; thread < threads; thread++)
            Assert.That(lines.Where(line => line.Text.StartsWith($"thread {thread} ", StringComparison.Ordinal)).Select(line => line.Seq),
                Is.EqualTo(Enumerable.Range(thread * each, each).Select(n => (long)n)), $"thread {thread}");
    }

    /// <summary>A receive cancelled by its token throws, and leaves the channel usable: the next message reads whole.</summary>
    [Test]
    public async Task A_receive_cancelled_by_its_token_leaves_the_channel_usable()
    {
        await WriteRawToRecorderAsync("{\"type\":\"key\",");
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            Assert.CatchAsync<OperationCanceledException>(async () => await _recorder.ReceiveAsync(cancel.Token));

        await WriteRawToRecorderAsync("\"body\":{\"key\":\"V\"}}\n");

        Assert.That(Body(await _recorder.ReceiveAsync(Token), LinkTypes.Key, LinkJson.Default.KeyPress), Is.EqualTo(new KeyPress(LinkKeys.V)));
    }

    /// <summary>Disposing a channel closes its stream: the other end reads the end of it.</summary>
    [Test]
    public async Task Disposing_a_channel_closes_its_stream()
    {
        await _recorder.DisposeAsync();

        Assert.That(await _window.ReceiveAsync(Token), Is.Null);
        Assert.ThrowsAsync<IOException>(async () => await _window.SendAsync(LinkTypes.Bye, new Bye(ByeReasons.Closed), LinkJson.Default.Bye, Token));
    }
}

/// <summary>The link's names: the pipe of each run, the keys and the reasons for bye that cross it, and the build it names.</summary>
[TestFixture]
public class LinkProtocolTests
{
    /// <summary>A run's pipe is named after the run.</summary>
    [Test]
    public void A_runs_pipe_is_named_after_the_run()
    {
        Assert.That(LinkProtocol.PipeNameFor("20261015-230002Z-1f3a9c07"), Is.EqualTo("ClassScribe-20261015-230002Z-1f3a9c07"));
    }

    /// <summary>Only a run id names a pipe: anything else is refused before it can name one.</summary>
    [TestCase("")]
    [TestCase("Kalam")]
    [TestCase(@"..\..\x")]
    [TestCase("20261015-230002Z-1f3a9c07\\x")]
    public void Only_a_run_id_names_a_pipe(string runId)
    {
        Assert.Throws<ArgumentException>(() => LinkProtocol.PipeNameFor(runId));
    }

    /// <summary>Only a name made by <see cref="LinkProtocol.PipeNameFor"/> counts as a run's pipe, so a run file changed by hand cannot point a window anywhere else.</summary>
    [TestCase("ClassScribe-20261015-230002Z-1f3a9c07", true)]
    [TestCase("classscribe-20261015-230002Z-1f3a9c07", false)]
    [TestCase("ClassScribe-", false)]
    [TestCase("ClassScribe-20261015-230002Z-1f3a9c07\\..\\x", false)]
    [TestCase("Other-20261015-230002Z-1f3a9c07", false)]
    [TestCase(null, false)]
    public void Only_a_name_made_for_a_run_counts_as_its_pipe(string? name, bool counts)
    {
        Assert.That(LinkProtocol.IsPipeName(name), Is.EqualTo(counts));
    }

    /// <summary>The keys that cross the link are Q, P, M, V and Ctrl+C, written exactly so; nothing else.</summary>
    [TestCase("Q", true)]
    [TestCase("P", true)]
    [TestCase("M", true)]
    [TestCase("V", true)]
    [TestCase("CtrlC", true)]
    [TestCase("q", false)]
    [TestCase("Enter", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void Only_the_recording_keys_cross_the_link(string? key, bool known)
    {
        Assert.That(LinkKeys.IsKnown(key), Is.EqualTo(known));
    }

    /// <summary>A window says bye for Esc or because it is closing, and for nothing else.</summary>
    [TestCase("Esc", true)]
    [TestCase("Closed", true)]
    [TestCase("esc", false)]
    [TestCase("Crashed", false)]
    public void A_window_says_bye_for_Esc_or_closing(string reason, bool known)
    {
        Assert.That(ByeReasons.IsKnown(reason), Is.EqualTo(known));
    }

    /// <summary>The build names ClassScribe's version.</summary>
    [Test]
    public void The_build_names_the_version()
    {
        Assert.That(LinkProtocol.Build, Does.StartWith(typeof(LinkProtocol).Assembly.GetName().Version!.ToString(3)));
    }
}
