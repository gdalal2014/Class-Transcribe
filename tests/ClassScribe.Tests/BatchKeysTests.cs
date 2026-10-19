using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// The keys of a YouTube batch (design 3.9), on the steady clock of a test: Q stops after the video or transcript in hand;
/// a second Q within 5 seconds stops at once, and one later than that only says again to stop after the one in hand.
/// </summary>
[TestFixture]
public class BatchKeysTests
{
    private FakeConsoleUi _ui = null!;
    private ManualTimeProvider _time = null!;

    /// <summary>A console to type into and a clock the test moves.</summary>
    [SetUp]
    public void SetUp()
    {
        _ui = new FakeConsoleUi();
        _time = new ManualTimeProvider(TestRuns.Start);
    }

    /// <summary>Presses Q, and waits until the keys have taken it and gone back to reading the next.</summary>
    private async Task PressQAsync()
    {
        var reads = _ui.KeyReads;
        _ui.Type('q');
        await Wait.UntilAsync(() => _ui.KeyReads > reads, timeoutSeconds: 10);
    }

    /// <summary>A second Q 4.9 seconds after the first stops at once.</summary>
    [Test]
    public async Task A_second_Q_within_5_seconds_stops_at_once()
    {
        using var keys = new BatchKeys(_ui, _time, new RecordingLogger<BatchKeysTests>(), CancellationToken.None);
        await Wait.UntilAsync(() => _ui.KeyReads == 1, timeoutSeconds: 10);

        await PressQAsync();
        Assert.That(keys.StopAfterThis, Is.True);
        _time.Advance(TimeSpan.FromSeconds(4.9));
        _ui.Type('q');

        await Wait.UntilAsync(() => keys.Token.IsCancellationRequested, timeoutSeconds: 10);
    }

    /// <summary>A second Q 5.1 seconds after the first is a first again: it says to stop after the one in hand, and stops nothing at once.</summary>
    [Test]
    public async Task A_second_Q_after_5_seconds_only_stops_after_the_one_in_hand()
    {
        using var keys = new BatchKeys(_ui, _time, new RecordingLogger<BatchKeysTests>(), CancellationToken.None);
        await Wait.UntilAsync(() => _ui.KeyReads == 1, timeoutSeconds: 10);

        await PressQAsync();
        _time.Advance(TimeSpan.FromSeconds(5.1));
        await PressQAsync();

        Assert.That(keys.Token.IsCancellationRequested, Is.False);
        Assert.That(keys.StopAfterThis, Is.True);
    }
}
