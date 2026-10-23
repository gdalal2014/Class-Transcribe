using System.ComponentModel;
using System.Runtime.Versioning;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Link;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Camera.Windows;

/// <summary>
/// Starts camera processes through the same Windows start as the YouTube feature's tools (<see cref="PipedProcess"/>):
/// this very program, <c>"&lt;exe&gt;" --camera-process &lt;job id&gt;</c> (under dotnet, dotnet running ClassScribe's dll),
/// with no window, its three pipes and no other handle, the environment without the API key, ClassScribe's own folder
/// under %LOCALAPPDATA% as its working folder, and a kill-on-close job of its own, made before it runs, so that it ends with
/// the window or the recorder that started it. Its standard error is read line by line into the log at Debug, under the
/// category Camera. In a test run the real start refuses (see <see cref="DeviceGuards"/>), naming FakeCameraProcess;
/// Windows' own tool start refuses any program but cmd.exe there too.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CameraProcessStarter : ICameraProcessStarter
{
    /// <summary>The log's category for the camera process's lines.</summary>
    public const string LogCategory = "Camera";

    /// <summary>Windows' start of a process, or a test's stand-in.</summary>
    private readonly IProcessCreationApi _api;

    /// <summary>The program to start, which is this one.</summary>
    private readonly ThisProgram _program;

    /// <summary>The folder the camera process runs in.</summary>
    private readonly string _workingFolder;

    /// <summary>True when the device guard keeps the real start from running in a test.</summary>
    private readonly bool _guarded;

    /// <summary>The environment the camera process's is made from, without the key.</summary>
    private readonly Func<System.Collections.IDictionary> _environment;

    /// <summary>Where the starts and the camera process's standard error are logged.</summary>
    private readonly ILogger _logger;

    /// <summary>A starter over Windows' own start, from this program, in ClassScribe's own folder; refused in a test run.</summary>
    public CameraProcessStarter(RuntimeFolder folder, ILoggerFactory loggers)
        : this(NativeProcessCreationApi.Instance, ThisProgram.Current, folder.Root, guarded: true, Environment.GetEnvironmentVariables, loggers.CreateLogger(LogCategory))
    {
    }

    /// <summary>A starter over <paramref name="api"/>, as if this process ran <paramref name="program"/>; with <paramref name="guarded"/> false, it starts in a test run too.</summary>
    internal CameraProcessStarter(IProcessCreationApi api, ThisProgram program, string workingFolder, bool guarded, Func<System.Collections.IDictionary> environment, ILogger logger)
    {
        _api = api;
        _program = program;
        _workingFolder = workingFolder;
        _guarded = guarded;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public ICameraProcess Start(string jobId)
    {
        if (_guarded) DeviceGuards.RefuseInTests("the real start of a camera process", "FakeCameraProcess");
        if (!RunIds.IsWellFormed(jobId))
            throw new ArgumentException($"\"{jobId}\" is not a job id, such as 20261016-230002Z-0a1b2c3d; it goes on a command line, so nothing else is taken.", nameof(jobId));

        if (_program.CommandFor($"{CameraProtocol.ProcessSwitch} {jobId}") is not { } command)
            throw new Win32Exception(2, "Windows did not start the camera process: it did not say which program ClassScribe runs as (error 2)");

        var piped = PipedProcess.Start(_api, command.Application, command.CommandLine, _workingFolder, ChildEnvironment.WithoutTheKey(_environment()), out var error);
        if (piped is null)
        {
            _logger.LogDebug("Windows did not start the camera process (error {Error}): {CommandLine}", error, command.CommandLine);
            throw new Win32Exception(error, $"Windows did not start the camera process: {new Win32Exception(error).Message.TrimEnd('.')} (error {error})");
        }

        _logger.LogDebug("Started the camera process for job {JobId}: process {ProcessId}, {CommandLine}, in {Folder}", jobId, piped.Started.ProcessId, command.CommandLine, _workingFolder);
        return new StartedCameraProcess(piped, _logger);
    }

    /// <summary>A camera process this starter started: its lines over its pipes, its standard error into the log, and its job.</summary>
    private sealed class StartedCameraProcess : ICameraProcess
    {
        /// <summary>The process, its pipes and its job.</summary>
        private readonly PipedProcess _piped;

        /// <summary>The reading of its standard error.</summary>
        private readonly Task _errors;

        /// <summary>Starts reading the process's standard error into <paramref name="logger"/>, and joins its input and output into one channel.</summary>
        public StartedCameraProcess(PipedProcess piped, ILogger logger)
        {
            _piped = piped;
            Channel = new LineChannel(new JoinedStream(piped.Output, piped.Input));
            _errors = Task.Run(() => LogErrorsAsync(piped.Errors, logger));
        }

        /// <inheritdoc />
        public int ProcessId => _piped.Started.ProcessId;

        /// <inheritdoc />
        public LineChannel Channel { get; }

        /// <inheritdoc />
        public int? ExitCode => _piped.Started.ExitCode();

        /// <inheritdoc />
        public void Kill() => _piped.Terminate();

        /// <summary>Closes the channel, then the job, which ends the process if it still runs, and lets the reading of its errors finish.</summary>
        public async ValueTask DisposeAsync()
        {
            await Channel.DisposeAsync();
            _piped.Dispose();
            await _errors;
        }

        /// <summary>Writes each line of the camera process's standard error to the log at Debug, until it ends.</summary>
        private static async Task LogErrorsAsync(Stream errors, ILogger logger)
        {
            try
            {
                using var reader = new StreamReader(errors, leaveOpen: true);
                while (await reader.ReadLineAsync() is { } line) logger.LogDebug("Camera process: {Line}", line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Deliberately not rethrown: the pipe closed as the process or its starter let go, which ends the reading.
                logger.LogDebug(ex, "The camera process's error output closed");
            }
        }
    }
}
