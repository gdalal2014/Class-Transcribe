using System.Net;
using ClassScribe.YouTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// The titles of the record path (design 2.2): one GET of YouTube's public oEmbed answer per video, through the Refit
/// interface and the stub transport, with no key and no cookie; the title, the channel and its @handle read from it; a video
/// YouTube does not answer for, an answer that cannot be read, a connection that fails and a read that runs into its own
/// 10-second limit each give no title, never an error; the caller's cancel goes on; reads at most one a second, on the steady
/// clock; and the guard that keeps every test away from youtube.com itself.
/// </summary>
[TestFixture]
public class OEmbedTitlesTests
{
    /// <summary>YouTube's answer for a public video, as read on 18 October 2026, with the fields this app reads and others it passes over.</summary>
    private const string Answer =
        """{"title":"The creed, part 1","author_name":"Your Channel","author_url":"https://www.youtube.com/@yourchannel","type":"video","height":113,"width":200,"version":"1.0","provider_name":"YouTube","provider_url":"https://www.youtube.com/","thumbnail_url":"https://i.ytimg.com/vi/abcdefghijk/hqdefault.jpg","html":"<iframe></iframe>"}""";

    private StubHttpHandler _handler = null!;
    private ManualTimeProvider _time = null!;
    private RecordingLogger<OEmbedTitles> _log = null!;

    /// <summary>The stub transport, the clock and the log to look at.</summary>
    [SetUp]
    public void SetUp()
    {
        _handler = new StubHttpHandler();
        _time = new ManualTimeProvider(TestRuns.Start);
        _log = new RecordingLogger<OEmbedTitles>();
    }

    /// <summary>The reader under test: the Refit interface over the stub transport at a local stub's address.</summary>
    private OEmbedTitles Create()
    {
        var http = new HttpClient(new YouTubeHostGuard { InnerHandler = _handler }) { BaseAddress = new Uri("http://127.0.0.1:5005/") };
        var api = RestService.ForGenerated<IYouTubeOEmbedApi>(http, YouTubeJson.Default, OEmbedTitles.RefitSettings());
        return new OEmbedTitles(api, _time, _log);
    }

    /// <summary>
    /// A title is read with one GET to the oEmbed address, the video's link rebuilt from its ID and escaped, asking for JSON,
    /// with no key and no body; the title, the channel's name, its address and its @handle come from the answer.
    /// </summary>
    [Test]
    public async Task A_title_is_read_with_one_GET_and_its_channel_and_handle_come_with_it()
    {
        _handler.EnqueueFor("/oembed", HttpStatusCode.OK, Answer);

        var title = await Create().ReadAsync("abcdefghijk", CancellationToken.None);

        var request = _handler.Requests.Single();
        Assert.That((request.Method, request.Uri!.AbsoluteUri, request.ApiKey, request.Body),
            Is.EqualTo((HttpMethod.Get, "http://127.0.0.1:5005/oembed?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dabcdefghijk&format=json", (string?)null, "")));
        Assert.That(title, Is.EqualTo(new VideoTitle("The creed, part 1", "Your Channel", "@yourchannel", "https://www.youtube.com/@yourchannel")));
    }

    /// <summary>A channel whose address is its /channel/ id has no @handle; an Arabic title comes through as it is, without the spaces around it.</summary>
    [Test]
    public async Task A_channel_address_without_a_handle_gives_none_and_a_title_is_kept_as_written()
    {
        _handler.EnqueueFor("/oembed", HttpStatusCode.OK,
            """{"title":"  العقيدة 1 ","author_name":"Shaykh X","author_url":"https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv"}""");

        var title = await Create().ReadAsync("abcdefghijk", CancellationToken.None);

        Assert.That(title, Is.EqualTo(new VideoTitle("العقيدة 1", "Shaykh X", null, "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv")));
    }

    /// <summary>
    /// A title and a channel's name lose every control character, which could ring a bell or start a sequence in the window:
    /// each becomes a space, and a title that is nothing else is no title.
    /// </summary>
    [Test]
    public async Task Control_characters_are_taken_out_of_a_title_and_its_channel()
    {
        _handler.EnqueueFor("/oembed", HttpStatusCode.OK, """{"title":"The creed\u0007, part 1\u001b[0m","author_name":"Shaykh\u0007X","author_url":"https://www.youtube.com/@shaykhx"}""");
        _handler.EnqueueFor("/oembed", HttpStatusCode.OK, """{"title":"\u0007\u001b","author_name":"Shaykh X"}""");
        var titles = Create();

        var title = await titles.ReadAsync("abcdefghijk", CancellationToken.None);
        _time.Advance(OEmbedTitles.Every);
        var nothing = await titles.ReadAsync("bcdefghijkl", CancellationToken.None);

        Assert.That((title!.Title, title.Channel), Is.EqualTo(("The creed , part 1 [0m", "Shaykh X")));
        Assert.That(nothing, Is.Null);
    }

