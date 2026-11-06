using System.Threading.Channels;
using ClassScribe.Commands;
using ClassScribe.Link;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// The recorder's console: it has no window, so its keys come from the windows that show the run, through the server, and
/// what it would show goes into the log, which those windows show. Somebody can always type, since a window can attach at
/// any moment; a read answers without a key only when it is cancelled, or once the server has stopped and no key can come
/// any more. Nobody can type a line into a recorder, so a line read is always none.
/// </summary>
/// <param name="server">The windows' end, whose keys the recording loop reads.</param>
/// <param name="logger">Where the text that would be shown goes.</param>
public sealed class LinkConsoleUi(RecorderServer server, ILogger<LinkConsoleUi> logger) : IConsoleUi
{
    /// <inheritdoc />
    public bool IsInteractive => true;

    /// <inheritdoc />
    public async Task<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await server.Keys.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: a read that was cancelled answers without a key, as the console's own read does.
            return null;
        }
        catch (ChannelClosedException)
        {
            // Deliberately not rethrown: the server has stopped serving the windows, so no key can come any more, which
            // answering without a key says; the recording loop then stops reading keys, and the recording goes on.
            logger.LogDebug("The windows' keys have run out: the server has stopped");
            return null;
        }
    }

    /// <summary>Nobody can type a line into a recorder: always null.</summary>
    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public void DiscardPendingKeys()
    {
        while (server.Keys.TryRead(out _))
        {
        }
    }

    /// <inheritdoc />
    public void Write(string text) => WriteLine(text);

    /// <inheritdoc />
    public void WriteLine(string text = "")
    {
        if (text.Length > 0) logger.LogInformation("{Text}", text);
    }
}
