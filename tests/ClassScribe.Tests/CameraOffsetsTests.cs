using ClassScribe.Camera;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Tests;

/// <summary>
/// Recording:CameraOffsets (teach-video 6.6 and 7): a part of a camera's name to the milliseconds its picture is moved
/// against the sound, the longest part in the camera's name applying; checked when the settings are read (each part 1 to
/// 200 characters without control characters, each figure from -1000 to 1000 and whole); empty in the shipped file; and
/// carried to the recorder in the window's settings snapshot.
/// </summary>
[TestFixture]
public sealed class CameraOffsetsTests
{
    /// <summary>The problems the check finds in <paramref name="settings"/>.</summary>
    private static IReadOnlyList<string> Problems(Dictionary<string, string?> settings) =>
        new SettingsCheck().FindProblems(new ConfigurationBuilder().AddInMemoryCollection(settings).AddApiKeyFromEnvironment("test-key").Build());

    /// <summary>The longest part in the camera's name applies, in any case; none in it, or none set, is no offset.</summary>
    [Test]
    public void The_longest_part_in_the_camera_s_name_applies()
    {
        var offsets = new Dictionary<string, int> { ["OBSBOT"] = -60, ["obsbot meet flip"] = -45, ["Integrated"] = -30 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CameraOffsets.For("OBSBOT Meet Flip StreamCamera", offsets), Is.EqualTo(-45));
            Assert.That(CameraOffsets.For("OBSBOT Tiny 2", offsets), Is.EqualTo(-60));
            Assert.That(CameraOffsets.For("Integrated Camera", offsets), Is.EqualTo(-30));
            Assert.That(CameraOffsets.For("Pixel 10 Pro XL", offsets), Is.Zero);
            Assert.That(CameraOffsets.For("OBSBOT Meet Flip StreamCamera", null), Is.Zero);
        }
    }

    /// <summary>Offsets within the rules pass, and bind as the run reads them.</summary>
    [Test]
    public void Offsets_within_the_rules_pass_and_bind()
    {
        var settings = new Dictionary<string, string?> { ["Recording:CameraOffsets:OBSBOT"] = "-60", ["Recording:CameraOffsets:Integrated"] = "1000" };

        var bound = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(RecordingOptions.SectionName).Get<RecordingOptions>()!;

        Assert.That(Problems(settings), Is.Empty);
        Assert.That(bound.CameraOffsets, Is.EquivalentTo(new Dictionary<string, int> { ["OBSBOT"] = -60, ["Integrated"] = 1000 }));
    }

    /// <summary>A figure outside -1000 to 1000, one that is no whole number, a single value in place of the set, and a name part with a colon are refused, each in words.</summary>
    [Test]
    public void Offsets_outside_the_rules_are_refused_in_words()
    {
        var problems = Problems(new Dictionary<string, string?>
        {
            ["Recording:CameraOffsets:OBSBOT"] = "-1500",
            ["Recording:CameraOffsets:Integrated"] = "-30ms",
            ["Recording:CameraOffsets:Pixel:Back"] = "10"
        });

        Assert.That(problems, Is.EquivalentTo(new[]
        {
            "Recording:CameraOffsets:OBSBOT is -1500; it must be from -1000 to 1000 milliseconds, such as -60 (negative moves the picture earlier).",
            "Recording:CameraOffsets:Integrated is \"-30ms\", which is not a whole number of milliseconds, such as -60.",
            "Recording:CameraOffsets:Pixel is not a number: a name part may not hold a colon, and each takes a whole number of milliseconds, such as -60."
        }));
        Assert.That(Problems(new Dictionary<string, string?> { ["Recording:CameraOffsets"] = "-60" }),
            Is.EqualTo(new[] { "Recording:CameraOffsets is \"-60\", which is not a set of camera name parts and milliseconds in braces, such as { \"OBSBOT\": -60 }." }));
    }

    /// <summary>A name part longer than 200 characters is refused.</summary>
    [Test]
    public void A_name_part_too_long_is_refused()
    {
        var part = new string('o', 201);

        var problems = Problems(new Dictionary<string, string?> { [$"Recording:CameraOffsets:{part}"] = "-60" });

        Assert.That(problems.Single(), Does.StartWith($"Recording:CameraOffsets names \"{part}\"; each name part must be 1 to 200 characters"));
    }

    /// <summary>The shipped file holds the setting empty: no camera is moved until a clap measures one.</summary>
    [Test]
    public void The_shipped_file_holds_no_offset()
    {
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false, reloadOnChange: false).Build();

        Assert.That(shipped.GetSection(RecordingOptions.SectionName).Get<RecordingOptions>()!.CameraOffsets, Is.Empty);
        Assert.That(new RecordingOptions().CameraOffsets, Is.Empty);
    }
}
