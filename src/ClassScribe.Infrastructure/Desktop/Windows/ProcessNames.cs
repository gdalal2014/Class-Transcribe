using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// Finds the program behind a process id by asking Windows for the process's program path: the process is opened for a
/// limited query only (PROCESS_QUERY_LIMITED_INFORMATION), which works for the processes of other users' apps too, and
/// its handle is closed at once. Nothing is logged: a process that cannot be looked at is the answer, four times a second.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessNames : IProcessNames
{
    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION: enough to read a process's program path, and nothing more.</summary>
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>The longest path Windows can return.</summary>
    private const int LongestPath = 32_768;

    /// <inheritdoc />
    public ProcessImage? Describe(int processId)
    {
        if (processId <= 0) return null;

        using var process = OpenProcess(QueryLimitedInformation, inheritHandle: false, processId);
        if (process.IsInvalid) return null;

        var buffer = new char[LongestPath];
        var length = buffer.Length;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length)) return null;

        var path = new string(buffer, 0, length);
        return new ProcessImage(Path.GetFileNameWithoutExtension(path), path);
    }

    /// <summary>Opens a process for the access asked for; an invalid handle when Windows refuses, or no process has the id.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    /// <summary>The full path of a process's program.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] buffer, ref int length);
}
