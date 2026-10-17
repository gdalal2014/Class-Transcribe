namespace ClassScribe.Desktop;

/// <summary>
/// What Windows says about the end of the session: a restart, a shutdown or a sign-out. A console program like ClassScribe
/// hears nothing of these from the console, since it has user32 loaded (see <see cref="Commands.ProcessExitWarnings"/>), so
/// they come through a window of its own (<see cref="Windows.SessionWindow"/>), which also lets a recording make Windows
/// ask first.
/// </summary>
/// <remarks>
/// <para>
/// The events are raised on the window's own thread, one handler after another; a handler that fails is logged and the
/// others still run.
/// </para>
/// <para>
/// Windows allows little time, as Microsoft documents in "Application Shutdown Changes in Windows Vista". When it forces
/// the end, it gives about one second to answer its question and ends a program that takes longer; <see cref="Ended"/>
/// then has 30 seconds while a reason is held (see <see cref="BlockShutdown"/>), and 5 without. When it does not force the
/// end and no reason is held, it gives 5 seconds for each message and ends a program that takes longer. While a reason is
/// held, it waits for the answer, and shows its screen after 5 seconds. So an <see cref="Ending"/> handler does only quick
/// work, and only the quickest when <see cref="SessionEndEventArgs.Forced"/> is true, leaving the closing of files to
/// Ended; and no handler waits long for another thread, such as one that holds the recording's locks.
/// </para>
/// </remarks>
public interface ISessionEvents : IDisposable
{
    /// <summary>
    /// Windows asks whether it may end the session (WM_QUERYENDSESSION): bring the files on disk up to date, and note why
    /// the recording may be about to end, but close nothing. The arguments say what kind of end it is and whether Windows
    /// forces it; a forced end leaves about one second, enough to note why and little more. The session may still go on,
    /// as when the user cancels a restart that ClassScribe asked Windows to hold (see <see cref="EndingCancelled"/>), and
    /// Windows may ask again.
    /// </summary>
    event EventHandler<SessionEndEventArgs>? Ending;

    /// <summary>
    /// The session ends now (WM_ENDSESSION saying so, or an end Windows forces): close the files, so that what is on disk
    /// is complete. The arguments say what kind of end it is and whether Windows forces it. Windows may end the process at
    /// any moment once every handler has returned.
    /// </summary>
    event EventHandler<SessionEndEventArgs>? Ended;

    /// <summary>
    /// Windows is not ending the session after all (WM_ENDSESSION saying so), as when the user cancelled the restart or
    /// another program held it off: the recording goes on, and what <see cref="Ending"/> noted no longer holds.
    /// </summary>
    event EventHandler? EndingCancelled;

    /// <summary>
    /// With a <paramref name="reason"/>: from now on Windows asks the user before a restart, a shutdown or a sign-out the
    /// user starts, naming ClassScribe and showing the reason (ShutdownBlockReasonCreate); an end Windows forces is not
    /// held. With null: Windows may end the session again without asking (ShutdownBlockReasonDestroy). It returns at once,
    /// and the window's thread hands the change to Windows straight away, on purpose, rather than when Windows asks:
    /// Microsoft documents that when another program holds a restart first, Windows' screen lists a program, with its
    /// reason, only if the reason was registered before Windows asked that program. The reason wanted is also applied
    /// again before each of Windows' questions is answered. A reason Windows refuses is logged, never thrown, and then
    /// Windows is not held either.
    /// </summary>
    void BlockShutdown(string? reason);
}

/// <summary>The kind of end Windows announces in its session messages.</summary>
public enum SessionEndKind
{
    /// <summary>A restart or a shutdown; Windows does not say which.</summary>
    RestartOrShutdown,

    /// <summary>The user signs out of Windows (ENDSESSION_LOGOFF).</summary>
    SignOut,

    /// <summary>
    /// Windows closes programs without ending the session (ENDSESSION_CLOSEAPP): an update or an installer needs a file a
    /// program has open, Windows is being serviced, or it is short of resources.
    /// </summary>
    ClosingPrograms
}

/// <summary>What Windows says about the end it announces: what kind of end it is, and whether it forces it.</summary>
public sealed class SessionEndEventArgs(SessionEndKind kind, bool forced) : EventArgs
{
    /// <summary>A restart or a shutdown, a sign-out, or Windows closing programs.</summary>
    public SessionEndKind Kind { get; } = kind;

    /// <summary>
    /// True when Windows forces the end (ENDSESSION_CRITICAL), which no program can hold. Windows then gives about one
    /// second to answer its question, and ends a program that takes longer.
    /// </summary>
    public bool Forced { get; } = forced;
}
