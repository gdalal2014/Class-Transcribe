using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>Opens folders and files the Windows way. A failure is logged rather than thrown: showing the result is a convenience.</summary>
public sealed class WindowsDesktop(ILogger<WindowsDesktop> logger) : IDesktop
{
    /// <inheritdoc />
    public void OpenFolder(string path) => Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false }, path);

    /// <inheritdoc />
    public void OpenFile(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true }, path);

    /// <inheritdoc />
    public void OpenInNotepad(string path) => Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false }, path);

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
