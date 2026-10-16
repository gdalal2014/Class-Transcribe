using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Commands;

/// <summary>Runs the app's commands. Each run gets a scope of its own: fresh Ctrl+C signals and a fresh recording session.</summary>
public interface ICommandRunner
{
    /// <summary>Records under <paramref name="name"/> until stopped, then transcribes when <paramref name="transcribe"/> is true. Returns the exit code.</summary>
    Task<int> RecordAsync(string name, bool transcribe);

    /// <summary>Transcribes what the request names: a class folder, a file of a recording, or any other file. Returns the exit code.</summary>
    Task<int> TranscribeAsync(TranscribeRequest request);

    /// <summary>
    /// Rebuilds, for free, the transcript of a class folder, of a file of a recording, or of an answer from ElevenLabs saved
    /// as .json, and opens it at the end when <paramref name="open"/> is true and the settings ask. Returns the exit code.
    /// </summary>
    Task<int> RebuildAsync(string path, bool open);
}

/// <summary>Runs each command in a new scope of the service container.</summary>
public sealed class CommandRunner(IServiceScopeFactory scopes) : ICommandRunner
{
    /// <inheritdoc />
    public async Task<int> RecordAsync(string name, bool transcribe)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RecordCommand>().RunAsync(name, transcribe);
    }

    /// <inheritdoc />
    public async Task<int> TranscribeAsync(TranscribeRequest request)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TranscribeCommand>().RunAsync(request);
    }

    /// <inheritdoc />
    public async Task<int> RebuildAsync(string path, bool open)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RebuildCommand>().RunAsync(path, open);
    }
}