    /// <summary>
    /// The client's own transport keeps no cookie (design 2.2): a cookie one of YouTube's answers sets never goes back with the
    /// next request, so the batch's reads are not linked by it.
    /// </summary>
    [Test]
    public void The_clients_transport_keeps_no_cookie()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddYouTube();
        List<HttpMessageHandler?> primaries = [];
        services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => primaries.Add(builder.PrimaryHandler)));
        using var provider = services.BuildServiceProvider();

        // The client, as a batch's scope makes it: the only one made here, so the only transport built.
        Assert.That(provider.GetRequiredService<IYouTubeOEmbedApi>(), Is.Not.Null);

        Assert.That(primaries.Single(), Is.InstanceOf<SocketsHttpHandler>());
        Assert.That(((SocketsHttpHandler)primaries.Single()!).UseCookies, Is.False);
    }

    /// <summary>
    /// A video YouTube does not answer for (private, removed, or not embeddable: 401, 403, 404), an answer that is not JSON,
    /// one without a title, and a connection that fails each give no title, said at Debug alone; never an error, and never
    /// a second request.
    /// </summary>
    [TestCase(HttpStatusCode.Unauthorized, "Unauthorized")]
    [TestCase(HttpStatusCode.Forbidden, "Forbidden")]
    [TestCase(HttpStatusCode.NotFound, "Not Found")]
    [TestCase(HttpStatusCode.OK, "<html>no</html>")]
    [TestCase(HttpStatusCode.OK, """{"author_name":"Your Channel"}""")]
    [TestCase(HttpStatusCode.OK, """{"title":"   "}""")]
    public async Task A_video_without_an_answer_gives_no_title(HttpStatusCode status, string body)
    {
        _handler.EnqueueFor("/oembed", status, body);

        var title = await Create().ReadAsync("abcdefghijk", CancellationToken.None);

        Assert.That(title, Is.Null);
        Assert.That(_handler.Requests, Has.Count.EqualTo(1));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A connection that fails gives no title, said at Debug.</summary>
    [Test]
    public async Task A_connection_that_fails_gives_no_title()
    {
        _handler.EnqueueFailureFor("/oembed", new HttpRequestException("No such host is known."));

        var title = await Create().ReadAsync("abcdefghijk", CancellationToken.None);

        Assert.That(title, Is.Null);
        Assert.That(_log.Problems, Is.Empty);
        Assert.That(_log.Messages, Has.Some.Contains("abcdefghijk"));
    }

    /// <summary>A read whose answer never comes gives no title once its own 10 seconds are up, on the steady clock.</summary>
    [Test]
    public async Task A_read_that_runs_into_its_own_limit_gives_no_title()
    {
        _handler.EnqueueStalledBodyFor("/oembed", HttpStatusCode.OK);

        var reading = Create().ReadAsync("abcdefghijk", CancellationToken.None);
        var moved = await _time.AdvanceUntilAsync(() => reading.IsCompleted, TimeSpan.FromSeconds(1), atMost: TimeSpan.FromSeconds(12));

        Assert.That(await reading, Is.Null);
        Assert.That(moved, Is.GreaterThanOrEqualTo(OEmbedTitles.Limit));
    }

    /// <summary>The caller's own cancel is not a missing title: it goes on to the caller.</summary>
    [Test]
    public void The_callers_cancel_goes_on()
    {
        _handler.EnqueueStalledBodyFor("/oembed", HttpStatusCode.OK);
        using var cancel = new CancellationTokenSource();

        var reading = Create().ReadAsync("abcdefghijk", cancel.Token);
        cancel.Cancel();

        Assert.That(async () => await reading, Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>Reads are at most one a second, counted on the steady clock from the start of the one before.</summary>
    [Test]
    public async Task Reads_are_at_most_one_a_second()
    {
        _handler.EnqueueFor("/oembed", HttpStatusCode.OK, Answer);
        _handler.EnqueueFor("/oembed", HttpStatusCode.OK, Answer);
        var titles = Create();

        await titles.ReadAsync("abcdefghijk", CancellationToken.None);
        var second = titles.ReadAsync("bcdefghijkl", CancellationToken.None);
        await Task.Delay(50);
        Assert.That(_handler.Requests, Has.Count.EqualTo(1), "the second waits");

        _time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50);
        Assert.That(_handler.Requests, Has.Count.EqualTo(1), "still waiting a millisecond before the second");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5)), Is.Not.Null);
        Assert.That(_handler.Requests[1].Uri!.Query, Does.Contain("bcdefghijkl"));
    }

    /// <summary>
    /// In a test, the guard on the client refuses youtube.com itself, naming the stand-in to use, so that a test that forgets
    /// the stub never reaches YouTube from Gulzar's connection; a stub's address passes.
    /// </summary>
    [TestCase("https://www.youtube.com/oembed")]
    [TestCase("https://youtube.com/oembed")]
    [TestCase("https://m.youtube.com/oembed")]
    public void The_guard_refuses_YouTube_itself_in_a_test(string address)
    {
        using var http = new HttpClient(new YouTubeHostGuard { InnerHandler = _handler });

        var refused = Assert.ThrowsAsync<InvalidOperationException>(async () => await http.GetAsync(address));

        Assert.That(refused!.Message, Does.Contain("StubHttpHandler"));
        Assert.That(_handler.Requests, Is.Empty);
    }
}
