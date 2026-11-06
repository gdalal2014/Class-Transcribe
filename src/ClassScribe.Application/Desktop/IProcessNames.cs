namespace ClassScribe.Desktop;

/// <summary>The program behind a process, as Windows names it.</summary>
/// <param name="Name">The program's name without .exe, such as chrome, the name <c>--app</c> and <c>Recording:MicrophoneFollows</c> match.</param>
/// <param name="Path">The program's full path, such as C:\Program Files\Google\Chrome\Application\chrome.exe; null when Windows gave the name alone.</param>
public sealed record ProcessImage(string Name, string? Path);

/// <summary>
/// Finds the program behind a process id, for the apps that have a microphone open. A process is opened only for a
/// limited query and closed at once; nothing is read but its program's path.
/// </summary>
public interface IProcessNames
{
    /// <summary>
    /// The program behind the process <paramref name="processId"/>, or null when the process has ended, or when it cannot be
    /// looked at, as another user's cannot. Windows gives the number of a process that has ended to a new one, so the answer
    /// is good for one look and is not kept.
    /// </summary>
    ProcessImage? Describe(int processId);
}
