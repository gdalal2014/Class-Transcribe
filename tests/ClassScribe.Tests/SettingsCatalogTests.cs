using System.Text.Json.Nodes;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Tests;

/// <summary>
/// Every setting the user's file holds, reflected from the options' types: what the writer adds, sets and resets, and
/// what settings list shows.
/// </summary>
[TestFixture]
public class SettingsCatalogTests
{
    /// <summary>
    /// The catalog holds every setting of the six sections in the options' order, never ElevenLabs:ApiKey and never a
    /// property without a setter, and the Logging section's log levels as a map with their defaults.
    /// </summary>
    [Test]
    public void The_catalog_holds_every_setting_but_the_key()
    {
        var names = SettingsCatalog.Keys.Select(key => key.Name).ToList();

        Assert.That(names, Does.Not.Contain("ElevenLabs:ApiKey"));
        Assert.That(names, Does.Not.Contain("ElevenLabs:Timeout"));
        Assert.That(names.Take(3), Is.EqualTo(new[] { "Recording:OutputFolder", "Recording:Device", "Recording:App" }));
        Assert.That(names, Has.Member("YouTube:MayDownloadChannels"));
        Assert.That(SettingsCatalog.Keys.Select(key => key.Section).Distinct(), Is.EqualTo(SettingsCatalog.Sections));
        var levels = SettingsCatalog.Find("logging:loglevel")!;
        Assert.That((levels.Kind, levels.EntryKind), Is.EqualTo((SettingKind.Map, (SettingKind?)SettingKind.Choice)));
        Assert.That(((JsonObject)levels.Default!).Select(entry => entry.Key), Is.EqualTo(new[] { "Default", "Microsoft", "System" }));
        Assert.That(SettingsCatalog.Find("Recording:KeepScreenOn")!.ChoiceType, Is.EqualTo(typeof(KeepScreenOn)));
    }

    /// <summary>
    /// The shipped appsettings.json documents every setting of the catalog, so that the file beside the program shows every
    /// default, and holds no setting the catalog does not know.
    /// </summary>
    [Test]
    public void The_shipped_file_holds_every_setting()
    {
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, SettingsFile.FileName)).Build();

        foreach (var key in SettingsCatalog.Keys)
            Assert.That(shipped.GetSection(key.Name).Exists() || key.Kind == SettingKind.List && shipped[key.Name] is not null, Is.True, key.Name);
    }

    /// <summary>A map's entry is found by its key path, in any case, and a name that is no entry is not.</summary>
    [Test]
    public void A_maps_entry_is_found_by_its_key_path()
    {
        Assert.That(SettingsCatalog.FindEntry("logging:loglevel:ClassScribe.Drive") is { Map.Name: "Logging:LogLevel", Entry: "ClassScribe.Drive" });
        Assert.That(SettingsCatalog.FindEntry("Logging:LogLevel"), Is.Null);
        Assert.That(SettingsCatalog.FindEntry("Logging:LogLevel:A:B"), Is.Null);
        Assert.That(SettingsCatalog.FindEntry("Recording:FlushSeconds:Thing"), Is.Null);
    }

    /// <summary>A near miss gets the setting meant, by its letters or by its last part; a name far from any gets none.</summary>
    [TestCase("Recording:FlushSecond", "Recording:FlushSeconds")]
    [TestCase("Recordings:OutputFolder", "Recording:OutputFolder")]
    [TestCase("OutputFolder", "Recording:OutputFolder")]
    [TestCase("Drive:Folder", null)]
    [TestCase("Nothing:Like:This", null)]
    public void A_near_miss_gets_the_setting_meant(string given, string? meant)
    {
        Assert.That(SettingsCatalog.DidYouMean(given), Is.EqualTo(meant));
    }

    /// <summary>A value typed on the command line is read as the setting's kind, or refused with the rule in words.</summary>
    [TestCase(SettingKind.WholeNumber, null, "60", "60", null)]
    [TestCase(SettingKind.WholeNumber, null, "1.5", null, "takes a whole number, such as 60")]
    [TestCase(SettingKind.Number, null, "1.5", "1.5", null)]
    [TestCase(SettingKind.Number, null, "1,5", null, "takes a number written with a dot, such as 1.5")]
    [TestCase(SettingKind.YesNo, null, "FALSE", "false", null)]
    [TestCase(SettingKind.YesNo, null, "yes", null, "takes true or false")]
    [TestCase(SettingKind.Choice, typeof(KeepScreenOn), "never", "\"Never\"", null)]
    [TestCase(SettingKind.Text, null, "D:\\Classes", "\"D:\\\\Classes\"", null)]
    public void A_typed_value_is_read_as_its_kind(SettingKind kind, Type? choice, string text, string? json, string? problem)
    {
        var (value, why) = SettingsCatalog.Read(kind, choice, text);

        Assert.That((value?.ToJsonString(), why), Is.EqualTo((json, problem)));
    }
}
