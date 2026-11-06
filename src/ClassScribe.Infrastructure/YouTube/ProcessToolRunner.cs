using System.Collections;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>
/// Runs a tool, such as yt-dlp, through Windows' own CreateProcessW (design 3.2; <see cref="PipedProcess"/>, which the camera
/// process is started through too), never through
/// <see cref="System.Diagnostics.Process"/>, which would start it running before it could be put in its job and would hand
/// it every inheritable handle of this process. The tool gets: an environment block of this process's environment without
/// ELEVENLABS_API_KEY or any ElevenLabs__ setting, and with PYTHONIOENCODING=utf-8, so that it writes UTF-8; three pipes for
/// its standard handles, and no other handle; no window; the folder the call names; and a job of its own, which ends it and
/// everything it started when the run is cancelled, when it is over, or when ClassScribe's window is closed. Its lines are
/// read as UTF-8, as they come. In a test run it refuses to run anything (see <see cref="YouTubeGuards"/>), unless a test made
/// it over a fake of Windows' start.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessToolRunner : IToolRunner
{
    /// <summary>How often a tool whose output has ended is looked at until it has ended too.</summary>
    private static readonly TimeSpan ExitPoll = TimeSpan.FromMilliseconds(20);

    /// <summary>Windows' start of a process, or a test's stand-in for it.</summary>
    private readonly IProcessCreationApi _api;

    /// <summary>True when the switch of <see cref="YouTubeGuards"/> keeps this runner from running anything in a test.</summary>
    private readonly bool _guarded;

    /// <summary>Where the starts and ends are logged, at Debug.</summary>
    private readonly ILogger _logger;

    /// <summary>A runner over Windows' own start of a process, refused in a test run.</summary>
    public ProcessToolRunner(ILogger<ProcessToolRunner> logger)
        : this(NativeProcessCreationApi.Instance, guarded: true, logger)
    {
    }

    /// <summary>A runner over <paramref name="api"/>; with <paramref name="guarded"/> false, it runs in a test run too, as the tests of the runner itself need.</summary>
    internal ProcessToolRunner(IProcessCreationApi api, bool guarded, ILogger logger)
    {
        _api = api;
        _guarded = guarded;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ToolResult> RunAsync(ToolCall call, Action<ToolLine>? onLine, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(call.Program);
        if (_guarded) YouTubeGuards.RefuseInTests($"the real {name}", "FakeToolRunner");
        cancellationToken.ThrowIfCancellationRequested();

        var commandLine = WindowsCommandLine.Of(call.Program, call.Arguments);
        using var piped = PipedProcess.Start(_api, call.Program, commandLine, call.WorkingFolder, ToolEnvironment(Environment.GetEnvironmentVariables()), out var error);
        if (piped is null)
        {
            _logger.LogDebug("Windows did not start {Program} (error {Error}): {CommandLine}", call.Program, error, commandLine);
            throw new Win32Exception(error, $"Windows did not start {name}: {new Win32Exception(error).Message.TrimEnd('.')} (error {error})");
        }

        var started = piped.Started;
        _logger.LogDebug("Started {Program} as process {ProcessId} in {Folder}: {CommandLine}", name, started.ProcessId, call.WorkingFolder, commandLine);

        // Nothing is ever typed into a tool: its input ends at once.
        piped.Input.Dispose();

        List<string> outputLines = [];
        List<string> errorLines = [];
        using (cancellationToken.Register(piped.Terminate))
        {
            await Task.WhenAll(
                ReadLinesAsync(piped.Output, line => Keep(outputLines, line, isError: false, onLine)),
                ReadLinesAsync(piped.Errors, line => Keep(errorLines, line, isError: true, onLine)));

            var exitCode = await ExitOfAsync(started);
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("{Program} (process {ProcessId}) ended with exit code {ExitCode}", name, started.ProcessId, exitCode);
            return new ToolResult(exitCode, outputLines, errorLines);
        }
    }

    /// <summary>
    /// The environment a tool gets: <paramref name="current"/>, this process's, without ELEVENLABS_API_KEY or any setting of
    /// the ElevenLabs section given as an environment variable (ElevenLabs__...), in any letter case, and with
    /// PYTHONIOENCODING=utf-8, so that a tool written in Python, as yt-dlp is, writes its lines in UTF-8.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ToolEnvironment(IDictionary current)
    {
        var environment = ChildEnvironment.WithoutTheKey(current);
        environment["PYTHONIOENCODING"] = "utf-8";
        return environment;
    }

    /// <summary>Keeps <paramref name="line"/> in <paramref name="lines"/> and hands it on.</summary>
    private static void Keep(List<string> lines, string line, bool isError, Action<ToolLine>? onLine)
    {
        lock (lines) lines.Add(line);
        onLine?.Invoke(new ToolLine(line, isError));
    }

    /// <summary>Reads <paramref name="pipe"/> to its end as UTF-8 lines, handing each to <paramref name="take"/>.</summary>
    private static async Task ReadLinesAsync(Stream pipe, Action<string> take)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (await reader.ReadLineAsync() is { } line) take(line);
    }

    /// <summary>The exit code of <paramref name="started"/>, once it has ended; its output has ended already, so the wait is short.</summary>
    private static async Task<int> ExitOfAsync(StartedProcess started)
    {
        while (true)
        {
            if (started.ExitCode() is { } exitCode) return exitCode;
            await Task.Delay(ExitPoll);
        }
    }
}
