namespace ClassScribe.YouTube;

/// <summary>The tools a download needs, each by its full path.</summary>
/// <param name="YtDlp">yt-dlp.exe.</param>
/// <param name="Deno">deno.exe, the JavaScript runtime yt-dlp solves YouTube's challenges with (--js-runtimes).</param>
/// <param name="FfmpegFolder">The folder of ffmpeg.exe and ffprobe.exe (--ffmpeg-location).</param>
public sealed record ToolPaths(string YtDlp, string Deno, string FfmpegFolder);
