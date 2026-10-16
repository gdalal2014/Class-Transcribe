using ClassScribe.Commands;

namespace ClassScribe.Tests;

/// <summary>When and how a window ClassScribe opened for itself waits before it closes.</summary>
[TestFixture]
public class ClosePromptTests
{
    /// <summary>
    /// The window waits when it is ClassScribe's own, someone can type, and there is something to read:
    /// the result of a command given on the command line (a dropped file), or any failure, including
    /// settings that stopped the menu from starting. The menu left with Q has nothing more to show.
    /// </summary>
    [TestCase(new[] { "lecture.mp4" }, 0, true, true, ExpectedResult = true)]
    [TestCase(new[] { "lecture.mp4" }, 1, true, true, ExpectedResult = true)]
    [TestCase(new string[0], 1, true, true, ExpectedResult = true)]
    [TestCase(new string[0], 0, true, true, ExpectedResult = false)]
    [TestCase(new[] { "lecture.mp4" }, 1, true, false, ExpectedResult = false)]
    [TestCase(new[] { "lecture.mp4" }, 1, false, true, ExpectedResult = false)]
    public bool The_window_waits_when_there_is_something_to_read(string[] args, int exitCode, bool interactive, bool ownsWindow) =>
        ClosePrompt.IsNeeded(args, exitCode, interactive, ownsWindow);

    /// <summary>
    /// An Enter pressed during the upload, to check that the window was alive, does not answer the
    /// question that comes later: the window stays open until Enter is pressed after it.
    /// </summary>
    [Test]
    public async Task An_Enter_typed_before_the_question_does_not_close_the_window()
    {
        var ui = new FakeConsoleUi();
        ui.TypeAhead('\r');

        var waiting = new ClosePrompt(ui).WaitAsync();

        Assert.That(waiting.IsCompleted, Is.False, "still waiting");
        Assert.That(ui.Screen, Does.Contain("Press Enter to close."));
        ui.Type('\r');
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Other keys leave the window open; Esc and Ctrl+C close it as Enter does.</summary>
    [TestCase('\u001b')]
    [TestCase('\u0003')]
    public async Task Esc_and_Ctrl_C_close_the_window_too(char closing)
    {
        var ui = new FakeConsoleUi();
        var waiting = new ClosePrompt(ui).WaitAsync();

        ui.Type('x');
        await Task.Delay(50);
        Assert.That(waiting.IsCompleted, Is.False, "a stray key does not close it");
        ui.Type(closing);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
