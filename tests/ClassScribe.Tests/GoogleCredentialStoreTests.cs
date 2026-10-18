using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ClassScribe.Drive;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The store of the Google client and sign-in, with Windows' real data protection for this account, in a temporary folder
/// that stands for %APPDATA%\ClassScribe: the real folder is never touched. What is written reads back; the file holds no
/// secret in plain text; a file that cannot be opened counts as no sign-in, with the reason; and a write replaces the file
/// whole or not at all.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class GoogleCredentialStoreTests
{
    private TempFolder _folder = null!;
    private RoamingFolder _roaming = null!;

    /// <summary>The credentials the tests store: the tests' client, signed in.</summary>
    private static readonly GoogleCredentials SignedIn = new(FakeGoogle.ClientId, FakeGoogle.ClientSecret)
    {
        RefreshToken = "1//0gTestRefresh-0001-secret",
        Email = "student@example.com",
        SignedInUtc = TestRuns.Start,
        Scope = FakeGoogle.DriveFileScope
    };

    /// <summary>Makes the temporary folder, with the roaming folder inside it, not yet made.</summary>
    [SetUp]
    public void SetUp()
    {
        Assume.That(OperatingSystem.IsWindows(), "Windows' data protection");
        _folder = new TempFolder();
        _roaming = new RoamingFolder(_folder.File("Roaming"));
    }

    /// <summary>Deletes it.</summary>
    [TearDown]
    public void TearDown() => _folder?.Dispose();

    /// <summary>The store over the temporary roaming folder.</summary>
    private DpapiGoogleCredentialStore Store() => new(_roaming, NullLogger<DpapiGoogleCredentialStore>.Instance);

    /// <summary>The files in the roaming folder, by name.</summary>
    private string[] FilesThere() => Directory.Exists(_roaming.Root) ? [.. Directory.GetFiles(_roaming.Root).Select(file => Path.GetFileName(file)!)] : [];

    /// <summary>With nothing stored, the store reads as not set up, and makes nothing.</summary>
    [Test]
    public void Nothing_stored_reads_as_not_set_up()
    {
        var read = Store().Read();

        Assert.That(read, Is.EqualTo(StoredCredentials.None));
        Assert.That(read.IsSetUp, Is.False);
        Assert.That(Directory.Exists(_roaming.Root), Is.False, "nothing is made until a sign-in is stored");
    }

    /// <summary>What is written reads back, in a new store as in a new process; the folder is made for it; the file is named google-drive.dat.</summary>
    [Test]
    public void What_is_written_reads_back()
    {
        Store().Write(SignedIn);

        var read = Store().Read();

        Assert.That(read.Credentials, Is.EqualTo(SignedIn));
        Assert.That(read.Problem, Is.Null);
        Assert.That(FilesThere(), Is.EqualTo(new[] { "google-drive.dat" }));
        Assert.That(Store().Location, Is.EqualTo(Path.Combine(_roaming.Root, "google-drive.dat")));
    }

    /// <summary>A client without a sign-in, as after a sign-out, reads back as set up and not signed in.</summary>
    [Test]
    public void A_client_without_a_sign_in_reads_back()
    {
        Store().Write(SignedIn.SignedOut());

        var read = Store().Read().Credentials!;

        Assert.That((read.ClientId, read.ClientSecret, read.IsSignedIn, read.RefreshToken, read.Email), Is.EqualTo((FakeGoogle.ClientId, FakeGoogle.ClientSecret, false, (string?)null, (string?)null)));
    }

    /// <summary>The file holds neither the secret, nor the refresh token, nor even the client id or the email, in plain text, in UTF-8 or UTF-16.</summary>
    [Test]
    public void The_file_holds_nothing_in_plain_text()
    {
        Store().Write(SignedIn);

        var bytes = File.ReadAllBytes(_roaming.GoogleDriveFile);
        foreach (var value in new[] { FakeGoogle.ClientSecret, SignedIn.RefreshToken!, FakeGoogle.ClientId, SignedIn.Email! })
        {
            Assert.That(Contains(bytes, Encoding.UTF8.GetBytes(value)), Is.False, $"{value[..6]}... in UTF-8");
            Assert.That(Contains(bytes, Encoding.Unicode.GetBytes(value)), Is.False, $"{value[..6]}... in UTF-16");
        }
    }

    /// <summary>
    /// A file this account cannot open, such as one another Windows account or another computer made (here: data protected
    /// with another entropy, or bytes that are not protected data at all), reads as unreadable, with the reason and what to do.
    /// </summary>
    [Test]
    public void A_file_that_cannot_be_opened_reads_as_unreadable_with_the_reason()
    {
        Directory.CreateDirectory(_roaming.Root);
        var others = new[]
        {
            ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"clientId\":\"x\",\"clientSecret\":\"y\"}"), Encoding.UTF8.GetBytes("Another program"), DataProtectionScope.CurrentUser),
            RandomNumberGenerator.GetBytes(300)
        };

        foreach (var bytes in others)
        {
            File.WriteAllBytes(_roaming.GoogleDriveFile, bytes);

            var read = Store().Read();

            Assert.That(read.Credentials, Is.Null);
            Assert.That(read.IsSetUp, Is.False);
            Assert.That(read.Problem, Does.StartWith($"{_roaming.GoogleDriveFile} cannot be opened by this Windows account"));
            Assert.That(read.Problem, Does.Contain("drive signin --client"));
        }
    }

    /// <summary>Protected data that is not the store's JSON, such as a file damaged on the way, reads as damaged.</summary>
    [TestCase("not json")]
    [TestCase("{\"clientId\":\"\",\"clientSecret\":\"y\"}")]
    [TestCase("null")]
    public void Protected_data_that_is_not_a_sign_in_reads_as_damaged(string text)
    {
        Directory.CreateDirectory(_roaming.Root);
        File.WriteAllBytes(_roaming.GoogleDriveFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes("ClassScribe Google Drive 1"), DataProtectionScope.CurrentUser));

        var read = Store().Read();

        Assert.That(read.Credentials, Is.Null);
        Assert.That(read.Problem, Does.StartWith($"{_roaming.GoogleDriveFile} is damaged"));
    }

    /// <summary>A second write replaces the first whole, and leaves no temporary file behind.</summary>
    [Test]
    public void A_write_replaces_the_file_whole()
    {
        Store().Write(SignedIn);
        var second = SignedIn with { RefreshToken = "1//0gTestRefresh-0002-secret", Email = "other@example.com" };

        Store().Write(second);

        Assert.That(Store().Read().Credentials, Is.EqualTo(second));
        Assert.That(FilesThere(), Is.EqualTo(new[] { "google-drive.dat" }));
    }

    /// <summary>
    /// A write that cannot replace the file, here because another program holds it, fails, leaves the file as it was, and
    /// leaves no temporary file: a crash or a failure never leaves half a file.
    /// </summary>
    [Test]
    public void A_write_that_cannot_replace_the_file_leaves_it_whole()
    {
        Store().Write(SignedIn);

        using (new FileStream(_roaming.GoogleDriveFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.That(() => Store().Write(SignedIn with { RefreshToken = "1//0gTestRefresh-0003-secret" }), Throws.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());

        Assert.That(Store().Read().Credentials, Is.EqualTo(SignedIn));
        Assert.That(FilesThere(), Is.EqualTo(new[] { "google-drive.dat" }));
    }

    /// <summary>Credentials written as text, as in a log line by mistake, show neither the secret nor the refresh token.</summary>
    [Test]
    public void Credentials_never_show_their_secrets_as_text()
    {
        var text = SignedIn.ToString();

        Assert.That(text, Does.Not.Contain(FakeGoogle.ClientSecret).And.Not.Contain(SignedIn.RefreshToken!));
        Assert.That(text, Does.Contain("...a1b2c3.apps.googleusercontent.com").And.Contain("IsSignedIn = True"));
    }

    /// <summary>True when <paramref name="needle"/> occurs in <paramref name="haystack"/>.</summary>
    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
