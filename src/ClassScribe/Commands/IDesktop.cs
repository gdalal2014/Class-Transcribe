using System.ComponentModel;
using System.Diagnostics;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>Shows folders and files to the person at the computer.</summary>
public interface IDesktop
{
    /// <summary>Opens a folder in Explorer.</summary>
    void OpenFolder(string path);

    /// <summary>Opens a file in the app Windows uses for its type, such as Notepad for a .txt file.</summary>
    void OpenFile(string path);
}

/// <summary>Opens folders and files the Windows way. A failure is logged rather than thrown: showing the result is a convenience.</summary>
public sealed class WindowsDesktop(ILogger<WindowsDesktop> logger) : IDesktop
{
    /// <inheritdoc />
    public void OpenFolder(string path) => Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false }, path);

    /// <inheritdoc />
    public void OpenFile(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true }, path);

    /// <summary>Starts the program that shows <paramref name="path"/>.</summary>
    private void Start(ProcessStartInfo start, string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Deliberately not rethrown: the work is done and saved; only showing it failed.
            logger.LogWarning(ex, "Could not open {Path}", path);
        }
    }
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
