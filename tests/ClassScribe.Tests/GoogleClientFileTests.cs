using System.Text.Json.Nodes;
using ClassScribe.Drive;

namespace ClassScribe.Tests;

/// <summary>
/// Reading the client file Google offers when a client is made (Download JSON): a Desktop app client's id and secret are
/// taken; any other file is refused with what to do instead, and nothing of what the file holds is ever repeated, since it
/// may be the secret.
/// </summary>
[TestFixture]
public class GoogleClientFileTests
{
    private GoogleWorld _world = null!;

    /// <summary>Makes the folder the client files are written to.</summary>
    [SetUp]
    public void SetUp() => _world = new GoogleWorld();

    /// <summary>Deletes it.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The message of the refusal of the file at <paramref name="path"/>, which must be one about the client file.</summary>
    private static string Refusal(string path)
    {
        var refusal = Assert.Throws<GoogleDriveException>(() => new GoogleClientFile().Read(path))!;
        Assert.That(refusal.Problem, Is.EqualTo(DriveProblem.ClientFile));
        Assert.That(refusal.Message, Does.Not.Contain(FakeGoogle.ClientSecret), "the secret is never repeated");
        return refusal.Message;
    }

    /// <summary>A Desktop app client's file, as Google's Download JSON gives it, gives the client's id and secret.</summary>
    [Test]
    public void A_desktop_client_file_gives_its_id_and_secret()
    {
        var client = new GoogleClientFile().Read(_world.WriteClientFile());

        Assert.That(client, Is.EqualTo(new GoogleClient(FakeGoogle.ClientId, FakeGoogle.ClientSecret)));
    }

    /// <summary>The id and the secret are taken without spaces around them, as a file edited by hand may have them.</summary>
    [Test]
    public void Spaces_around_the_id_and_the_secret_are_left_out()
    {
        var path = _world.WriteClientFile(change: inner =>
        {
            inner["client_id"] = $"  {FakeGoogle.ClientId} ";
            inner["client_secret"] = $"{FakeGoogle.ClientSecret}\n";
        });

        Assert.That(new GoogleClientFile().Read(path), Is.EqualTo(new GoogleClient(FakeGoogle.ClientId, FakeGoogle.ClientSecret)));
    }

    /// <summary>A Web application client's file is refused: ClassScribe signs in as a Desktop app, and the message says how to make one.</summary>
    [Test]
    public void A_web_client_file_is_refused_with_what_to_make_instead()
    {
        var path = _world.WriteClientFile(kind: "web");

        Assert.That(Refusal(path), Is.EqualTo($"{path} is the file of a Web application client, and ClassScribe signs in as a Desktop app. In Google Auth platform > Clients, click Create client, choose Desktop app, and download its JSON."));
    }

    /// <summary>A JSON file without the installed section, such as a service account's key, is refused.</summary>
    [Test]
    public void A_file_without_the_installed_section_is_refused()
    {
        var path = _world.Downloads.File("key.json");
        File.WriteAllText(path, new JsonObject { ["type"] = "service_account", ["private_key"] = "-----BEGIN PRIVATE KEY-----" }.ToJsonString());

        Assert.That(Refusal(path), Does.StartWith($"{path} is not a Desktop app client's file from Google").And.Contain("Desktop app").And.Not.Contain("PRIVATE KEY"));
    }

    /// <summary>A file without the client's id or secret, or with one empty, names what is missing and what to do.</summary>
    [TestCase("client_id", null, "holds no client_id")]
    [TestCase("client_id", " ", "holds no client_id")]
    [TestCase("client_secret", null, "holds no client_secret")]
    [TestCase("client_secret", "", "holds no client_secret")]
    public void A_missing_id_or_secret_is_named(string field, string? value, string reason)
    {
        var path = _world.WriteClientFile(change: inner =>
        {
            if (value is null) inner.Remove(field);
            else inner[field] = value;
        });

        var message = Refusal(path);

        Assert.That(message, Does.StartWith($"{path} {reason}"));
        if (field == "client_secret") Assert.That(message, Does.Contain("Add secret"), "Google shows a secret only once, so a new one is added");
    }

    /// <summary>An id or secret that is not text is refused as missing.</summary>
    [Test]
    public void An_id_that_is_not_text_is_refused()
    {
        var path = _world.WriteClientFile(change: inner => inner["client_id"] = 42);

        Assert.That(Refusal(path), Does.StartWith($"{path} holds no client_id"));
    }

    /// <summary>A file that is not JSON, or JSON that is not an object, is refused without a word of what it holds.</summary>
    [TestCase("client_id=abc&client_secret=GOCSPX-TestClientSecret-0123456789ab")]
    [TestCase("[\"GOCSPX-TestClientSecret-0123456789ab\"]")]
    [TestCase("")]
    public void A_file_that_is_not_a_JSON_object_is_refused_without_quoting_it(string text)
    {
        var path = _world.Downloads.File("client.json");
        File.WriteAllText(path, text);

        Assert.That(Refusal(path), Is.EqualTo($"{path} is not a client file from Google: it is not JSON in the shape Google writes. Download it again with Download JSON on the client's page (Google Auth platform > Clients)."));
    }

    /// <summary>A path where nothing is, or a folder, is named.</summary>
    [Test]
    public void A_missing_file_or_a_folder_is_named()
    {
        var missing = _world.Downloads.File("nothing.json");

        Assert.That(Refusal(missing), Does.StartWith($"Nothing found at {missing}."));
        Assert.That(Refusal(_world.Downloads.Path), Does.StartWith($"{_world.Downloads.Path} is a folder; give the client file itself"));
    }

    /// <summary>A large file, such as a recording dragged in by mistake, is refused without being read.</summary>
    [Test]
    public void A_large_file_is_refused_unread()
    {
        var path = _world.Downloads.File("Kalam.wav");
        File.WriteAllBytes(path, new byte[GoogleClientFile.LargestFile + 1]);

        Assert.That(Refusal(path), Does.StartWith($"{path} is not a client file from Google: it is 65,537 bytes"));
    }

    /// <summary>A client written as text, as in a log line by mistake, shows the end of its id and never the secret.</summary>
    [Test]
    public void A_client_never_shows_its_secret_as_text()
    {
        var client = new GoogleClient(FakeGoogle.ClientId, FakeGoogle.ClientSecret);

        Assert.That(client.ShortId, Is.EqualTo("...a1b2c3.apps.googleusercontent.com"));
        Assert.That(client.ToString(), Does.Not.Contain(FakeGoogle.ClientSecret).And.Contain(client.ShortId));
        Assert.That(GoogleClient.Describe("short"), Is.EqualTo("...short"), "an id of another shape shows its last 6 characters");
    }
}
