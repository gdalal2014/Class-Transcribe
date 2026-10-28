namespace ClassScribe.Desktop;

/// <summary>
/// What the window's taskbar button shows: nothing, a bar that fills, a bar that runs back and forth, or a bar coloured
/// yellow or red. Windows Terminal shows the same on its tab.
/// </summary>
public enum TaskbarState
{
    /// <summary>No bar: the button looks as usual.</summary>
    None,

    /// <summary>A green bar, filled as far as the percent says.</summary>
    Normal,

    /// <summary>A bar that runs back and forth, for work whose progress is not known, such as waiting for ElevenLabs.</summary>
    Indeterminate,

    /// <summary>A yellow bar: worth a look, such as a paused recording or no sound for a while.</summary>
    Paused,

    /// <summary>A red bar: needs attention now, such as nothing being recorded, or a failure.</summary>
    Error
}

/// <summary>
/// Shows a run's state on the window ClassScribe runs in, without a sound: its title, its taskbar button, and a flash
/// of that button when something needs attention. Nothing is ever thrown: these are conveniences, and the log says the
/// same in words.
/// </summary>
public interface IWindowSignals
{
    /// <summary>Sets the window's title to <paramref name="title"/>.</summary>
    void SetTitle(string title);

    /// <summary>
    /// Shows <paramref name="state"/> on the taskbar button. <paramref name="percent"/>, from 0 to 100, is how far the bar
    /// is filled: the upload's progress, or 100 for a state that has none, so that its colour fills the button
    /// (<see cref="TitleText.TaskbarPercentFor"/> gives it for a run's state). A yellow or red bar given 0, the default,
    /// is filled all the same, since an empty one would not show its colour. It is ignored for
    /// <see cref="TaskbarState.None"/> and <see cref="TaskbarState.Indeterminate"/>.
    /// </summary>
    void SetTaskbar(TaskbarState state, int percent = 0);

    /// <summary>Flashes the taskbar button until the window comes to the front. It never makes a sound.</summary>
    void Flash();
}

/// <summary>
/// A window without signals: nothing is shown. It stands in on a system other than Windows, where ClassScribe can only
/// transcribe, and wherever a run has no window of its own to show its state on.
/// </summary>
public sealed class NoWindowSignals : IWindowSignals
{
    /// <inheritdoc />
    public void SetTitle(string title)
    {
    }

    /// <inheritdoc />
    public void SetTaskbar(TaskbarState state, int percent = 0)
    {
    }

    /// <inheritdoc />
    public void Flash()
    {
    }
}
