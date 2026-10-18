using System.Globalization;
using System.Text;
using System.Text.Json;

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

/// <summary>
/// Reads the client file Google offers when a client is made (Google Auth platform > Clients > Download JSON): a Desktop app
/// client's file has the client under "installed", with "client_id" and "client_secret". Any other file is refused with
/// what to do instead, and nothing the file holds is repeated in the message, since it may be the secret.
/// </summary>
public static class GoogleClientFile
{
    /// <summary>The largest file read: a client file is well under a kilobyte, and a recording dragged in by mistake is not read at all.</summary>
    public const long LargestFile = 64 * 1024;

    /// <summary>Where Google's console keeps the clients, for the messages.</summary>
    private const string ClientsPage = "Google Auth platform > Clients";

    /// <summary>
    /// The client in the file at <paramref name="path"/>. Throws a <see cref="GoogleDriveException"/> of
    /// <see cref="DriveProblem.ClientFile"/> when there is no file there, or one that is not a Desktop app client's file
    /// from Google, saying what to do instead.
    /// </summary>
    public static GoogleClient Read(string path)
    {
        if (Directory.Exists(path)) throw Refused($"{path} is a folder; give the client file itself, such as client_secret_....json in your Downloads folder.");

        byte[] bytes;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw Refused($"Nothing found at {path}. Give the client file you downloaded from Google, such as client_secret_....json in your Downloads folder.");
            if (file.Length > LargestFile)
                throw Refused(string.Create(CultureInfo.InvariantCulture, $"{path} is not a client file from Google: it is {file.Length:N0} bytes, and a client file is well under a kilobyte. Give the client_secret_....json file you downloaded from {ClientsPage}."));

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Refused($"{path} could not be read: {ex.Message}", ex);
        }

        return Parse(path, bytes);
    }

    /// <summary>The client in <paramref name="bytes"/>, the file at <paramref name="path"/>, or a refusal that says why there is none.</summary>
    private static GoogleClient Parse(string path, byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            // The parser's own message is not passed on: it may quote what the file holds.
            throw Refused(NotJson(path), ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Refused(NotJson(path));

            if (!root.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Object)
            {
                if (root.TryGetProperty("web", out _))
                    throw Refused($"{path} is the file of a Web application client, and ClassScribe signs in as a Desktop app. In {ClientsPage}, click Create client, choose Desktop app, and download its JSON.");

                throw Refused($"{path} is not a Desktop app client's file from Google: it has no \"installed\" section. In {ClientsPage}, click Create client, choose Desktop app, and download its JSON.");
            }

            var id = Text(installed, "client_id");
            if (id is null) throw Refused($"{path} holds no client_id. Download the client's JSON again from {ClientsPage}.");

            var secret = Text(installed, "client_secret");
            if (secret is null)
                throw Refused($"{path} holds no client_secret. Google shows a client's secret only once, when it is made: on the client's page in {ClientsPage}, click Add secret, download the JSON again, and give that file.");

            return new GoogleClient(id, secret);
        }
    }

    /// <summary>The string property <paramref name="name"/> of <paramref name="section"/>, trimmed; null when it is missing, empty or not text.</summary>
    private static string? Text(JsonElement section, string name) =>
        section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>What is said about a file that is not JSON in the shape Google writes.</summary>
    private static string NotJson(string path) =>
        $"{path} is not a client file from Google: it is not JSON in the shape Google writes. Download it again with Download JSON on the client's page ({ClientsPage}).";

    /// <summary>The refusal of a client file, with <paramref name="message"/>.</summary>
    private static GoogleDriveException Refused(string message, Exception? inner = null) => new(DriveProblem.ClientFile, message, inner);
}
