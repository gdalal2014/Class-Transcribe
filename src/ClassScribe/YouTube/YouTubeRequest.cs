namespace ClassScribe.YouTube;

/// <summary>How a batch goes ahead once its plan is shown.</summary>
public enum BatchConfirmation
{
    /// <summary>--plan: the plan is printed and nothing more; nothing is fetched, recorded or sent.</summary>
    PlanOnly,

    /// <summary>--yes: the plan is printed and the batch goes ahead with the videos that need no word.</summary>
    Yes,

    /// <summary>The menu: each video that needs Gulzar's word is asked about, then YES for the batch.</summary>
    Ask
}

/// <summary>One batch of YouTube videos, as the youtube command or menu choice Y asks for it (design 1).</summary>
/// <param name="Links">The links, read and each taken once (see <see cref="LinkList"/>).</param>
/// <param name="Name">The course the videos belong to: the folder they go to, as a class's name names it.</param>
/// <param name="OutputRoot">The recordings folder, as a full path: --output, or Recording:OutputFolder in the menu.</param>
/// <param name="Download">True to download the audio with yt-dlp (--download, or D in the menu).</param>
/// <param name="Confirmation">How the batch goes ahead once its plan is shown.</param>
/// <param name="Transcribe">True to transcribe each new recording; false for --no-transcribe.</param>
/// <param name="Open">False to open nothing at the end (--no-open); otherwise the course folder opens once, as the settings say.</param>
/// <param name="Again">True to fetch videos fetched before as new recordings (--again); transcribing them is billed again.</param>
/// <param name="AllowUnlisted">
/// True to accept every unlisted video of the batch on Gulzar's word given once (--allow-unlisted), refused while
/// YouTube:CourseChannels is empty; it never overrides any other refusal of the guard.
/// </param>
public sealed record YouTubeRequest(IReadOnlyList<YouTubeLink> Links, string Name, string OutputRoot, bool Download, BatchConfirmation Confirmation,
    bool Transcribe = true, bool Open = true, bool Again = false, bool AllowUnlisted = false);
