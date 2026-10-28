namespace ClassScribe.YouTube;

/// <summary>One run of a tool, such as yt-dlp: the program, its arguments as they are to reach it, and the folder it runs in.</summary>
/// <param name="Program">The program's full path.</param>
/// <param name="Arguments">Its arguments, each as the program is to read it; quoting them is the runner's.</param>
/// <param name="WorkingFolder">The folder it runs in: for yt-dlp, the video's staging folder.</param>
public sealed record ToolCall(string Program, IReadOnlyList<string> Arguments, string WorkingFolder);

/// <summary>One line a tool wrote, as it came.</summary>
/// <param name="Text">The line, without its line break.</param>
/// <param name="IsError">True for a line of standard error, where yt-dlp writes its warnings and errors.</param>
public sealed record ToolLine(string Text, bool IsError);

/// <summary>What a tool's run came to.</summary>
/// <param name="ExitCode">The tool's exit code.</param>
/// <param name="Output">Every line of its standard output, in order.</param>
/// <param name="Errors">Every line of its standard error, in order.</param>
public sealed record ToolResult(int ExitCode, IReadOnlyList<string> Output, IReadOnlyList<string> Errors);

/// <summary>
/// Runs a tool to its end (design 3.2), behind an interface so that no test ever runs a real one: the batch's tests play yt-dlp
/// with a fake that writes the files it would. The real one is <see cref="ProcessToolRunner"/>.
/// </summary>
public interface IToolRunner
{
    /// <summary>
    /// Runs <paramref name="call"/> to its end, handing each line to <paramref name="onLine"/> as it comes, and returns its exit
    /// code and lines. A cancel from <paramref name="cancellationToken"/> ends the tool and everything it started, and is thrown
    /// as an <see cref="OperationCanceledException"/>. A tool Windows will not start is thrown as a
    /// <see cref="System.ComponentModel.Win32Exception"/>.
    /// </summary>
    Task<ToolResult> RunAsync(ToolCall call, Action<ToolLine>? onLine, CancellationToken cancellationToken);
}
