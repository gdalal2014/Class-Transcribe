using System.IO.Pipes;
using System.Runtime.Versioning;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// A program started as ClassScribe starts its tools and its camera process (teach-video 6.2, YouTube design 3.2): through
/// Windows' own CreateProcessW, with an environment of its own (never the API key), three pipes for its standard handles and
/// no other handle, no window, the folder given, and a kill-on-close job of its own, made before it runs, so that it, and
/// everything it starts, ends when the job is closed or terminated, or when ClassScribe ends. This process keeps the ends it
/// writes the program's input to and reads its output and errors from. Disposing it closes the pipes and then the job,
/// which ends the program if it still runs.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PipedProcess : IDisposable
{
    /// <summary>The program's job: kill-on-close.</summary>
    private readonly ToolJob _job;

    /// <summary>A started program, with this process's ends of its pipes and its job.</summary>
    private PipedProcess(StartedProcess started, ToolJob job, AnonymousPipeServerStream input, AnonymousPipeServerStream output, AnonymousPipeServerStream errors)
    {
        Started = started;
        _job = job;
        Input = input;
        Output = output;
        Errors = errors;
    }

    /// <summary>The program, its id and start time, and its exit code once it has ended.</summary>
    public StartedProcess Started { get; }

    /// <summary>Where this process writes the program's standard input.</summary>
    public Stream Input { get; }

    /// <summary>Where this process reads the program's standard output.</summary>
    public Stream Output { get; }

    /// <summary>Where this process reads the program's standard error.</summary>
    public Stream Errors { get; }

    /// <summary>
    /// Starts <paramref name="application"/> with <paramref name="commandLine"/> in <paramref name="workingFolder"/>, with
    /// <paramref name="environment"/> as its whole environment, through <paramref name="api"/>. Null when Windows refuses,
    /// or the job would not take it (which ends it first), with Windows' error in <paramref name="error"/>; nothing is left
    /// open then, nor when the start throws, which is rethrown as it was. Throws a
    /// <see cref="System.ComponentModel.Win32Exception"/> when Windows will not make the job.
    /// </summary>
    public static PipedProcess? Start(IProcessCreationApi api, string application, string commandLine, string workingFolder,
        IReadOnlyDictionary<string, string> environment, out int error)
    {
        var job = ToolJob.Create();
        AnonymousPipeServerStream? input = null, output = null, errors = null;
        try
        {
            input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            errors = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var launch = new ProcessLaunch(application, commandLine, InheritHandles: true,
                ProcessCreationFlags.NoWindow | ProcessCreationFlags.UnicodeEnvironment, workingFolder, Show: null)
            {
                Environment = environment,
                Handles = new StandardHandles(input.ClientSafePipeHandle, output.ClientSafePipeHandle, errors.ClientSafePipeHandle),
                Job = job.Handle
            };

            StartedProcess? started;
            try
            {
                started = api.Start(launch, out error);
            }
            finally
            {
                // The program holds its own ends now; this process keeps only the ends it uses, so the pipes end with the program.
                input.DisposeLocalCopyOfClientHandle();
                output.DisposeLocalCopyOfClientHandle();
                errors.DisposeLocalCopyOfClientHandle();
            }

            if (started is not null) return new PipedProcess(started, job, input, output, errors);
        }
        catch
        {
            // The start threw, Windows' own or a test run's guard: nothing it made is left open, and the exception goes on as it was.
            LetGo(job, input, output, errors);
            throw;
        }

        LetGo(job, input, output, errors);
        return null;
    }

    /// <summary>Closes this process's ends of the pipes that were made, then the job, for a start that came to nothing.</summary>
    private static void LetGo(ToolJob job, AnonymousPipeServerStream? input, AnonymousPipeServerStream? output, AnonymousPipeServerStream? errors)
    {
        input?.Dispose();
        output?.Dispose();
        errors?.Dispose();
        job.Dispose();
    }

    /// <summary>Ends the program, and everything it started, at once, for a cancel or a program that hangs.</summary>
    public void Terminate() => _job.Terminate();

    /// <summary>Closes this process's ends of the pipes, then the job, which ends the program if it still runs.</summary>
    public void Dispose()
    {
        Input.Dispose();
        Output.Dispose();
        Errors.Dispose();
        _job.Dispose();
    }
}
