using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Refit;

namespace ClassScribe.YouTube;

/// <summary>
/// YouTube's public oEmbed address (design 2.2), declared for Refit: one GET per video, with no key and no cookie, which
/// answers with its title and its channel, and nothing of its length or privacy. The response comes back whatever its
/// status, so that a video YouTube does not answer for is read as no title, never thrown.
/// </summary>
public interface IYouTubeOEmbedApi
{
    /// <summary>Reads the oEmbed answer for the video at <paramref name="url"/>, a watch link rebuilt from its ID, as <paramref name="format"/> (json).</summary>
    [Get("/oembed")]
    Task<HttpResponseMessage> ReadAsync([Query] string url, [Query] string format, CancellationToken cancellationToken);
}

/// <summary>The fields of YouTube's oEmbed answer that ClassScribe reads; every other field is passed over.</summary>
/// <param name="Title">The video's title.</param>
/// <param name="AuthorName">Its channel's name.</param>
/// <param name="AuthorUrl">Its channel's address: its @handle's, such as https://www.youtube.com/@yourchannel, or its /channel/ id's.</param>
public sealed record OEmbedAnswer(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("author_name")] string? AuthorName,
    [property: JsonPropertyName("author_url")] string? AuthorUrl);

/// <summary>A video's title and channel, as YouTube's oEmbed answer gives them.</summary>
/// <param name="Title">The title, without the spaces around it.</param>
/// <param name="Channel">The channel's name, when given.</param>
/// <param name="ChannelHandle">The channel's @handle, when its address names one.</param>
/// <param name="ChannelUrl">The channel's address, when given.</param>
public sealed record VideoTitle(string Title, string? Channel, string? ChannelHandle, string? ChannelUrl);

/// <summary>
/// Reads the titles of the record path's videos (design 2.2) from YouTube's oEmbed address, YouTube:OEmbedBaseUrl: at most
/// one request a second, counted on the steady clock from the start of the one before, each with a limit of 10 seconds of
/// its own. A video it does not answer for (private, removed, or not embeddable), an answer that cannot be read, a connection
/// that fails and a read that runs out of time each give no title, said at Debug: the video keeps its ID as its title and is
/// tried all the same. The caller's own cancel goes on. One per batch, which paces its reads.
/// </summary>
public sealed class OEmbedTitles(IYouTubeOEmbedApi api, TimeProvider time, ILogger<OEmbedTitles> logger)
{
    /// <summary>How long one read may take, the answer included.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>The least time between the starts of two reads.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    /// <summary>What the plan says of a video YouTube gave no title for.</summary>
    public const string NoTitle = "YouTube gave no title for it (private, removed, or not embeddable); it is tried all the same";

    /// <summary>When the last read began, on the steady clock; null before the first.</summary>
    private long? _lastStart;

    /// <summary>
    /// The Refit settings the client is used with, in the app and in the tests: a request that cannot be sent fails with its
    /// own exception rather than Refit's wrapper, so that the reader can tell YouTube out of reach from its own limit.
    /// </summary>
    public static RefitSettings RefitSettings() => new() { TransportExceptionFactory = (_, error, _) => error };

    /// <summary>The title of video <paramref name="videoId"/>; null when YouTube gave none (see the class).</summary>
    public async Task<VideoTitle?> ReadAsync(string videoId, CancellationToken cancellationToken)
    {
        if (_lastStart is { } last && Every - time.GetElapsedTime(last) is var left && left > TimeSpan.Zero)
            await Task.Delay(left, time, cancellationToken);
        _lastStart = time.GetTimestamp();

        using var limit = new CancellationTokenSource(Limit, time);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            using var response = await api.ReadAsync(YouTubeLinks.WatchUrl(videoId), "json", limited.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("YouTube gave no title for {VideoId}: HTTP {Status}", videoId, (int)response.StatusCode);
                return null;
            }

            var title = Parse(await response.Content.ReadAsStringAsync(limited.Token));
            if (title is null) logger.LogDebug("YouTube's answer for {VideoId} holds no title", videoId);
            return title;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Deliberately not rethrown: a title only names the video in the plan and its transcript; a read that ran into its
            // own limit leaves the video its ID as its title, and it is tried all the same.
            logger.LogDebug(ex, "YouTube gave no title for {VideoId}: no answer within {Seconds} seconds", videoId, (int)Limit.TotalSeconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            // Deliberately not rethrown: as above; YouTube out of reach leaves the video its ID as its title.
            logger.LogDebug(ex, "YouTube gave no title for {VideoId}: the connection failed", videoId);
            return null;
        }
    }

    /// <summary>The title and channel in an oEmbed answer; null for an answer that is not JSON, or has no title.</summary>
    private static VideoTitle? Parse(string body)
    {
        OEmbedAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize(body, YouTubeJson.Default.OEmbedAnswer);
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: an answer in another shape is no answer; the caller says so at Debug and goes on without a title.
            return null;
        }

        // YouTube's words are shown and kept: none of them carries a control character from here on.
        if (VideoTitles.Plain(answer?.Title)?.Trim() is not { Length: > 0 } title) return null;

        var channel = string.IsNullOrWhiteSpace(answer!.AuthorName) ? null : VideoTitles.Plain(answer.AuthorName)!.Trim();
        var url = string.IsNullOrWhiteSpace(answer.AuthorUrl) ? null : VideoTitles.Plain(answer.AuthorUrl)!.Trim();
        return new VideoTitle(title, channel, HandleOf(url), url);
    }

    /// <summary>The @handle a channel's address names, such as @yourchannel in https://www.youtube.com/@yourchannel; null for another address.</summary>
    internal static string? HandleOf(string? channelUrl) =>
        Uri.TryCreate(channelUrl, UriKind.Absolute, out var address)
        && address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
        && Uri.UnescapeDataString(first) is { } handle && Settings.YouTubeOptions.IsChannel(handle) && handle.StartsWith('@')
            ? handle
            : null;
}

/// <summary>
/// Keeps a test from reaching YouTube itself (design 7.1): a handler on the oEmbed client that, while
/// <see cref="YouTubeGuards.RefuseTheRealThingsSwitch"/> is on, refuses any request to YouTube's own hosts, naming the stand-in
/// to use; a test's stub at a stub address passes. ClassScribe itself never turns the switch on, and then it passes
/// everything.
/// </summary>
public sealed class YouTubeHostGuard : DelegatingHandler
{
    /// <summary>YouTube's own hosts, in any letter case.</summary>
    private static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "youtube-nocookie.com", "www.youtube-nocookie.com"
    };

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { Host: var host } && YouTubeHosts.Contains(host))
            YouTubeGuards.RefuseInTests($"a request to {host}", "StubHttpHandler at a stub address (YouTube:OEmbedBaseUrl)");

        return base.SendAsync(request, cancellationToken);
    }
}
