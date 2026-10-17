using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// How a run in a recorder shows its end. The run's own lines have said how it ended, in the log the windows show. Opening
/// the results, the transcript and its folder, is the window's that started the run, or the last window a person can type
/// into, which the server tells when it sends the end; only when no window takes the end does the recorder open them
/// itself, so that the transcript appearing is the sign that the work is done. So the end is kept here until the host
/// knows whether a window took it (see <see cref="OpenResults"/>). One per run.
/// </summary>
/// <param name="opening">What opens the results as the settings say, the same as in a window.</param>
/// <param name="logger">Where the opening is noted.</param>
public sealed class RecorderResultShower(WindowResultShower opening, ILogger<RecorderResultShower> logger) : IResultShower
{
    private readonly Lock _gate = new();

    /// <summary>How the run ended, once it has; null before.</summary>
    private RunOutcome? _outcome;

    /// <summary>Whether the results are to be opened at all: false with --no-open.</summary>
    private bool _open;

    /// <summary>Keeps how the run ended, and whether the results are to be opened, for <see cref="OpenResults"/>.</summary>
    public void Show(RunOutcome outcome, bool open)
    {
        lock (_gate)
        {
            _outcome = outcome;
            _open = open;
        }

        logger.LogDebug("The run ended ({Kind}); whether to open the results is settled once the end has gone to the windows", outcome.Kind);
    }

    /// <summary>
    /// Opens the results as the settings say, because no window took the end to open them. Returns true when they were
    /// opened; false when there is nothing to open, as after a failure, or the run was asked to open nothing (--no-open).
    /// The log says that the recorder opens the results only when there are results to open.
    /// </summary>
    public bool OpenResults()
    {
        RunOutcome? outcome;
        bool open;
        lock (_gate) (outcome, open) = (_outcome, _open);

        if (outcome is null)
        {
            logger.LogDebug("The run ended without an outcome to show, so there is nothing to open");
            return false;
        }

        if (!open)
        {
            logger.LogDebug("Nothing is opened at the end of this run, as it was asked (--no-open)");
            return false;
        }

        if (outcome is not ({ Kind: OutcomeKind.Transcribed, TranscriptPath: not null } or { Kind: OutcomeKind.Recorded, Folder: not null }))
        {
            logger.LogDebug("The run ended ({Kind}) with no results to open", outcome.Kind);
            return false;
        }

        logger.LogInformation("No ClassScribe window was showing this recording at its end, so the recorder opens the results itself");
        opening.Show(outcome, open: true);
        return true;
    }
}
