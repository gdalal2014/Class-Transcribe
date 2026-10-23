using System.Text;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Transcription;

/// <summary>
/// Reads the glossary file: one term per line, blank lines and lines starting with # ignored, by the rules of
/// <see cref="KeytermRules"/>. Terms that break ElevenLabs' rules are skipped with a warning instead of failing the upload.
/// The file is looked for where the settings were read from first, then beside the program (see <see cref="ResolvePath"/>).
/// </summary>
public sealed class KeytermLoader(SettingsPlaces places, ILogger<KeytermLoader> logger)
{
    /// <summary>
    /// Where the configured glossary file is: environment variables such as %USERPROFILE% expanded, quotes around the
    /// path ignored, and a relative path taken from the folder the settings were read from when the file is there, else
    /// from the program's own folder: the user's own glossary first, the shipped one second (see
    /// <see cref="SettingsPlaces.ResolveFile"/>). An empty setting gives an empty path, which <see cref="Load"/> reads as
    /// no glossary.
    /// </summary>
    public string ResolvePath(string configured) => places.ResolveFile(configured);

    /// <summary>
    /// Loads the valid, distinct terms from <paramref name="path"/>, in composed (NFC) form. A missing file gives an
    /// empty list and a warning, which names both places when the user's own folder was looked in first; an empty path
    /// means no glossary was wanted.
    /// </summary>
    public IReadOnlyList<string> Load(string path)
    {
        if (path.Length == 0)
        {
            logger.LogInformation("No glossary is set in ElevenLabs:KeytermsFile; transcribing without one");
            return [];
        }

        if (!File.Exists(path))
        {
            if (places.Origin == SettingsOrigin.UserFolder && string.Equals(Path.GetDirectoryName(path), places.ProgramFolder, StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                logger.LogWarning(
                    "No glossary at {UserPath} or beside ClassScribe.exe, so the transcript is made without its terms. Put {Name} in the first folder, or set ElevenLabs:KeytermsFile to where it is",
                    Path.Combine(places.UserFolder!, name), name);
            }
            else
            {
                logger.LogWarning(
                    "No glossary at {Path}, so the transcript is made without its terms. Keep keyterms.txt next to ClassScribe.exe, or set ElevenLabs:KeytermsFile to where it is",
                    path);
            }

            return [];
        }

        var read = KeytermRules.Read(File.ReadLines(path, Encoding.UTF8), KeytermLimits.ElevenLabs);
        foreach (var skipped in read.Skipped) logger.LogWarning("Skipping keyterm on line {Line} ({Term}): {Problem}", skipped.Line, skipped.Term, skipped.Problem);
        if (read.OverLimit) logger.LogWarning("The keyterms file has more than {MaxTerms} terms; the rest are ignored", KeytermLimits.ElevenLabs.MaxTerms);
        return read.Terms;
    }
}
