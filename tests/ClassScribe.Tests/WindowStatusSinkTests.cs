using System.Text.Json;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// A run recording in this window shows each state it publishes on the window: the title, the taskbar button, and a
/// flash for each new Alert, not for each state that repeats it, and never for a Notice (design 1.13 and section 4).
/// </summary>
[TestFixture]
public class WindowStatusSinkTests
{
    private FakeWindowSignals _window = null!;
    private WindowStatusSink _sink = null!;

    /// <summary>Creates a window to look at and the sink under test.</summary>
    [SetUp]
    public void SetUp()
    {
        _window = new FakeWindowSignals();
        _sink = new WindowStatusSink(_window);
    }

    /// <summary>A state of a run of "Kalam" recording in this window, an hour, 12 minutes and 40 seconds in.</summary>
    private static RecorderState State(RecorderPhase phase = RecorderPhase.Recording, Attention? attention = null, string name = "Kalam") =>
        new(phase, name, new TimeSpan(1, 12, 40), "chrome", null, null, null, null, attention ?? Attention.None, InWindow: true);

    /// <summary>Attention at <paramref name="level"/>, the <paramref name="count"/>th thing to need it in the run.</summary>
    private static Attention AttentionAt(AttentionLevel level, int count) =>
        level == AttentionLevel.None
            ? new(AttentionLevel.None, "", "", count)
            : new(level, level == AttentionLevel.Alert ? "CANNOT WRITE" : "no sound 2 min", "Words.", count);

