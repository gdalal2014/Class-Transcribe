using System.Reflection;
using System.Text.RegularExpressions;

namespace ClassScribe;

/// <summary>
/// What build this is: the version from the project file and the commit it was built from, read once from the
/// informational version stamped into the assembly. Inside a git repository the SDK stamps the whole commit
/// (0.4.0+f5f2fb04b43e65a58888ecd217914f200705cb9d); the install script passes what git describe gives instead
/// (f5f2fb0, f5f2fb0-dirty or v0.4.0-3-gf5f2fb0). The menu's header, the help, --version, the run files, the recorder's
/// log, the microphone check's log and the link's hello and welcome all name the build through this one place, so that
/// a window and a recorder of different builds can be told apart.
/// </summary>
public static partial class BuildInfo
{
    /// <summary>The version a build without an informational version reports.</summary>
    public const string UnknownVersion = "0.0.0";

    /// <summary>How many characters of a whole 40-character commit are shown, as git shows them.</summary>
    private const int ShortCommitLength = 7;

    /// <summary>Reads the assembly's informational version once and splits it into the version and the commit.</summary>
    static BuildInfo()
    {
        var stamped = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Trim() ?? "";
        (Version, Commit) = Parse(stamped);
        InformationalVersion = stamped.Length > 0 ? stamped : Version;
        Build = Describe(Version, Commit);
    }

    /// <summary>
    /// The informational version as the assembly carries it, such as 0.4.0+f5f2fb04b43e65a58888ecd217914f200705cb9d, which
    /// --version prints as it is; the version alone when the assembly carries none.
    /// </summary>
    public static string InformationalVersion { get; }

    /// <summary>The version from the project file, such as 0.4.0: the part before the plus sign, or 0.0.0 when there is no informational version.</summary>
    public static string Version { get; }

    /// <summary>The commit as the build knew it: f5f2fb0, f5f2fb0-dirty or v0.4.0-3-gf5f2fb0; empty when it is not known.</summary>
    public static string Commit { get; }

    /// <summary>The build string the run files, the logs and the link's hello and welcome carry: <see cref="Describe()"/>.</summary>
    public static string Build { get; }

    /// <summary>This build in words: 0.4.0 (f5f2fb0), or 0.4.0 alone when the commit is not known.</summary>
    public static string Describe() => Build;

    /// <summary>A build in words: <paramref name="version"/> with <paramref name="commit"/> in brackets, or the version alone when the commit is empty.</summary>
    public static string Describe(string version, string commit) => commit.Length == 0 ? version : $"{version} ({commit})";

    /// <summary>
    /// Splits an informational version at its first plus sign into the version and the commit, both trimmed. A whole
    /// 40-character commit is cut to its first 7, keeping anything after it, such as -dirty; anything shorter, such as
    /// what git describe gives, is kept as it is. No plus sign means no commit, and nothing at all means <see cref="UnknownVersion"/>.
    /// </summary>
    public static (string Version, string Commit) Parse(string? informationalVersion)
    {
        var text = informationalVersion?.Trim() ?? "";
        if (text.Length == 0) return (UnknownVersion, "");

        var plus = text.IndexOf('+');
        if (plus < 0) return (text, "");

        var version = text[..plus].Trim();
        var commit = text[(plus + 1)..].Trim();
        if (version.Length == 0) version = UnknownVersion;
        if (WholeCommit().Match(commit) is { Success: true } whole) commit = commit[..ShortCommitLength] + whole.Groups["rest"].Value;
        return (version, commit);
    }

    /// <summary>A whole 40-character commit, with whatever follows it in the rest group.</summary>
    [GeneratedRegex("^[0-9a-fA-F]{40}(?<rest>.*)$")]
    private static partial Regex WholeCommit();
}
