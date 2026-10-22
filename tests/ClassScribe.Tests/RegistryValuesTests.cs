using System.Globalization;
using System.Runtime.Versioning;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// Reading values under HKEY_CURRENT_USER as ClassScribe reads Lenovo Vantage's microphone mode and Windows' ducking
/// setting. Only values every Windows user profile has are read, and nothing is written: a key that does not exist is
/// named with a fresh id, and is never created.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class RegistryValuesTests
{
    /// <summary>A key every user profile has, with the user's environment variables as its values.</summary>
    private const string EnvironmentKey = "Environment";

    /// <summary>Explorer's per-user settings, where Hidden (show hidden files) is a number.</summary>
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private RecordingLogger<RegistryValues> _log = null!;
    private RegistryValues _registry = null!;

    /// <summary>The reader, with a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _log = new RecordingLogger<RegistryValues>();
        _registry = new RegistryValues(_log);
    }

    /// <summary>A key that does not exist reads as null, as a string and as a number, and nothing is logged as a problem.</summary>
    [Test, Category("WindowsRegistry")]
    public void A_missing_key_reads_as_null()
    {
        var key = $@"Software\ClassScribe.Tests\Missing-{Guid.NewGuid():N}";

        Assert.That(_registry.ReadString(key, "noise_micro_mode"), Is.Null);
        Assert.That(_registry.ReadDword(key, "UserDuckingPreference"), Is.Null);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A value that does not exist in a key that does reads as null.</summary>
    [Test, Category("WindowsRegistry")]
    public void A_missing_value_reads_as_null()
    {
        var name = $"ClassScribe-Missing-{Guid.NewGuid():N}";

        Assert.That(_registry.ReadString(EnvironmentKey, name), Is.Null);
        Assert.That(_registry.ReadDword(EnvironmentKey, name), Is.Null);
    }

    /// <summary>A string value reads as its text, and not as a number.</summary>
    [Test, Category("WindowsRegistry")]
    public void A_string_value_reads_as_text_and_not_as_a_number()
    {
        var temp = _registry.ReadString(EnvironmentKey, "TEMP");
        Assume.That(temp, Is.Not.Null, "this user profile has no TEMP in its environment key");

        Assert.That(temp, Is.Not.Empty);
        Assert.That(_registry.ReadDword(EnvironmentKey, "TEMP"), Is.Null);
    }

    /// <summary>A number value reads as a number, and as text gives the same number written out.</summary>
    [Test, Category("WindowsRegistry")]
    public void A_number_value_reads_as_a_number_and_as_its_text()
    {
        var hidden = _registry.ReadDword(ExplorerKey, "Hidden");
        Assume.That(hidden, Is.Not.Null, "this user profile has no Hidden value in Explorer's settings");

        Assert.That(_registry.ReadString(ExplorerKey, "Hidden"), Is.EqualTo(hidden!.Value.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The key's path is taken in any case, as Windows takes it.</summary>
    [Test, Category("WindowsRegistry")]
    public void The_key_is_found_in_any_case()
    {
        var temp = _registry.ReadString(EnvironmentKey, "TEMP");
        Assume.That(temp, Is.Not.Null, "this user profile has no TEMP in its environment key");

        Assert.That(_registry.ReadString(EnvironmentKey.ToUpperInvariant(), "temp"), Is.EqualTo(temp));
    }

    /// <summary>
    /// A value under HKEY_LOCAL_MACHINE that every Windows has reads as its text: the build number, where yt-dlp's tools are
    /// looked for on the machine's PATH beside it. Nothing is written.
    /// </summary>
    [Test, Category("WindowsRegistry")]
    public void A_local_machine_value_reads_as_its_text()
    {
        var build = _registry.ReadLocalMachineString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild");

        Assert.That(build, Does.Match("^[0-9]+$"));
        Assert.That(_registry.ReadLocalMachineString($@"SOFTWARE\ClassScribe.Tests\Missing-{Guid.NewGuid():N}", "Path"), Is.Null);
    }
}
