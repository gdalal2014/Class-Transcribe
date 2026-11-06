using ClassScribe.Settings;
using ClassScribe.Transcription;

namespace ClassScribe.Commands;

/// <summary>Shows folders and files to the person at the computer.</summary>
public interface IDesktop
{
    /// <summary>Opens a folder in Explorer.</summary>
    void OpenFolder(string path);

    /// <summary>Opens a file in the app Windows uses for its type, such as Notepad for a .txt file.</summary>
    void OpenFile(string path);

    /// <summary>Opens a text file in Notepad, whatever app Windows uses for its type, as settings open does with the settings file.</summary>
    void OpenInNotepad(string path);
}

/// <summary>Showing a finished transcript, as the settings ask.</summary>
public static class DesktopExtensions
{
    /// <summary>Opens the transcript's folder and then the transcript itself, each when its setting is on.</summary>
    public static void ShowTranscript(this IDesktop desktop, RecordingOptions settings, TranscriptFiles files)
    {
        if (settings.OpenFolderWhenDone) desktop.OpenFolder(Path.GetDirectoryName(files.TranscriptPath)!);
        if (settings.OpenTranscriptWhenDone) desktop.OpenFile(files.TranscriptPath);
    }
}
