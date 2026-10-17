using ClassScribe.Recorder;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Shows how a run that recorded in this window ended. The run's own lines have said it in the window already, so what is
/// left is opening the results, as Recording:OpenFolderWhenDone and Recording:OpenTranscriptWhenDone say: the transcript's
/// folder and the transcript itself, or, for a recording made without a transcript, its folder. This window started the
/// run and is in front, so what opens comes to the front. A run asked to open nothing (--no-open) opens nothing, and a
/// run that ended without a result to show opens nothing either. Opening never throws (see <see cref="IDesktop"/>).
/// </summary>
public sealed class WindowResultShower(IDesktop desktop, IOptionsSnapshot<RecordingOptions> recording, ILogger<WindowResultShower> logger) : IResultShower
{
    /// <inheritdoc />
    public void Show(RunOutcome outcome, bool open)
    {
        if (!open)
        {
            logger.LogDebug("Nothing is opened at the end of this run, as it was asked (--no-open)");
            return;
        }

        var settings = recording.Value;
        switch (outcome)
        {
            case { Kind: OutcomeKind.Transcribed, TranscriptPath: { } transcript }:
                if (settings.OpenFolderWhenDone && Path.GetDirectoryName(transcript) is { Length: > 0 } folder) desktop.OpenFolder(folder);
                if (settings.OpenTranscriptWhenDone) desktop.OpenFile(transcript);
                break;

            case { Kind: OutcomeKind.Recorded, Folder: { } recorded } when settings.OpenFolderWhenDone:
                desktop.OpenFolder(recorded);
                break;
        }
    }
}
