namespace ClassScribe.Runs;

/// <summary>
/// The version of the link between a recorder and the windows that show its run, which every run file records beside the
/// build, so that a window can tell a recorder that speaks another version.
/// </summary>
public static class LinkVersion
{
    /// <summary>The version of the link's protocol this build speaks.</summary>
    public const int Current = 1;
}