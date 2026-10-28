using System.Text;

namespace ClassScribe.Drive;

/// <summary>
/// The Google OAuth client ClassScribe signs in with: Gulzar's own Desktop app client, from his own Google Cloud project.
/// Google treats a desktop client's secret as not confidential, but it is his and stays out of git, logs and settings: as
/// text, a client shows only the end of its id (<see cref="ShortId"/>), never the secret.
/// </summary>
/// <param name="Id">The client id, ending in .apps.googleusercontent.com.</param>
/// <param name="Secret">The client secret.</param>
public sealed record GoogleClient(string Id, string Secret)
{
    /// <summary>How every Google client id ends.</summary>
    public const string IdSuffix = ".apps.googleusercontent.com";

    /// <summary>How many characters of the id before <see cref="IdSuffix"/> are shown.</summary>
    private const int ShownCharacters = 6;

    /// <summary>The end of the id, for status lines: ...a1b2c3.apps.googleusercontent.com. Never the secret.</summary>
    public string ShortId => Describe(Id);

    /// <summary>
    /// The end of the client id <paramref name="id"/>: its last 6 characters before .apps.googleusercontent.com, with the
    /// suffix, or the last 6 characters of an id of another shape.
    /// </summary>
    public static string Describe(string id)
    {
        var head = id.EndsWith(IdSuffix, StringComparison.OrdinalIgnoreCase) ? id[..^IdSuffix.Length] : id;
        var tail = head.Length > ShownCharacters ? head[^ShownCharacters..] : head;
        return id.EndsWith(IdSuffix, StringComparison.OrdinalIgnoreCase) ? $"...{tail}{IdSuffix}" : $"...{tail}";
    }

    /// <summary>The client as text, which a log line might hold by mistake: the end of its id, never the secret.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(ShortId);
        return true;
    }
}
