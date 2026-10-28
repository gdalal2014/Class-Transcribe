using System.Text.Json;
using ClassScribe.Commands;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// Where a run in a recorder publishes its states: each goes to the windows that show the run, and the last one is sent
/// again once a second while no new one comes, so that a long upload never looks like a recorder that stopped answering.
/// Once the run is over nothing is sent again: the end follows within moments. A window is played over an in-memory
/// connection to a real server, on a clock moved by hand.
/// </summary>
[TestFixture]
public class LinkStatusSinkTests
{
    /// <summary>The run the states are of.</summary>
    private static readonly string RunId = TestRuns.Id();

    private ManualTimeProvider _time = null!;
    private RecorderLog _log = null!;
    private RecorderServer _server = null!;
    private UnattendedAlerts _alerts = null!;
    private LinkStatusSink _sink = null!;
    private TestWindow _window = null!;

    /// <summary>A server for the run with one window attached, the alerts for it, and the sink over both, on a clock moved by hand.</summary>
    [SetUp]
    public async Task SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _log = new RecorderLog(_time);
        _server = new RecorderServer(_log, _time, new RecordingLogger<RecorderServer>());
        _server.Begin(() => TestRuns.Record(RunId, RunState.Recording));
        _alerts = new UnattendedAlerts(_server, new FakeProcessStarter(_time), TestOptions.Create(new RecordingOptions()), _time, new RecordingLogger<UnattendedAlerts>());
        _alerts.Start(RunId);
        _sink = new LinkStatusSink(_server, _alerts, _time, new RecordingLogger<LinkStatusSink>());
        (_window, _) = await TestWindow.AttachAsync(_server);
    }

    /// <summary>Closes the window, the sink, the alerts, the server and the log.</summary>
    [TearDown]
    public async Task TearDown()
    {
        await _window.DisposeAsync();
        _sink.Dispose();
        _alerts.Dispose();
        await _server.DisposeAsync();
        _log.Dispose();
    }

    /// <summary>A state of the run in <paramref name="phase"/>.</summary>
    private static RecorderState State(RecorderPhase phase) =>
        new(phase, "Kalam", TimeSpan.FromMinutes(1), "Speakers", null, null, null, null, Attention.None, InWindow: false);

    /// <summary>Reads what the recorder sends until a state comes, and returns its phase.</summary>
    private async Task<RecorderPhase> NextStateAsync()
    {
        while (true)
        {
            var envelope = await _window.NextAsync() ?? throw new AssertionException("The recorder let the window go before a state came.");
            if (envelope.Type == LinkTypes.State) return envelope.Body.Deserialize(LinkJson.Default.StateUpdate)!.State.Phase;
        }
    }

    /// <summary>While the run goes on, the last state is sent to the windows again once a second while no new one comes.</summary>
    [Test]
    public async Task The_last_state_is_sent_again_once_a_second_while_no_new_one_comes()
    {
        _sink.Publish(State(RecorderPhase.Transcribing));
        Assert.That(await NextStateAsync(), Is.EqualTo(RecorderPhase.Transcribing), "the state itself");

        for (var second = 0; second < 3; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            Assert.That(await NextStateAsync(), Is.EqualTo(RecorderPhase.Transcribing), $"sent again, second {second + 1}");
        }
    }

    /// <summary>
    /// A state that says the run is over, done, failed or cancelled, is sent once and never again: the end follows within
    /// moments, and a failure sent again after the windows have taken the end would count as a new one.
    /// </summary>
    [TestCase(RecorderPhase.Done)]
    [TestCase(RecorderPhase.Failed)]
    [TestCase(RecorderPhase.Cancelled)]
    public async Task A_state_that_ends_the_run_is_not_sent_again(RecorderPhase end)
    {
        _sink.Publish(State(RecorderPhase.Transcribing));
        Assert.That(await NextStateAsync(), Is.EqualTo(RecorderPhase.Transcribing));

        _sink.Publish(State(end));
        Assert.That(await NextStateAsync(), Is.EqualTo(end), "the end state itself");
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));
        _log.Write(LogLevel.Information, "a line after the end");

        Assert.That(_time.ActiveTimers, Is.Zero, "nothing is left to send the state again");
        var next = await _window.NextAsync();
        Assert.That(next?.Type, Is.EqualTo(LinkTypes.Log), "the line comes next, with no state sent again before it");
    }
}
