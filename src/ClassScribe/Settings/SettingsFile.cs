using Microsoft.Extensions.Logging;

namespace ClassScribe.Settings;

/// <summary>
/// The bytes of appsettings.json as read at one moment, where the file is, and which file it was (see
/// <see cref="SettingsOrigin"/>). <see cref="Bytes"/> is null when there is no file.
/// </summary>
public sealed record SettingsText(string Path, byte[]? Bytes, SettingsOrigin Origin = SettingsOrigin.BesideTheProgram);

/// <summary>
/// The one appsettings.json ClassScribe reads its settings from: the user's own, in the user's folder, when it is there,
/// else the file next to the exe, decided at each read (see <see cref="SettingsPlaces"/>). Under dotnet run the latter is
/// the copy a build puts in bin\Debug\net10.0-windows10.0.19041.0, so it also says when the project's own file has changed
/// since that copy was made.
/// </summary>
public sealed class SettingsFile(SettingsPlaces places, ILogger<SettingsFile> logger)
{
    /// <summary>The name of the settings file.</summary>
    public const string FileName = "appsettings.json";

    /// <summary>How many times a read is tried again while another program holds the file.</summary>
    private const int Retries = 3;

    /// <summary>The pause before a read is tried again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>How a read waits before it tries again: the thread sleeps, unless a test plays the wait.</summary>
    private readonly Action<TimeSpan> _pause = Thread.Sleep;

    /// <summary>
    /// Reads the file <paramref name="places"/> names as the public constructor does, but waits with
    /// <paramref name="pause"/> before each new try instead of sleeping, so that a test can act at that moment, as the
    /// program that holds the file, or an editor that saves it, would.
    /// </summary>
    internal SettingsFile(SettingsPlaces places, ILogger<SettingsFile> logger, Action<TimeSpan> pause) : this(places, logger) => _pause = pause;

    /// <summary>Where the settings and the glossary are read from, decided at each read (see <see cref="SettingsPlaces"/>).</summary>
    public SettingsPlaces Places { get; } = places;

    /// <summary>
    /// The appsettings.json the person knows as theirs now: the user's own, even while it is missing, or the copy next to
    /// the exe (see <see cref="SettingsSource.Path"/>).
    /// </summary>
    public string Path => Places.SettingsPath;

    /// <summary>
    /// Reads the file the places name now (see <see cref="SettingsPlaces.Current"/>), sharing it with any program that has it
    /// open. A file another program holds for a moment, as OneDrive or an editor saving does, is tried again three times,
    /// 200 ms apart, and the last try's <see cref="IOException"/> is handed on. Without a file there are no settings of the
    /// file's own, and <see cref="SettingsText.Bytes"/> is null; when <paramref name="expectFile"/> is true, because the
    /// file was read before, a missing file is looked for again the same way first.
    /// </summary>
    public SettingsText Read(bool expectFile = false) => Read(Places.Current(), expectFile);

    /// <summary>
    /// The same for <paramref name="source"/>, as the places decided it a moment before: its <see cref="SettingsSource.ReadPath"/>
    /// is read, and the text carries its origin.
    /// </summary>
    public SettingsText Read(SettingsSource source, bool expectFile = false)
    {
        var path = source.ReadPath;
        for (var retry = 0; ; retry++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var bytes = new MemoryStream();
                file.CopyTo(bytes);
                return new SettingsText(path, bytes.ToArray(), source.Origin);
            }
            catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException) && expectFile && retry < Retries)
            {
                // Deliberately not rethrown: the file was there before, and an editor that saves by deleting it and renaming
                // a new one into its place leaves it missing for a moment, so it is looked for again shortly.
                logger.LogDebug(ex, "{Path} is not there just now; looking for it again in {Milliseconds} ms", path, RetryDelay.TotalMilliseconds);
                _pause(RetryDelay);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deliberately not rethrown: without the file there are no settings of the file's own, which the caller
                // weighs (see SettingsReloader).
                logger.LogDebug(ex, "There is no {Path}", path);
                return new SettingsText(path, null, source.Origin);
            }
            catch (IOException ex) when (retry < Retries)
            {
                // Deliberately not rethrown: another program, such as OneDrive or an editor saving, holds the file for a
                // moment, so it is read again shortly. The last try's failure is handed on.
                logger.LogDebug(ex, "{Path} is held by another program; reading it again in {Milliseconds} ms", path, RetryDelay.TotalMilliseconds);
                _pause(RetryDelay);
            }
        }
    }

    /// <summary>
    /// In the development layout, where the file read is the copy a build puts in &lt;project&gt;\bin\&lt;configuration&gt;\&lt;framework&gt;,
    /// says so when the project's own appsettings.json is newer than that copy and differs from it: an edit there does
    /// not count until the next build. Null otherwise: for a published app, which has no project behind it, and whenever
    /// the user's own file is the one read, or missing, since no build makes that copy.
    /// </summary>
    public string? DescribeStaleCopy()
    {
        if (Places.Origin != SettingsOrigin.BesideTheProgram) return null;

        var copy = Places.ProgramFile;
        var output = new DirectoryInfo(Places.ProgramFolder);
        var bin = output.Parent?.Parent;
        var project = bin?.Parent;
        if (bin is null || project is null || !bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)) return null;

        var source = System.IO.Path.Combine(project.FullName, FileName);
        try
        {
            if (!project.EnumerateFiles("*.csproj").Any() || !File.Exists(source) || !File.Exists(copy)) return null;
            if (File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(copy)) return null;
            if (File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(copy))) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the warning is a courtesy, and a file that cannot be looked at just now, such as
            // one an editor is saving, brings none this time. The settings themselves are read, strictly, by Read.
            logger.LogDebug(ex, "Could not compare {Source} with its copy {Copy}", source, copy);
            return null;
        }

        var shownSource = System.IO.Path.GetRelativePath(project.Parent?.Parent?.FullName ?? project.FullName, source);
        var shownCopy = System.IO.Path.GetRelativePath(project.FullName, output.FullName);
        return $"{shownSource} has changed since the last build; ClassScribe reads the copy in {shownCopy}. Build again, or edit the copy, for the change to count";
    }
}
