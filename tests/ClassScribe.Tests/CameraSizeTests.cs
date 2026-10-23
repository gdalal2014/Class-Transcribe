using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// The picture's size (amendment 39): the one parser the setting, the command line and the menu share takes the three
/// sizes in any case and a typed size within its limits, and refuses everything else.
/// </summary>
[TestFixture]
public class CameraSizeTests
{
    /// <summary>The three sizes are taken in any case, with spaces around them, and keep their words.</summary>
    [TestCase("480p", 848, 480, "480p")]
    [TestCase("720P", 1280, 720, "720p")]
    [TestCase(" 1080p ", 1920, 1080, "1080p")]
    public void The_three_sizes_are_taken_in_any_case(string text, int width, int height, string written)
    {
        Assert.That(CameraSize.TryParse(text, out var size), Is.True);
        Assert.That(size, Is.EqualTo(new CameraSize(width, height)));
        Assert.That(size!.Text, Is.EqualTo(written));
    }

    /// <summary>A typed size is taken with x or X, written back as typed, and one equal to a preset as its word.</summary>
    [TestCase("1024x576", 1024, 576, "1024x576")]
    [TestCase("640X480", 640, 480, "640x480")]
    [TestCase("1280x720", 1280, 720, "720p")]
    [TestCase("320x240", 320, 240, "320x240")]
    [TestCase("1920x1080", 1920, 1080, "1080p")]
    [TestCase("1440x1080", 1440, 1080, "1440x1080")]
    public void A_typed_size_within_the_limits_is_taken(string text, int width, int height, string written)
    {
        Assert.That(CameraSize.TryParse(text, out var size), Is.True);
        Assert.That(size, Is.EqualTo(new CameraSize(width, height)));
        Assert.That(size!.ToString(), Is.EqualTo(written));
    }

    /// <summary>Anything else is refused: no size, other words, odd sides, sizes out of range, shapes outside 4:3 to 16:9, other separators and digits.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("720")]
    [TestCase("4k")]
    [TestCase("hd")]
    [TestCase("1025x576")]
    [TestCase("1024x575")]
    [TestCase("318x240")]
    [TestCase("1922x1080")]
    [TestCase("1920x1082")]
    [TestCase("320x238")]
    [TestCase("1080x1080")]
    [TestCase("1920x800")]
    [TestCase("1024*576")]
    [TestCase("1024 x 576")]
    [TestCase("-1024x576")]
    [TestCase("1024x576p")]
    [TestCase("１０２４x576")]
    [TestCase("99999999x576")]
    public void Anything_else_is_refused(string? text)
    {
        Assert.That(CameraSize.TryParse(text, out var size), Is.False);
        Assert.That(size, Is.Null);
    }

    /// <summary>The default is 720p, and the rule in words names the three sizes and every limit.</summary>
    [Test]
    public void The_default_is_720p_and_the_rule_names_every_limit()
    {
        Assert.That(CameraSize.Default, Is.EqualTo(CameraSize.P720));
        Assert.That(CameraSize.Default.Text, Is.EqualTo(CameraSize.DefaultText));
        Assert.That(CameraSize.Rule, Does.Contain("480p, 720p or 1080p").And.Contain("320").And.Contain("1920").And.Contain("240").And.Contain("1080")
            .And.Contain("even").And.Contain("4:3").And.Contain("16:9"));
    }
}
