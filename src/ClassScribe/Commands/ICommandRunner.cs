using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Commands;

/// <summary>Runs the app's commands. Each run gets a scope of its own: fresh Ctrl+C signals and a fresh recording session.</summary>
public interface ICommandRunner
{
    /// <summary>Records under <paramref name="name"/> until stopped, then transcribes when <paramref name="transcribe"/> is true. Returns the exit code.</summary>
    Task<int> RecordAsync(string name, bool transcribe);

    /// <summary>Transcribes a class folder or a single file. Returns the exit code.</summary>
    Task<int> TranscribeAsync(string path);
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
    public async Task<int> TranscribeAsync(string path)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TranscribeCommand>().RunAsync(path);
    }
}
