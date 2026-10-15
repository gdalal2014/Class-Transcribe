using ClassScribe.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe;

/// <summary>Reads the command line and runs the matching command.</summary>
public sealed class App(IServiceProvider services, ILogger<App> logger)
{
    /// <summary>Help text shown for an unknown command.</summary>
    public const string Usage = """
        ClassScribe records the sound your computer plays and gets a transcript from ElevenLabs.

          ClassScribe                      Record now. Press Q to stop and transcribe.
          ClassScribe transcribe <path>    Transcribe a class folder, or any audio or video file.

        The API key is read from the ELEVENLABS_API_KEY environment variable.
        """;

    /// <summary>Runs the command named by <paramref name="args"/> and returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            switch (args)
            {
                case [] or ["record"]:
                    return await services.GetRequiredService<RecordCommand>().RunAsync();

                case ["transcribe", var path]:
                    return await services.GetRequiredService<TranscribeCommand>().RunAsync(path);

                default:
                    Console.WriteLine(Usage);
                    return args is ["help" or "--help" or "-h" or "/?"] ? 0 : 2;
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C during the upload: a normal way to leave, reported as such.
            logger.LogWarning("Cancelled");
            return 130;
        }
        catch (Exception ex)
        {
            // Last stop before the process exits: the failure becomes the log line and the exit code.
            logger.LogError(ex, "ClassScribe stopped because of an error");
            return 1;
        }
    }
}
