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

    /// <summary>
    /// The glossary's row (architecture 8.2, item 2): ElevenLabs:KeytermsFile went from keyterms.txt, shipped beside the
    /// program, to empty, no glossary, as none is shipped. A file holding keyterms.txt, in any case, holds the older default,
    /// and the newer one is shown as ""; a glossary of one's own, or none, holds no older default.
    /// </summary>
    [Test]
    public void The_glossarys_row_points_out_keyterms_txt()
    {
        Assert.That(DefaultChanges.Rows, Has.Member(new DefaultChange("ElevenLabs:KeytermsFile", "keyterms.txt", "", "")));
        Assert.That(new ElevenLabsOptions().KeytermsFile, Is.Empty, "the code's default is the row's new one");

        var held = DefaultChanges.HeldIn(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ElevenLabs:KeytermsFile"] = " Keyterms.txt " });

        Assert.That(held.Single().Key, Is.EqualTo("ElevenLabs:KeytermsFile"));
        Assert.That(DefaultChanges.Describe(held[0]), Is.EqualTo("newer default: \"\""));
        Assert.That(DefaultChanges.HeldIn(new Dictionary<string, string?> { ["ElevenLabs:KeytermsFile"] = @"%APPDATA%\ClassScribe\keyterms-islamic-sciences.txt" }), Is.Empty);
        Assert.That(DefaultChanges.HeldIn(new Dictionary<string, string?> { ["ElevenLabs:KeytermsFile"] = "" }), Is.Empty);
    }

    /// <summary>
    /// The vendors' defaults as they first shipped in the settings, as the settings file writes them: the addresses, the API
    /// versions, the model, the prices and ElevenLabs' limits (architecture 7.1). A release that moves one adds a row.
    /// </summary>
    private static readonly Dictionary<string, string> VendorDefaultsFirstShipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ElevenLabs:BaseUrl"] = "https://api.elevenlabs.io/",
        ["ElevenLabs:ApiVersion"] = "v1",
        ["ElevenLabs:ModelId"] = "scribe_v2",
        ["ElevenLabs:MaxKeyterms"] = "1000",
        ["ElevenLabs:KeytermCharacterLimit"] = "50",
        ["ElevenLabs:MaxKeytermWords"] = "5",
        ["ElevenLabs:LongestFileHours"] = "10",
        ["ElevenLabs:PricePerHourUsd"] = "0.22",
        ["ElevenLabs:KeytermsPricePerHourUsd"] = "0.05",
        ["Drive:ApiBaseUrl"] = "https://www.googleapis.com/",
        ["Drive:ApiVersion"] = "v3",
        ["Drive:OAuthBaseUrl"] = "https://oauth2.googleapis.com/",
        ["Drive:AuthorizationUrl"] = "https://accounts.google.com/o/oauth2/v2/auth",
        ["YouTube:OEmbedBaseUrl"] = "https://www.youtube.com/"
    };

    /// <summary>
    /// A vendor's default that moves needs a row (architecture 7.1, the release cut's checklist): the user's file is a full
    /// copy that keeps the old value, so without a row nothing would tell him that ElevenLabs or Google moved on. Each vendor
    /// value's default in code is still the one it first shipped with, or the latest row of that setting names it as the newer
    /// default. When this fails, add the row to <see cref="DefaultChanges.Rows"/> (its version empty on a feature branch).
    /// </summary>
    [Test]
    public void A_vendors_default_that_moves_needs_a_row()
    {
        foreach (var (name, first) in VendorDefaultsFirstShipped)
        {
            var now = SettingsCatalog.Show(SettingsCatalog.Find(name)?.Default);
            var row = DefaultChanges.Rows.LastOrDefault(change => change.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            Assert.That(now == first || row?.NewDefault == now, Is.True,
                $"{name}'s default is {now}, not the {first} it first shipped with, and no row of DefaultChanges names it: a vendor's default that moves needs a row");
        }
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
