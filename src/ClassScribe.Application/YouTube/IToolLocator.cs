namespace ClassScribe.YouTube;

/// <summary>
/// Where the record path's browser is (architecture 4.3; design 4.1): Chrome, as YouTube:BrowserPath names it, else as
/// Windows' App Paths or its install folders say. yt-dlp, Deno and FFmpeg, the download path's tools, are found by the
/// check of <see cref="IVideoTool"/>. It lives as long as the app, and reads the setting at each search.
/// </summary>
public interface IToolLocator
{
    /// <summary>What a batch says when Chrome is nowhere to be found.</summary>
    const string BrowserNotFound = "Chrome was not found (YouTube:BrowserPath, App Paths, Program Files, LOCALAPPDATA). Install it, or set YouTube:BrowserPath to its chrome.exe";

    /// <summary>Chrome's chrome.exe, as a full path; null when it is nowhere to be found (see <see cref="BrowserNotFound"/>).</summary>
    string? FindBrowser();

    /// <summary>Chrome's version, as its file gives it, for the video's source file: "Chrome 141.0.7390.108", or "Chrome" when the file does not say.</summary>
    string BrowserVersionOf(string chrome);
}