    /// <summary>Each state sets the title and the taskbar button as <see cref="TitleText"/> gives them.</summary>
    [Test]
    public void Each_state_sets_the_title_and_the_taskbar()
    {
        _sink.Publish(State());
        Assert.That(_window.Title, Is.EqualTo("REC 01:12:40 Kalam (in this window) | ClassScribe"));
        Assert.That(_window.Taskbar, Is.EqualTo((TaskbarState.Normal, 100)));

        _sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 1)));
        Assert.That(_window.Title, Is.EqualTo("! CANNOT WRITE Kalam (in this window) | ClassScribe"));
        Assert.That(_window.Taskbar, Is.EqualTo((TaskbarState.Error, 100)));

        _sink.Publish(State(RecorderPhase.Done, AttentionAt(AttentionLevel.None, 1)));
        Assert.That(_window.Title, Is.EqualTo("DONE Kalam (in this window) | ClassScribe"));
        Assert.That(_window.Taskbar, Is.EqualTo((TaskbarState.None, 0)));
    }

    /// <summary>The window flashes once for each new Alert, and not again for the states that repeat it once a second.</summary>
    [Test]
    public void The_window_flashes_once_per_new_alert_not_per_state()
    {
        _sink.Publish(State());
        Assert.That(_window.Flashes, Is.Zero, "nothing needs attention yet");

        for (var second = 0; second < 5; second++) _sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 1)));
        Assert.That(_window.Flashes, Is.EqualTo(1), "one flash for the first Alert, however many states repeat it");

        _sink.Publish(State(attention: AttentionAt(AttentionLevel.None, 1)));
        for (var second = 0; second < 5; second++) _sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 2)));
        Assert.That(_window.Flashes, Is.EqualTo(2), "one more for the next Alert");
    }

    /// <summary>
    /// A Notice never flashes the window, however many come: it is worth a look, not a call to come at once, so the title
    /// notes it and the taskbar button turns yellow (design section 4). A class falls silent at every break, and that
    /// must not flash the window each time (4.1, row 8).
    /// </summary>
    [Test]
    public void A_notice_never_flashes()
    {
        for (var count = 1; count <= 5; count++)
        {
            _sink.Publish(State(attention: AttentionAt(AttentionLevel.Notice, count)));
            _sink.Publish(State(attention: AttentionAt(AttentionLevel.None, count)));
        }

        _sink.Publish(State(attention: AttentionAt(AttentionLevel.Notice, 6)));

        Assert.That(_window.Flashes, Is.Zero);
        Assert.That(_window.Title, Is.EqualTo("REC 01:12:40 Kalam (no sound 2 min) (in this window) | ClassScribe"));
        Assert.That(_window.Taskbar, Is.EqualTo((TaskbarState.Paused, 100)));
    }

    /// <summary>
    /// A Notice that becomes an Alert, such as a pause left on for 10 minutes, flashes once as it becomes one, whether or
    /// not whoever raised it counted it as a new event; an Alert that comes back with the count it had does not flash
    /// again; and a new event that is an Alert flashes.
    /// </summary>
    [Test]
    public void A_notice_that_becomes_an_alert_flashes_once_as_it_does()
    {
        for (var second = 0; second < 3; second++) _sink.Publish(State(attention: AttentionAt(AttentionLevel.Notice, 1)));
        Assert.That(_window.Flashes, Is.Zero, "a Notice");

        for (var second = 0; second < 3; second++) _sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 1)));
        Assert.That(_window.Flashes, Is.EqualTo(1), "the same event, now an Alert");

        _sink.Publish(State(attention: AttentionAt(AttentionLevel.None, 1)));
        _sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 1)));
        Assert.That(_window.Flashes, Is.EqualTo(1), "the same Alert back again");

        _sink.Publish(State(attention: AttentionAt(AttentionLevel.Notice, 2)));
        _sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 3)));
        Assert.That(_window.Flashes, Is.EqualTo(2), "a new event that is an Alert");
    }

    /// <summary>
    /// A window that starts showing a run part way through does not flash for what needed attention before it came, and
    /// is over now; it flashes once for an Alert that is on as it comes.
    /// </summary>
    [Test]
    public void A_window_that_comes_later_flashes_only_for_what_needs_attention_now()
    {
        _sink.Publish(State(attention: AttentionAt(AttentionLevel.None, 3)));
        _sink.Publish(State(attention: AttentionAt(AttentionLevel.None, 3)));
        Assert.That(_window.Flashes, Is.Zero, "three things needed attention before, and none does now");

        var later = new FakeWindowSignals();
        var sink = new WindowStatusSink(later);
        sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 3)));
        sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 3)));
        Assert.That(later.Flashes, Is.EqualTo(1), "the Alert needs attention now");
    }

    /// <summary>The next run in the same window counts from nothing again, so its first Alert flashes too.</summary>
    [Test]
    public void The_next_run_in_the_same_window_flashes_for_its_own_first_alert()
    {
        _sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 3)));
        _sink.Publish(State(RecorderPhase.Done, AttentionAt(AttentionLevel.None, 3)));

        _sink.Publish(State(RecorderPhase.Starting, name: "Fiqh"));
        _sink.Publish(State(attention: AttentionAt(AttentionLevel.Alert, 1), name: "Fiqh"));

        Assert.That(_window.Flashes, Is.EqualTo(2));
    }

    /// <summary>
    /// A run that fails flashes the window once as it fails, even when nothing else marked the failure as needing
    /// attention: a failure always needs a look, as the red taskbar button says.
    /// </summary>
    [Test]
    public void A_run_that_fails_flashes_once()
    {
        _sink.Publish(State(RecorderPhase.Uploading));
        _sink.Publish(State(RecorderPhase.Failed));
        _sink.Publish(State(RecorderPhase.Failed));

        Assert.That(_window.Flashes, Is.EqualTo(1));
        Assert.That(_window.Title, Is.EqualTo("! FAILED Kalam (in this window) | ClassScribe"));
    }

    /// <summary>
    /// A state read from JSON without its attention, as the recorder's JSON reads a missing field, is shown as one with
    /// nothing that needs attention: no exception, and no flash.
    /// </summary>
    [Test]
    public void A_state_read_without_its_attention_is_shown_without_a_flash()
    {
        var state = JsonSerializer.Deserialize("""{"phase":"Recording","name":"Kalam","position":"01:12:40","inWindow":true}""", RecorderJson.Default.RecorderState)!;

        Assert.DoesNotThrow(() => _sink.Publish(state));

        Assert.That(_window.Title, Is.EqualTo("REC 01:12:40 Kalam (in this window) | ClassScribe"));
        Assert.That(_window.Flashes, Is.Zero);
    }

    /// <summary>A failure that is also raised as a new Alert flashes once, not twice.</summary>
    [Test]
    public void A_failure_counted_as_a_new_attention_flashes_once()
    {
        _sink.Publish(State(RecorderPhase.Uploading));
        _sink.Publish(State(RecorderPhase.Failed, AttentionAt(AttentionLevel.Alert, 1)));

        Assert.That(_window.Flashes, Is.EqualTo(1));
    }
}
