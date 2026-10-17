using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ClassScribe.Commands;
using ClassScribe.Link;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// The link over a real named pipe, made by the server and opened by the client in this process: hello, welcome, a line, a
/// key and the end cross it; the pipe is the first of its name and for this user only, so a name already taken stops a
/// recorder from starting; a fifth window waits quietly for one of four to leave; what the recorder writes does not wait
/// for a window to read it; and a pipe that cannot be made is tried again. Each test uses a run id of its own, made now, so
/// no recorder's pipe is ever touched. Nothing is recorded and no other process is started: the run's process is this
/// test's own.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public class NamedPipeLinkTests
{
    private RunRecord _run = null!;
    private RecorderLog _log = null!;
    private RecordingLogger<RecorderServer> _serverLog = null!;
    private RecorderServer _server = null!;
    private CancellationTokenSource _patience = null!;
    private List<IAsyncDisposable> _closing = null!;

    /// <summary>A run of its own, recorded by this test's process, and a server for it that has not started yet.</summary>
    [SetUp]
    public void SetUp()
    {
        var runId = RunIds.New(DateTimeOffset.UtcNow);
        using var me = Process.GetCurrentProcess();
        _run = TestRuns.Record(runId, RunState.Recording, processId: Environment.ProcessId, processStartedUtc: me.StartTime.ToUniversalTime())
            with { Pipe = LinkProtocol.PipeNameFor(runId) };
        _log = new RecorderLog(TimeProvider.System);
        _serverLog = new RecordingLogger<RecorderServer>();
        _server = new RecorderServer(_log, TimeProvider.System, _serverLog);
        _patience = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _closing = [];
    }

    /// <summary>Closes the clients and pipes the test made, stops the server and disposes the log.</summary>
    [TearDown]
    public async Task TearDown()
    {
        foreach (var closing in Enumerable.Reverse(_closing)) await closing.DisposeAsync();
        await _server.DisposeAsync();
        _log.Dispose();
        _patience.Dispose();
    }

    /// <summary>A token cancelled once the test has run too long, so that a wait that never ends fails the test.</summary>
    private CancellationToken Token => _patience.Token;

    /// <summary>The name of the run's pipe.</summary>
    private string Pipe => _run.Pipe!;

    /// <summary>A client with the real pipe and the real check of the recorder's process; closed when the test ends.</summary>
    private RecorderClient NewClient()
    {
        var client = new RecorderClient(new ProcessLiveness(TimeProvider.System, new RecordingLogger<ProcessLiveness>()), TimeProvider.System, new RecordingLogger<RecorderClient>());
        _closing.Add(client);
        return client;
    }

    /// <summary>The pipe opened as any program of this user could, without the link's protocol; closed when the test ends.</summary>
    private NamedPipeClientStream RawClient()
    {
        var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        _closing.Add(pipe);
        return pipe;
    }

    /// <summary>The window's hello, the welcome, a line of the log, a key and the run's end cross a real pipe, and the end's wait ends once the window has gone.</summary>
    [Test]
    public async Task Hello_welcome_a_line_a_key_and_the_end_cross_a_real_pipe()
    {
        _log.Write(LogLevel.Information, "19:00:02  Recording \"Kalam\"");
        _server.Start(Pipe, _run);
        var client = NewClient();

        var welcome = await client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = client.ReadAsync(Token).GetAsyncEnumerator(Token);
        _closing.Add(reading);
        Assert.That(await reading.MoveNextAsync(), Is.True);
        var line = reading.Current;
        var sent = await client.SendKeyAsync(LinkKeys.M, Token);
        var key = await _server.Keys.ReadAsync(Token);
        var outcome = new RunOutcome(OutcomeKind.Recorded, 0, "Saved the recording", null, @"D:\Classes\Kalam", null);
        var ending = _server.EndAsync(outcome, open: true, TimeSpan.FromSeconds(10));
        Assert.That(await reading.MoveNextAsync(), Is.True);
        var end = reading.Current;

        Assert.That(welcome.Run, Is.EqualTo(_run));
        Assert.That(welcome.Protocol, Is.EqualTo(LinkProtocol.Version));
        Assert.That(line, Is.InstanceOf<LogLine>().With.Property(nameof(LogLine.Text)).EqualTo("19:00:02  Recording \"Kalam\""));
        Assert.That((sent, key.Key), Is.EqualTo((true, ConsoleKey.M)));
        Assert.That(end, Is.EqualTo(new End(outcome, Open: true)));
        Assert.That(await reading.MoveNextAsync(), Is.False);
        Assert.That(await ending.WaitAsync(TimeSpan.FromSeconds(5)), Is.True, "the window took the end and went");
    }

    /// <summary>A second recorder cannot take a pipe name already in use: the first instance of a name is the only one, so it fails, and says so.</summary>
    [Test]
    public async Task A_second_recorder_cannot_take_a_pipe_name_in_use()
    {
        _server.Start(Pipe, _run);
        var secondLog = new RecordingLogger<RecorderServer>();
        await using var second = new RecorderServer(_log, TimeProvider.System, secondLog);

        Assert.Throws<UnauthorizedAccessException>(() => second.Start(Pipe, _run));
        Assert.That(secondLog.Problems, Has.Some.Contains(Pipe));
    }

    /// <summary>
    /// A pipe of the run's name made by another program first, as a program posing as the recorder would, stops the
    /// recorder from starting, and the reason is logged with the pipe's name. Windows says so in one of two ways: a pipe
    /// that allows one instance is busy, and one that allows more is not the recorder's to make first.
    /// </summary>
    [TestCase(1, typeof(IOException))]
    [TestCase(4, typeof(UnauthorizedAccessException))]
    public void A_pipe_name_taken_by_another_program_stops_the_recorder(int instances, Type refusal)
    {
        using var squatter = new NamedPipeServerStream(Pipe, PipeDirection.InOut, instances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var failure = Assert.Catch(() => _server.Start(Pipe, _run));

        Assert.That(failure, Is.InstanceOf(refusal));
        Assert.That(_serverLog.Problems, Has.Some.Contains(Pipe));
        Assert.That(_server.Windows, Is.Zero);
    }

    /// <summary>The pipe is for this user only: its one access rule allows this user, and nobody else.</summary>
    [Test]
    public async Task The_pipe_is_for_this_user_only()
    {
        _server.Start(Pipe, _run);
        var pipe = RawClient();
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), Token);

        var rules = pipe.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        using var me = WindowsIdentity.GetCurrent();

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.That(rules[0].IdentityReference, Is.EqualTo(me.Owner));
        Assert.That(rules[0].AccessControlType, Is.EqualTo(AccessControlType.Allow));
    }

    /// <summary>
    /// With four windows attached, a fifth waits: its pipe has no free instance until one of the four leaves, and then it
    /// connects at once. Meanwhile the recorder waits quietly for a place to come free, rather than try to make a fifth
    /// instance, fail, and say so in the log every window shows.
    /// </summary>
    [Test]
    public async Task A_fifth_window_waits_until_one_of_four_leaves()
    {
        _server.Start(Pipe, _run);
        List<RecorderClient> four = [];
        for (var n = 0; n < LinkProtocol.MaxWindows; n++)
        {
            var client = NewClient();
            await client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token);
            four.Add(client);
        }

        await Wait.UntilAsync(() => _server.Windows == 4);
        var fifth = RawClient();
        Assert.ThrowsAsync<TimeoutException>(() => fifth.ConnectAsync(TimeSpan.FromSeconds(1), Token));
        Assert.That(_serverLog.Problems, Is.Empty, "the recorder waited quietly while four windows were attached");

        await four[0].ByeAsync(ByeReasons.Esc);
        var sinceTheLeave = Stopwatch.StartNew();
        await fifth.ConnectAsync(TimeSpan.FromSeconds(5), Token);

        Assert.That(fifth.IsConnected, Is.True);
        Assert.That(sinceTheLeave.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)), "the fifth window got the place that came free at once");
        Assert.That(_serverLog.Problems, Is.Empty);
    }

    /// <summary>
    /// What the recorder writes to a window does not wait for the window to read it, up to the pipe's buffer: a window that
    /// said hello and has read nothing yet is sent its welcome, the log and the run's end all the same, so the end reaches
    /// a window that is busy, and the recorder knows it went. Without a buffer, each write would wait for the window to read.
    /// </summary>
    [Test]
    public async Task What_the_recorder_writes_does_not_wait_for_the_window_to_read_it()
    {
        const int lines = 40;
        for (var n = 1; n <= lines; n++) _log.Write(LogLevel.Information, $"line {n} {new string('x', 200)}");
        _server.Start(Pipe, _run);
        var pipe = RawClient();
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), Token);
        var window = new LineChannel(pipe);
        await window.SendAsync(LinkTypes.Hello, TestWindow.Hello(owner: true), LinkJson.Default.Hello, Token);
        await Wait.UntilAsync(() => _server.Windows == 1);

        var outcome = new RunOutcome(OutcomeKind.Recorded, 0, "Saved the recording", null, @"D:\Classes\Kalam", null);
        var endWent = await _server.EndAsync(outcome, open: true, TimeSpan.FromSeconds(1));
        List<string> types = [];
        while (await window.ReceiveAsync(Token) is { } message)
        {
            types.Add(message.Type);
            if (message.Type == LinkTypes.End) break;
        }

        string[] expected = [LinkTypes.Welcome, .. Enumerable.Repeat(LinkTypes.Log, lines), LinkTypes.End];
        Assert.That(endWent, Is.True, "the end went to the window before it read anything");
        Assert.That(types, Is.EqualTo(expected));
    }

    /// <summary>
    /// When the pipe cannot be made for the next window, the recorder says so in plain words, waits a second, then two
    /// seconds, doubling, and listens again once it can: a window that comes then is shown the run. The recording never
    /// depends on its pipe.
    /// </summary>
    [Test]
    public async Task When_the_pipe_cannot_be_made_the_recorder_waits_and_tries_again()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var serverLog = new RecordingLogger<RecorderServer>();
        var made = 0;
        await using var server = new RecorderServer(_log, time, serverLog)
        {
            // The first instance is made; the next two tries fail, as they do when every instance of a pipe is busy.
            MakePipe = (name, first) => Interlocked.Increment(ref made) is 2 or 3
                ? throw new IOException("All pipe instances are busy.")
                : RecorderServer.NewPipe(name, first)
        };
        server.Start(Pipe, _run);

        await NewClient().ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token);
        await Wait.UntilAsync(() => serverLog.Problems.Count == 1);
        var firstPause = await time.AdvanceUntilAsync(() => serverLog.Problems.Count == 2, TimeSpan.FromMilliseconds(100), atMost: TimeSpan.FromSeconds(5));
        var secondPause = await time.AdvanceUntilAsync(() => Volatile.Read(ref made) == 4, TimeSpan.FromMilliseconds(100), atMost: TimeSpan.FromSeconds(5));
        await NewClient().ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token);

        Assert.That(serverLog.Problems[0], Does.EndWith($"on the pipe {Pipe}; trying again in 1 second"));
        Assert.That(serverLog.Problems[1], Does.EndWith($"on the pipe {Pipe}; trying again in 2 seconds"));
        Assert.That(firstPause, Is.InRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5)));
        Assert.That(secondPause, Is.InRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5)));
        Assert.That(server.Windows, Is.EqualTo(2));
    }

    /// <summary>When the server stops, a window's pipe ends, and the pipe is gone: opening it again finds nothing.</summary>
    [Test]
    public async Task When_the_server_stops_the_pipe_ends()
    {
        _server.Start(Pipe, _run);
        var pipe = RawClient();
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), Token);

        await _server.DisposeAsync();
        var read = await pipe.ReadAsync(new byte[16], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(read, Is.Zero);
        Assert.ThrowsAsync<TimeoutException>(() => RawClient().ConnectAsync(TimeSpan.FromMilliseconds(300), Token));
    }

    /// <summary>Opening a pipe that is not there times out, which the client takes as a recorder that has not made its pipe yet.</summary>
    [Test]
    public void Opening_a_pipe_that_is_not_there_times_out()
    {
        Assert.ThrowsAsync<TimeoutException>(() => RecorderClient.OpenRealPipeAsync(Pipe, TimeSpan.FromMilliseconds(200), Token));
    }
}
