using System.Net;
using ClassScribe.Drive;

namespace ClassScribe.Tests;

/// <summary>
/// Google Drive's room for a YouTube batch's plan (design 2.10): nothing is asked while Drive is off or not signed in; the
/// about call's storage is Drive's room, its free part worked out from the limit and the usage; an answer without the
/// storage gives no room; and Drive out of reach, or answering with an error, gives no room either, said in a line, never
/// thrown, so the batch goes on. Google is played in memory: nothing reaches it.
/// </summary>
[TestFixture]
public class DriveRoomTests
{
    private GoogleWorld _world = null!;

    /// <summary>The in-memory Google, nothing stored.</summary>
    [SetUp]
    public void SetUp() => _world = new GoogleWorld();

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The room's reader over the world's store, Drive and settings.</summary>
    private DriveRoom Room() => new(_world.Store, new Lazy<DriveClient>(_world.Drive), _world.Options, _world.Log<DriveRoom>());

    /// <summary>Not signed in, or with Drive off, there is no room to read, and Google is asked nothing.</summary>
    [Test]
    public async Task Without_a_sign_in_or_with_Drive_off_nothing_is_asked()
    {
        Assert.That(await Room().ReadAsync(CancellationToken.None), Is.Null, "not signed in");

        _world.SignedIn();
        _world.Options.Set(new DriveOptions { Enabled = false });

        Assert.That(await Room().ReadAsync(CancellationToken.None), Is.Null, "Drive off");
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>Signed in, the about call's storage is Drive's room: its usage, its limit, and what is free between them.</summary>
    [Test]
    public async Task Signed_in_the_storage_is_the_room()
    {
        _world.SignedIn();
        _world.Google.StorageLimit = 15_000_000_000;
        _world.Google.StorageUsage = 14_000_000_000;

        var room = await Room().ReadAsync(CancellationToken.None);

        Assert.That(room, Is.EqualTo(new DriveSpace(14_000_000_000, 15_000_000_000)));
        Assert.That(room!.Free, Is.EqualTo(1_000_000_000));
        Assert.That(_world.Google.Requests.Select(request => request.Path), Has.Some.EqualTo("/drive/v3/about"));
    }

    /// <summary>An answer of the about call without its storage gives no room.</summary>
    [Test]
    public async Task An_answer_without_the_storage_gives_no_room()
    {
        _world.SignedIn();
        _world.Google.Fail("/drive/v3/about", HttpStatusCode.OK, """{"user":{"displayName":"A Student","emailAddress":"student@example.com"}}""");

        Assert.That(await Room().ReadAsync(CancellationToken.None), Is.Null);
    }

    /// <summary>Drive answering with an error, or out of reach, gives no room, said in one line at Information; nothing is thrown.</summary>
    [Test]
    public async Task Drive_failing_gives_no_room_and_says_so()
    {
        _world.SignedIn();
        _world.Google.Fail("/drive/v3/about", HttpStatusCode.InternalServerError, """{"error":{"code":500,"message":"Backend Error"}}""");

        Assert.That(await Room().ReadAsync(CancellationToken.None), Is.Null);

        _world.Google.Unreachable = true;

        Assert.That(await Room().ReadAsync(CancellationToken.None), Is.Null);
        Assert.That(_world.Logs.Messages.Count(line => line.StartsWith("Google Drive's room could not be read (", StringComparison.Ordinal)
            && line.EndsWith("); what does not fit waits in the queue", StringComparison.Ordinal)), Is.EqualTo(2));
    }
}
