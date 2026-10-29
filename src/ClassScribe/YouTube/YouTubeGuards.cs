using ClassScribe.Recorder;

namespace ClassScribe.YouTube;

/// <summary>
/// What keeps a test from the real things of the YouTube feature (design 7.1): a test run turns on
/// <see cref="RefuseTheRealThingsSwitch"/> (TestGuards, the assembly's set-up), and then the real tool runner and Windows'
/// own start of a tool refuse to run yt-dlp, Deno or FFmpeg, naming the fake to use instead, so that a test that forgets a
/// fake fails rather than reaching YouTube from the laptop. ClassScribe itself never turns it on. Every folder of the
/// feature is derived from ClassScribe's own folder (<see cref="IRuntimeFolder"/>), whose adapter's own switch
/// (<c>RuntimeFolder.RefuseTheRealFolderSwitch</c>) keeps tests out of the real one.
/// </summary>
public static class YouTubeGuards
{
    /// <summary>The AppContext switch a test run turns on: with it on, the real tools, windows and youtube.com are refused.</summary>
    public const string RefuseTheRealThingsSwitch = "ClassScribe.YouTube.RefuseTheRealThings";

    /// <summary>True while <see cref="RefuseTheRealThingsSwitch"/> is on, as in a test run.</summary>
    public static bool RefusingTheRealThings => AppContext.TryGetSwitch(RefuseTheRealThingsSwitch, out var refused) && refused;

    /// <summary>
    /// Throws, while <see cref="RefuseTheRealThingsSwitch"/> is on, an <see cref="InvalidOperationException"/> saying that a test
    /// asked for <paramref name="what"/> and naming <paramref name="fake"/>, what to use instead; does nothing otherwise.
    /// </summary>
    public static void RefuseInTests(string what, string fake)
    {
        if (RefusingTheRealThings)
            throw new InvalidOperationException($"A test asked for {what}, which reaches the real world (YouTube, a real program or a real window). Use {fake} instead.");
    }
}
