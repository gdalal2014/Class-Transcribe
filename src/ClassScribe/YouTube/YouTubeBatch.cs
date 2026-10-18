using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.YouTube;

/// <summary>
/// What every YouTube batch shares, on either path (design 2): one batch at a time under youtube.lock, the laptop kept awake
/// from the plan to the summary, the ElevenLabs key checked and the account read before anything is spent, the keys Q and Q
/// twice, the wait while a class records, the transcripts, and the course folder opened once at the end.
/// </summary>
public sealed class YouTubeBatch(
    YouTubeLock youTubeLock,
    ClassWait wait,
    BatchTranscripts transcripts,
    IElevenLabsAccount account,
    UsageReport usage,
    IPowerRequests power,
    IPowerSource powerSource,
    IConsoleUi ui,
    IDesktop desktop,
    IOptionsSnapshot<RecordingOptions> recording,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<YouTubeBatch> logger)
{
    /// <summary>What the batch's keys do, said once as the fetching begins.</summary>
    public const string KeysLine = "Q stops after the video in hand; Q twice within 5 seconds, or Ctrl+C, stops at once.";

    /// <summary>The wait while a class records.</summary>
    public ClassWait Wait => wait;

    /// <summary>The batch's transcripts.</summary>
    public BatchTranscripts Transcripts => transcripts;

    /// <summary>
    /// Takes youtube.lock and returns what lets it go; or null, after saying which window holds it, when another batch runs:
    /// "Another ClassScribe window (process 1234) is working through YouTube videos; wait for it to finish, or close it."
    /// </summary>
    public IDisposable? TakeLock()
    {
        if (youTubeLock.TryTake(out var holder) is { } held) return held;

        var who = holder is { } id ? $"Another ClassScribe window (process {id})" : "Another ClassScribe window";
        logger.LogError("{Who} is working through YouTube videos; wait for it to finish, or close it", who);
        return null;
    }

    /// <summary>Keeps the laptop awake until the result is disposed, the screen as Recording:KeepScreenOn says, for the whole batch.</summary>
    public IDisposable KeepAwake()
    {
        var screen = recording.Value.KeepScreenOn;
        return power.Hold(StayAwake.VideosReason, () => StayAwake.Needs(screen, powerSource));
    }

    /// <summary>
    /// True when there is an ElevenLabs key; otherwise the batch is refused before anything is fetched, with what to do: a
    /// batch must not spend hours on transcripts it cannot make without saying so first.
    /// </summary>
    public bool HasKey()
    {
        if (!string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey)) return true;

        logger.LogError("No ElevenLabs API key found in {Variable}. Set it (README, Set up once), or add --no-transcribe to fetch only", ElevenLabsOptions.ApiKeyVariable);
        return false;
    }

    /// <summary>The account's standing for the plan, read once (see <see cref="UsageReport.Describe"/>); null when nothing was asked. Never throws but the caller's cancel.</summary>
    public async Task<string?> AccountLineAsync(CancellationToken cancellationToken) => usage.Describe(await account.ReadAsync(cancellationToken));

    /// <summary>Watches the keys while the batch fetches and transcribes (see <see cref="BatchKeys"/>), until the result is disposed.</summary>
    public BatchKeys WatchKeys(CancellationToken cancellationToken) => new(ui, time, logger, cancellationToken);

    /// <summary>
    /// Opens the course's folder once at the end, unless --no-open was given or Recording:OpenFolderWhenDone is off; never each
    /// transcript. A folder that is not there is not opened.
    /// </summary>
    public void OpenCourse(string? courseFolder, bool open)
    {
        if (!open || !recording.Value.OpenFolderWhenDone || courseFolder is null || !Directory.Exists(courseFolder)) return;

        desktop.OpenFolder(courseFolder);
    }
}

/// <summary>
/// The keys of a batch while it fetches and transcribes (design 3.9): Q stops after the video or transcript in hand, and
/// says so; Q again within 5 seconds stops at once, as Ctrl+C does, cancelling what is in hand. Read only where someone can
/// type; on the steady clock.
/// </summary>
public sealed class BatchKeys : IDisposable
{
    /// <summary>How soon a second Q stops at once.</summary>
    public static readonly TimeSpan SecondPressWithin = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopAtOnce;
    private readonly CancellationTokenSource _done = new();
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Task _watching;
    private long? _firstQ;
    private volatile bool _stopAfterThis;

    /// <summary>Starts reading <paramref name="ui"/>'s keys, while someone can type, linked to <paramref name="cancellationToken"/>.</summary>
    internal BatchKeys(IConsoleUi ui, TimeProvider time, ILogger logger, CancellationToken cancellationToken)
    {
        _time = time;
        _logger = logger;
        _stopAtOnce = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _watching = ui.IsInteractive ? WatchAsync(ui) : Task.CompletedTask;
    }

    /// <summary>Cancelled by Q twice, or by the batch's own cancel.</summary>
    public CancellationToken Token => _stopAtOnce.Token;

    /// <summary>True once Q asked to stop after what is in hand.</summary>
    public bool StopAfterThis => _stopAfterThis;

    /// <summary>
    /// Begins the batch's next step, the transcripts after the fetching: a Q that stopped the fetching does not stop the
    /// transcripts of what was fetched, so it is forgotten; a Q from now on stops after the transcript in hand.
    /// </summary>
    public void NextStep() => _stopAfterThis = false;

    /// <summary>Stops reading the keys.</summary>
    public void Dispose()
    {
        _done.Cancel();
        try
        {
            _watching.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            // Deliberately not rethrown: the watch ends with the cancel that stops it, which is how it is meant to end.
        }

        _stopAtOnce.Dispose();
        _done.Dispose();
    }

    /// <summary>Reads the keys until the batch is over: Q once stops after what is in hand, twice within 5 seconds at once.</summary>
    private async Task WatchAsync(IConsoleUi ui)
    {
        while (!_done.IsCancellationRequested)
        {
            var key = await ui.ReadKeyAsync(_done.Token);
            if (key is not { } pressed) return;
            if (pressed.Key != ConsoleKey.Q || pressed.Modifiers.HasFlag(ConsoleModifiers.Control)) continue;

            var now = _time.GetTimestamp();
            if (_firstQ is { } first && _time.GetElapsedTime(first, now) <= SecondPressWithin)
            {
                _logger.LogWarning("Stopping at once");
                await _stopAtOnce.CancelAsync();
                return;
            }

            _firstQ = now;
            _stopAfterThis = true;
            _logger.LogInformation("Stopping after the one in hand. Press Q again within 5 seconds to stop at once");
        }
    }
}
