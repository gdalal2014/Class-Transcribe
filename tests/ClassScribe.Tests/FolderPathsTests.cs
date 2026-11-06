using System.Globalization;
using ClassScribe.Application.Settings;
using ClassScribe.Core.Settings;
using ClassScribe.Infrastructure.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The rule for a recordings folder longer than any path (<see cref="FolderPaths"/>) and the way a line says a value too long
/// to read in it (<see cref="LongValues"/>, which a recording's drop line uses too).
/// </summary>
public class FolderPathsTests
{
    /// <summary>
    /// A value rooted but not fully qualified that Windows refuses is measured as Windows would join it: \folder under the
    /// base's root alone (a drive or a share), X:folder under the base when the base is on drive X (in either letter case),
    /// else under X:\. The full path given is the one Path.GetFullPath would have made, not the base folder put in front.
    /// </summary>
    [TestCase(@"\", @"C:\work", 32_768)]
    [TestCase(@"\", @"\\server\share\class", 32_780)]
    [TestCase("C:", @"C:\work", 32_773)]
    [TestCase("c:", @"C:\work", 32_773)]
    [TestCase("D:", @"C:\work", 32_768)]
    public void A_rooted_value_Windows_refuses_is_measured_as_Windows_joins_it(string root, string baseFolder, int expected)
    {
        var value = root + new string('r', 32_765);

        var words = FolderPaths.TooLong(value, baseFolder);

        Assert.That(words, Does.StartWith(string.Create(CultureInfo.InvariantCulture,
            $"names a folder whose full path is {expected:N0} characters long, starting \"{value[..40]}...\";")), words);
    }

    /// <summary>
    /// A value too long to quote is said by its length and its first 40 characters; any ordinary path, a deep one under
    /// WinGet's packages included, is quoted whole, up to 1,024 characters.
    /// </summary>
    [Test]
    public void A_line_quotes_any_ordinary_path_whole_and_says_a_longer_value_by_its_length_and_start()
    {
        var winget = @"C:\Users\gdala\AppData\Local\Microsoft\WinGet\Packages\yt-dlp.yt-dlp_Microsoft.Winget.Source_8wekyb3d8bbwe\yt-dlp.exe";

        Assert.Multiple(() =>
        {
            Assert.That(LongValues.TooLongToQuote(winget), Is.False);
            Assert.That(LongValues.TooLongToQuote(new string('a', 1_024)), Is.False);
            Assert.That(LongValues.TooLongToQuote(new string('a', 1_025)), Is.True);
            Assert.That(LongValues.TooLongToQuote(null), Is.False);
            Assert.That(LongValues.Described(new string('a', 1_025)), Is.EqualTo($"a value 1,025 characters long, starting \"{new string('a', 40)}...\""));
            Assert.That(LongValues.Start("short"), Is.EqualTo("short"));
        });
    }

    /// <summary>
    /// A recording's line for a refused setting quotes a path past 80 characters whole, as before the folder's refusal, so
    /// that the file name at its end is still there; only a value too long to read in a line is shortened.
    /// </summary>
    [Test]
    public void The_drop_line_quotes_a_deep_path_whole()
    {
        var winget = @"C:\Users\gdala\AppData\Local\Microsoft\WinGet\Packages\yt-dlp.yt-dlp_Microsoft.Winget.Source_8wekyb3d8bbwe\yt-dlp.exe";
        var tooLong = new string('a', 1_025);

        Assert.Multiple(() =>
        {
            Assert.That(new DroppedSetting("YouTube:YtDlpPath", winget, "the default").Describe("the file"),
                Is.EqualTo($"YouTube:YtDlpPath is \"{winget}\" in the file, which the check refuses; this recording uses the default in its place."));
            Assert.That(new DroppedSetting("Recording:OutputFolder", tooLong, "the default").Describe("the file"),
                Is.EqualTo($"Recording:OutputFolder is a value 1,025 characters long, starting \"{new string('a', 40)}...\", in the file, which the check refuses; this recording uses the default in its place."));
        });
    }
}
