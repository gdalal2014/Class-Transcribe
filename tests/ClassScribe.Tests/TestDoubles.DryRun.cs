using System.Diagnostics;
using System.Text;

namespace ClassScribe.Tests;

/// <summary>
/// Windows PowerShell 5.1 for the dry-run kit's tests: a hidden process of the test's own, with no profile, running
/// commands from a script file (with -EncodedCommand, Windows PowerShell would copy every Write-Host line to the error
/// stream as CLIXML), in a temporary folder, within a time limit.
/// </summary>
public static class WindowsPowerShell
{
    /// <summary>Windows PowerShell 5.1's own executable.</summary>
    public static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Runs <paramref name="commands"/> from a script file written to <paramref name="folder"/>, after lines that stop at
    /// the first error and show no progress, and returns the exit code, the output and the error stream.
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Errors)> RunAsync(string folder, string commands, TimeSpan limit)
    {
        var file = Path.Combine(folder, $"commands-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(file, "$ErrorActionPreference = 'Stop'\r\n$ProgressPreference = 'SilentlyContinue'\r\n[Console]::OutputEncoding = [Text.Encoding]::UTF8\r\n" + commands, Encoding.UTF8);
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeLimit = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(timeLimit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output, await errors);
    }

    /// <summary>Text written as a PowerShell string in single quotes.</summary>
    public static string Quoted(string text) => "'" + text.Replace("'", "''") + "'";
}
