using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// The table of defaults later builds changed (build-plan amendment 38): a full copy of the settings pins every value, so a
/// value equal to an old default is pointed out, never changed.
/// </summary>
[TestFixture]
public class DefaultChangesTests
{
    /// <summary>The first row is Recording:MicrophoneFollows, from chrome to auto (N9), without a version on a feature branch.</summary>
    [Test]
    public void The_first_row_is_the_followed_app_from_chrome_to_auto()
    {
        Assert.That(DefaultChanges.Rows[0], Is.EqualTo(new DefaultChange("Recording:MicrophoneFollows", "chrome", "auto", "")));
        Assert.That(new RecordingOptions().MicrophoneFollows, Is.EqualTo(DefaultChanges.Rows[0].NewDefault), "the code's default is the row's new one");
    }

    /// <summary>
    /// A file's value equal to an old default, in any case and with spaces around it, holds an older default; the newer one,
    /// a choice of another app, a missing key and a value in a section of the same name elsewhere do not.
    /// </summary>
    [TestCase("chrome", true)]
    [TestCase(" Chrome ", true)]
    [TestCase("auto", false)]
    [TestCase("zoom", false)]
    [TestCase(null, false)]
    public void A_value_equal_to_an_old_default_holds_it(string? value, bool held)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Recording:Microphone"] = "chrome" };
        if (value is not null) values["recording:microphonefollows"] = value;

        Assert.That(DefaultChanges.HeldIn(values).Count, Is.EqualTo(held ? 1 : 0));
    }

    /// <summary>With two changes of one setting, a value equal to either old default holds an older default, described by the latest row.</summary>
    [Test]
    public void Two_changes_of_one_setting_are_described_by_the_latest()
    {
        DefaultChange[] rows = [new("Recording:FlushSeconds", "10", "7", "0.5.0"), new("Recording:FlushSeconds", "7", "5", "0.9.0")];

        var held = DefaultChanges.HeldIn(new Dictionary<string, string?> { ["Recording:FlushSeconds"] = "10" }, rows);

        Assert.That(held, Is.EqualTo(new[] { rows[1] }));
        Assert.That(DefaultChanges.Describe(held[0]), Is.EqualTo("newer default: 5 (since 0.9.0)"));
        Assert.That(DefaultChanges.HeldIn(new Dictionary<string, string?> { ["Recording:FlushSeconds"] = "5" }, rows), Is.Empty);
    }

    /// <summary>A row without a version, as on a feature branch, is described without one; the menu's line counts one or several.</summary>
    [Test]
    public void The_words_name_the_newer_default_and_count_them()
    {
        Assert.That(DefaultChanges.Describe(DefaultChanges.Rows[0]), Is.EqualTo("newer default: auto"));
        Assert.That(DefaultChanges.HeaderLine(0), Is.Null);
        Assert.That(DefaultChanges.HeaderLine(1), Is.EqualTo("1 setting holds an older default; settings list shows it"));
        Assert.That(DefaultChanges.HeaderLine(2), Is.EqualTo("2 settings hold an older default; settings list shows them"));
    }
}
