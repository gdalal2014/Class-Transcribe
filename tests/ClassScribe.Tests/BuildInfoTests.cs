using ClassScribe.Link;

namespace ClassScribe.Tests;

/// <summary>
/// What build this is, as the menu's header, the help, --version, the run files and the link name it: the version from
/// the project and the commit the SDK or the install script stamped in, read from the informational version.
/// </summary>
[TestFixture]
public class BuildInfoTests
{
    /// <summary>
    /// The informational version is split at its plus sign into the version and the commit. The SDK's own stamp is the
    /// whole 40-character commit, which is cut to 7; what git describe gives (a short commit, -dirty, a tag with a
    /// distance) is kept as it is; a version without a commit has none; and no attribute at all gives 0.0.0.
    /// </summary>
    [TestCase("0.4.0+f5f2fb04b43e65a58888ecd217914f200705cb9d", "0.4.0", "f5f2fb0")]
    [TestCase("0.4.0+F5F2FB04B43E65A58888ECD217914F200705CB9D", "0.4.0", "F5F2FB0")]
    [TestCase("0.4.0+f5f2fb04b43e65a58888ecd217914f200705cb9d-dirty", "0.4.0", "f5f2fb0-dirty")]
    [TestCase("0.4.0+f5f2fb0", "0.4.0", "f5f2fb0")]
    [TestCase("0.4.0+f5f2fb0-dirty", "0.4.0", "f5f2fb0-dirty")]
    [TestCase("0.4.0+v0.4.0-3-gf5f2fb0", "0.4.0", "v0.4.0-3-gf5f2fb0")]
    [TestCase("0.4.0+v0.4.0", "0.4.0", "v0.4.0")]
    [TestCase("0.4.0", "0.4.0", "")]
    [TestCase("0.4.0+", "0.4.0", "")]
    [TestCase(" 0.4.0 + f5f2fb0 ", "0.4.0", "f5f2fb0")]
    [TestCase("1.0.0-beta.2+f5f2fb0", "1.0.0-beta.2", "f5f2fb0")]
    [TestCase("", "0.0.0", "")]
    [TestCase("   ", "0.0.0", "")]
    [TestCase(null, "0.0.0", "")]
    public void The_informational_version_is_read_as_the_version_and_the_commit(string? informational, string version, string commit)
    {
        Assert.That(BuildInfo.Parse(informational), Is.EqualTo((version, commit)));
    }

    /// <summary>A build is described as the version with the commit in brackets, or the version alone when the commit is not known.</summary>
    [TestCase("0.4.0", "f5f2fb0", "0.4.0 (f5f2fb0)")]
    [TestCase("0.4.0", "f5f2fb0-dirty", "0.4.0 (f5f2fb0-dirty)")]
    [TestCase("0.4.0", "v0.4.0-3-gf5f2fb0", "0.4.0 (v0.4.0-3-gf5f2fb0)")]
    [TestCase("0.4.0", "", "0.4.0")]
    [TestCase("0.0.0", "", "0.0.0")]
    public void A_build_is_described_as_the_version_with_the_commit_in_brackets(string version, string commit, string described)
    {
        Assert.That(BuildInfo.Describe(version, commit), Is.EqualTo(described));
    }

    /// <summary>
    /// This build's version is the project's, as the assembly carries it in its first three parts, and the informational
    /// version --version prints starts with it. The commit, when the build knows one, is what the description shows in brackets.
    /// </summary>
    [Test]
    public void This_build_names_the_projects_version()
    {
        var assemblyVersion = typeof(BuildInfo).Assembly.GetName().Version!.ToString(3);

        Assert.That(BuildInfo.Version, Is.EqualTo(assemblyVersion));
        Assert.That(BuildInfo.InformationalVersion, Does.StartWith(BuildInfo.Version));
        Assert.That(BuildInfo.Describe(), Is.EqualTo(BuildInfo.Describe(BuildInfo.Version, BuildInfo.Commit)));
        Assert.That(BuildInfo.Describe(), Does.StartWith(BuildInfo.Version));
    }

    /// <summary>The run files, the recorder's log and the link's hello and welcome all carry the one build string, which is the description.</summary>
    [Test]
    public void The_build_string_is_the_description_everywhere()
    {
        Assert.That(BuildInfo.Build, Is.EqualTo(BuildInfo.Describe()));
        Assert.That(LinkProtocol.Build, Is.EqualTo(BuildInfo.Build));
    }
}
