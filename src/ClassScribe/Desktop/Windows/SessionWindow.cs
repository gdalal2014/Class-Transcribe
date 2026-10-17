using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// ClassScribe's own window for the end of the session (see <see cref="ISessionEvents"/>): a hidden top-level window on a
/// thread of its own, with its own message loop, made with the object and destroyed when it is disposed.
/// </summary>
/// <remarks>
/// <para>
/// It is a top-level window because Windows sends its session messages to top-level windows; a message-only window gets
/// none. It is never shown. Its title, ClassScribe, is the name Windows' shutdown screen gives the program that holds the
/// session. Each object registers a kind of window of its own, so that several can live in one process, as in tests.
/// </para>
/// <para>
/// Windows lets only the window's own thread set or remove its reason (ShutdownBlockReasonCreate and
/// ShutdownBlockReasonDestroy). So <see cref="BlockShutdown"/> notes the reason wanted and posts a message, and the
/// window's thread hands the reason to Windows as soon as it takes that message; nothing ever waits for the window's
/// thread. Handing it over at once, rather than when Windows asks, is the point: Microsoft documents that when another
/// program holds a restart first, Windows' screen lists a program, with its reason, only if the reason was registered
/// before Windows asked that program. The window also applies the reason wanted again before each answer, so that a
/// message that could not be posted, or a reason set while a handler ran, still counts.
/// </para>
/// <para>
/// Microsoft also documents that a program without a visible window that answers no without a reason Windows holds is
/// ended, and that one holding a reason is treated like a program with a visible window. So the answer is no only while
/// Windows holds the reason, and never for an end Windows forces.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionWindow : ISessionEvents
{
    /// <summary>The window's title, which Windows' shutdown screen shows as the name of the program that holds the session.</summary>
    internal const string Title = "ClassScribe";

    /// <summary>WM_DESTROY: the window is being destroyed.</summary>
    private const uint DestroyMessage = 0x0002;

    /// <summary>WM_CLOSE: a request from outside to close the window, as taskkill without /F sends.</summary>
    private const uint CloseRequestMessage = 0x0010;

    /// <summary>WM_QUERYENDSESSION: Windows asks whether it may end the session.</summary>
    private const uint QueryEndSessionMessage = 0x0011;

    /// <summary>WM_ENDSESSION: Windows says whether the session ends.</summary>
    private const uint EndSessionMessage = 0x0016;

    /// <summary>WM_APP + 1, ClassScribe's own: apply the reason wanted now.</summary>
    internal const uint ApplyMessage = 0x8001;

    /// <summary>
    /// WM_APP + 2, ClassScribe's own: the object was disposed, so destroy the window. The window ignores it otherwise, since
    /// another program may post or broadcast the same number.
    /// </summary>
    internal const uint CloseMessage = 0x8002;

    /// <summary>ENDSESSION_CLOSEAPP: Windows closes programs for an update or a repair.</summary>
    private const uint CloseApp = 0x1;

    /// <summary>ENDSESSION_CRITICAL: an end Windows forces, which no program can hold.</summary>
    private const uint Critical = 0x40000000;

    /// <summary>ENDSESSION_LOGOFF: the user signs out.</summary>
    private const uint Logoff = 0x80000000;

    /// <summary>How long <see cref="Dispose"/> waits for the window's thread to end.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<SessionWindow> _logger;

    /// <summary>Windows' functions for the window's reason, or a test's stand-in for them.</summary>
    private readonly IShutdownReasonApi _reasons;

    /// <summary>The name of this object's own kind of window.</summary>
    private readonly string _className;

    /// <summary>The window procedure, kept here for as long as the window lives, because Windows holds only a pointer to it.</summary>
    private readonly WindowProcedure _procedure;

    private readonly Thread _thread;
    private readonly Lock _gate = new();

    /// <summary>The window, or zero when Windows would not make it. Under the lock.</summary>
    private IntPtr _window;

    /// <summary>The reason <see cref="BlockShutdown"/> last asked for, or null for none. Under the lock.</summary>
    private string? _wanted;

    /// <summary>True once disposed. Under the lock.</summary>
    private bool _disposed;

    /// <summary>
    /// True once the window is being destroyed: nothing is posted to it from then on, since Windows may give its handle to
    /// another window afterwards. Under the lock.
    /// </summary>
    private bool _closing;

    /// <summary>The reason Windows holds for the window now, or null. On the window's thread only.</summary>
    private string? _held;

    /// <summary>
    /// The reason whose refusal was logged, while it stays wanted: it is tried again each time it is applied, but its
    /// refusal is not logged again. On the window's thread only.
    /// </summary>
    private string? _refused;

    /// <summary>True once the window was destroyed. On the window's thread only.</summary>
    private bool _destroyed;

    /// <summary>Makes the window, on a thread of its own, and returns once it is there or Windows refused it, which is logged.</summary>
    public SessionWindow(ILogger<SessionWindow> logger)
        : this(logger, reasons: null)
    {
    }

    /// <summary>
    /// Makes the window with the reason set and removed through <paramref name="reasons"/>, and its kind of window
    /// registered as <paramref name="className"/>, when given; otherwise through Windows' own functions, under a name of
    /// its own. Tests pass a stand-in for Windows' functions, or a name Windows refuses.
    /// </summary>
    internal SessionWindow(ILogger<SessionWindow> logger, IShutdownReasonApi? reasons = null, string? className = null)
    {
        _logger = logger;
        _reasons = reasons ?? NativeShutdownReasonApi.Instance;
        _className = className ?? $"ClassScribe.SessionWindow.{Guid.NewGuid():N}";
        _procedure = OnMessage;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "ClassScribe session window" };
        _thread.Start();

        // An exception on the window's thread while the window was being made is thrown here.
        started.Task.GetAwaiter().GetResult();

        // Without a window the thread has nothing left to do; it ends at once, and is not left behind.
        if (Handle == IntPtr.Zero) _thread.Join();
    }

    /// <inheritdoc />
    public event EventHandler<SessionEndEventArgs>? Ending;

    /// <inheritdoc />
    public event EventHandler<SessionEndEventArgs>? Ended;

    /// <inheritdoc />
    public event EventHandler? EndingCancelled;

    /// <summary>The window, or zero when Windows would not make it.</summary>
    internal IntPtr Handle
    {
        get { lock (_gate) return _window; }
    }

    /// <summary>True while the window's thread runs.</summary>
    internal bool ThreadAlive => _thread.IsAlive;

    /// <summary>
    /// True when the window's thread is a background thread, which never keeps the process running, so that a window
    /// nobody disposed never keeps a finished recorder alive. Read it while the thread runs.
    /// </summary>
    internal bool ThreadIsBackground => _thread.IsBackground;

    /// <inheritdoc />
    public void BlockShutdown(string? reason)
    {
        int error;
        lock (_gate)
        {
            if (_disposed) return;

            _wanted = reason;
            if (_window == IntPtr.Zero || _closing) return;

            // Posted, never sent, so that the caller never waits for the window's thread, which hands the change to
            // Windows as soon as it takes the message. Doing that at once, rather than when Windows asks, is the point:
            // when another program holds a restart first, Windows' screen lists a program only if its reason was
            // registered before Windows asked it. Posted under the lock, so that nothing is posted once the window is
            // being destroyed.
            if (PostMessageW(_window, ApplyMessage, IntPtr.Zero, IntPtr.Zero)) return;
            error = Marshal.GetLastPInvokeError();
        }

        LogNotHandedOver(reason, error);
    }

    /// <summary>
    /// Lets go of the reason held, destroys the window and waits, up to five seconds, for its thread to end. From a
    /// handler, on the window's own thread, it does not wait: the thread ends once the handler returns. Disposing again
    /// does nothing.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            _wanted = null;
            if (_window == IntPtr.Zero || _closing) return;

            if (!PostMessageW(_window, CloseMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _logger.LogWarning(
                    "Could not close the window that watches for a restart, a shutdown or a sign-out ({Error}); it closes with ClassScribe",
                    DescribeError(Marshal.GetLastPInvokeError()));
                return;
            }
        }

        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) return;

        if (!_thread.Join(CloseTimeout))
        {
            _logger.LogWarning(
                "The window that watches for a restart, a shutdown or a sign-out did not close within {Seconds} seconds; it closes with ClassScribe",
                CloseTimeout.TotalSeconds);
        }
    }

    /// <summary>The window's thread: makes the window, runs its message loop until the window is destroyed, then tidies up.</summary>
    private void Run(TaskCompletionSource started)
    {
        try
        {
            var window = Open();
            lock (_gate) _window = window;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on the window's own thread, where an exception would end the process. It
            // is handed to the constructor, which throws it.
            started.SetException(ex);
            return;
        }

        started.SetResult();
        if (Handle == IntPtr.Zero) return;

        try
        {
            Pump();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on the window's own thread, where an exception would end the process, and
            // the recording with it. The recording goes on without the window.
            _logger.LogError(ex, "The window that watches for a restart, a shutdown or a sign-out stopped. Recording goes on, but Windows will not ask first before ending the session");
        }
        finally
        {
            Close();
        }
    }

    /// <summary>
    /// Registers this object's kind of window and makes the window, which then belongs to the calling thread. Returns
    /// zero when Windows refuses either, which is logged.
    /// </summary>
    private IntPtr Open()
    {
        var module = GetModuleHandleW(null);
        var kind = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = module,
            ClassName = _className
        };

        if (RegisterClassExW(ref kind) == 0)
        {
            LogRefused(Marshal.GetLastPInvokeError());
            return IntPtr.Zero;
        }

        // Top-level (no parent), and never shown (no WS_VISIBLE).
        var window = CreateWindowExW(0, _className, Title, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            UnregisterClassW(_className, module);
            LogRefused(error);
            return IntPtr.Zero;
        }

        _logger.LogDebug("Watching for a restart, a shutdown or a sign-out");
        return window;
    }

    /// <summary>Hands the messages for the window to it until it is destroyed.</summary>
    private static void Pump()
    {
        while (true)
        {
            var got = GetMessageW(out var message, IntPtr.Zero, 0, 0);
            if (got == 0) return;
            if (got == -1) throw new Win32Exception(Marshal.GetLastPInvokeError());

            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    /// <summary>
    /// After the message loop, on the window's thread: lets go of the reason and destroys the window if the loop ended
    /// early, and removes this object's kind of window.
    /// </summary>
    private void Close()
    {
        IntPtr window;
        lock (_gate)
        {
            _closing = true;
            window = _window;
        }

        if (!_destroyed)
        {
            Apply(window, null);
            DestroyWindow(window);
        }

        if (!UnregisterClassW(_className, GetModuleHandleW(null)))
            _logger.LogDebug("Could not remove the session window's kind of window ({Error}); it goes with the process", DescribeError(Marshal.GetLastPInvokeError()));
    }

    /// <summary>
    /// The window procedure: Windows' session messages, ClassScribe's own two, and the rest to Windows' default handling.
    /// Runs on the window's thread, called by Windows.
    /// </summary>
    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case QueryEndSessionMessage:
                    return Answer(window, Facts(lParam));
                case EndSessionMessage:
                    OnEndSession(wParam != IntPtr.Zero, Facts(lParam));
                    return IntPtr.Zero;
                case ApplyMessage:
                    Apply(window, Wanted());
                    return IntPtr.Zero;
                case CloseMessage:
                    TearDown(window);
                    return IntPtr.Zero;
                case CloseRequestMessage:
                    _logger.LogDebug("Left the window that watches for a restart, a shutdown or a sign-out open, though asked from outside to close it");
                    return IntPtr.Zero;
                case DestroyMessage:
                    _destroyed = true;
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                default:
                    return DefWindowProcW(window, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: Windows called the window, and an exception cannot go back to Windows; it would end
            // the process, and the recording with it. Windows' question is answered yes, which never gets ClassScribe ended.
            _logger.LogError(ex, "The window that watches for a restart, a shutdown or a sign-out could not handle message 0x{Message:X4}", message);
            return message == QueryEndSessionMessage ? 1 : IntPtr.Zero;
        }
    }

    /// <summary>
    /// Windows asks whether it may end the session: raises <see cref="Ending"/> with what Windows said of the end, and
    /// answers no (0) while Windows holds the reason and the end is not forced, otherwise yes (1). The reason wanted is
    /// applied before the handlers run and again after, so that one set meanwhile counts.
    /// </summary>
    private IntPtr Answer(IntPtr window, SessionEndEventArgs end)
    {
        Apply(window, Wanted());
        _logger.LogInformation("Windows asked whether it may end the session ({Kind})", Describe(end));
        Raise(Ending, handler => handler(this, end), "Windows asks whether it may end the session");
        Apply(window, Wanted());

        if (_held is null || end.Forced) return 1;

        _logger.LogInformation("Asked Windows to wait, and to show why: {Reason}", _held);
        return 0;
    }

    /// <summary>
    /// Windows says whether the session ends: raises <see cref="Ended"/>, with what Windows said of the end, when it does,
    /// as it always does when Windows forces the end, and <see cref="EndingCancelled"/> when it does not.
    /// </summary>
    private void OnEndSession(bool ending, SessionEndEventArgs end)
    {
        if (ending || end.Forced)
        {
            _logger.LogInformation("Windows is ending the session now ({Kind})", Describe(end));
            Raise(Ended, handler => handler(this, end), "the session ends");
        }
        else
        {
            _logger.LogInformation("Windows is not ending the session after all");
            Raise(EndingCancelled, handler => handler(this, EventArgs.Empty), "Windows does not end the session after all");
        }
    }

    /// <summary>The object was disposed: lets go of the reason and destroys the window, which ends the message loop.</summary>
    private void TearDown(IntPtr window)
    {
        lock (_gate)
        {
            // Only disposing closes the window; this message from anywhere else is left alone.
            if (!_disposed) return;
            _closing = true;
        }

        Apply(window, null);
        DestroyWindow(window);
    }

    /// <summary>
    /// Makes Windows hold <paramref name="wanted"/> as the window's reason, or none when it is null, unless it already
    /// does. A reason Windows refuses leaves none held; it is tried again each time it is applied, and its refusal is
    /// logged once for as long as it stays wanted. On the window's thread only, as Windows requires.
    /// </summary>
    private void Apply(IntPtr window, string? wanted)
    {
        if (wanted is null)
        {
            // A refused reason that is asked for again later, as for the next class, is reported again if refused again.
            _refused = null;
            LetGo(window);
            return;
        }

        if (wanted == _held) return;

        var error = _reasons.Create(window, wanted);
        if (error == 0)
        {
            _held = wanted;
            _refused = null;
            _logger.LogDebug("Windows asks first before ending the session, and shows why: {Reason}", wanted);
            return;
        }

        // A reason held before is not the one wanted now, and the answer may be no only while Windows holds the reason
        // wanted. Microsoft does not say what Windows still holds after it refused a new reason, so the old one is let go
        // too, and Windows' next question is answered yes.
        LetGo(window);

        if (wanted == _refused)
        {
            _logger.LogDebug("Windows again did not accept the request to ask first before ending the session ({Error})", DescribeError(error));
            return;
        }

        _refused = wanted;
        _logger.LogWarning(
            "Windows did not accept the request to ask first before ending the session ({Error}); a restart or a shutdown will not wait for ClassScribe",
            DescribeError(error));
    }

    /// <summary>Removes the reason Windows holds for the window, if it holds one. On the window's thread only, as Windows requires.</summary>
    private void LetGo(IntPtr window)
    {
        if (_held is null) return;

        var error = _reasons.Destroy(window);
        if (error != 0)
            _logger.LogDebug("Windows did not remove the reason ({Error}); it goes with the window", DescribeError(error));
        _held = null;
        _logger.LogDebug("Windows may end the session again without asking");
    }

    /// <summary>The reason <see cref="BlockShutdown"/> last asked for.</summary>
    private string? Wanted()
    {
        lock (_gate) return _wanted;
    }

    /// <summary>Calls each handler of <paramref name="handlers"/> in turn, through <paramref name="call"/>; one that fails is logged and the rest still run.</summary>
    private void Raise<THandler>(THandler? handlers, Action<THandler> call, string moment)
        where THandler : Delegate
    {
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<THandler>())
        {
            try
            {
                call(handler);
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: Windows called the window, and an exception cannot go back to Windows; it
                // would end the process, and the recording with it. The handlers after this one still run.
                _logger.LogError(ex, "Could not finish what ClassScribe does when {Moment}; the rest still ran", moment);
            }
        }
    }

    /// <summary>
    /// Logs that a change of the reason could not be handed to the window's thread at once, and what that means: the
    /// window still applies it before it answers Windows, but until then Windows' screen may be wrong about ClassScribe.
    /// </summary>
    private void LogNotHandedOver(string? reason, int error)
    {
        if (reason is null)
        {
            _logger.LogWarning(
                "Could not tell Windows at once that ClassScribe no longer holds a restart or a shutdown ({Error}). Windows is told when it asks; until then its screen may still list ClassScribe",
                DescribeError(error));
        }
        else
        {
            _logger.LogWarning(
                "Could not tell Windows at once why ClassScribe holds a restart or a shutdown ({Error}). Windows is told when it asks, but if another program holds a restart first, Windows' screen may leave ClassScribe out",
                DescribeError(error));
        }
    }

    /// <summary>Logs that Windows would not make the window, and what that means.</summary>
    private void LogRefused(int error) =>
        _logger.LogWarning(
            "Windows did not let ClassScribe watch for a restart, a shutdown or a sign-out ({Error}). Recording goes on, but Windows will not ask first before ending the session",
            DescribeError(error));

    /// <summary>
    /// What a session message says of the end, from the flags in its lParam: what kind of end it is, and whether Windows
    /// forces it. Only the low 32 bits count, whether Windows extends them with zeros or with the sign.
    /// </summary>
    private static SessionEndEventArgs Facts(IntPtr lParam)
    {
        var flags = unchecked((uint)(long)lParam);
        var kind = (flags & Logoff) != 0 ? SessionEndKind.SignOut
            : (flags & CloseApp) != 0 ? SessionEndKind.ClosingPrograms
            : SessionEndKind.RestartOrShutdown;
        return new SessionEndEventArgs(kind, (flags & Critical) != 0);
    }

    /// <summary>The end Windows announces, for the log, as in "a restart or a shutdown, forced".</summary>
    private static string Describe(SessionEndEventArgs end)
    {
        var kind = end.Kind switch
        {
            SessionEndKind.SignOut => "a sign-out",
            SessionEndKind.ClosingPrograms => "Windows closing programs, as for an update",
            _ => "a restart or a shutdown"
        };
        return end.Forced ? kind + ", forced" : kind;
    }

    /// <summary>Windows' words for an error code, and the code, as in "Access is denied, error 5".</summary>
    private static string DescribeError(int error) => $"{new Win32Exception(error).Message.TrimEnd('.')}, error {error}";

    /// <summary>A window procedure, as Windows calls it.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Windows' WNDCLASSEXW: a kind of window to register.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        /// <summary>The size of this structure, in bytes.</summary>
        public uint Size;

        /// <summary>The class styles; none.</summary>
        public uint Style;

        /// <summary>The window procedure.</summary>
        public IntPtr Procedure;

        /// <summary>Extra bytes after the class; none.</summary>
        public int ClassExtra;

        /// <summary>Extra bytes after each window; none.</summary>
        public int WindowExtra;

        /// <summary>The module that registers the class.</summary>
        public IntPtr Instance;

        /// <summary>The icon; none.</summary>
        public IntPtr Icon;

        /// <summary>The cursor; none.</summary>
        public IntPtr Cursor;

        /// <summary>The background brush; none.</summary>
        public IntPtr Background;

        /// <summary>The menu; none.</summary>
        public string? MenuName;

        /// <summary>The name of the kind of window.</summary>
        public string ClassName;

        /// <summary>The small icon; none.</summary>
        public IntPtr SmallIcon;
    }

    /// <summary>Windows' MSG: a message taken from the thread's queue.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        /// <summary>The window the message is for.</summary>
        public IntPtr Window;

        /// <summary>The message.</summary>
        public uint Message;

        /// <summary>The message's first parameter.</summary>
        public IntPtr WParam;

        /// <summary>The message's second parameter.</summary>
        public IntPtr LParam;

        /// <summary>When the message was posted.</summary>
        public uint Time;

        /// <summary>The cursor's horizontal position when the message was posted.</summary>
        public int X;

        /// <summary>The cursor's vertical position when the message was posted.</summary>
        public int Y;
    }

    /// <summary>Registers a kind of window; 0 when Windows refuses.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW([In] ref WindowClass windowClass);

    /// <summary>Removes a kind of window registered before.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    /// <summary>Makes a window, which belongs to the calling thread; zero when Windows refuses.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    /// <summary>Destroys a window of the calling thread.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    /// <summary>Windows' default handling of a message.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Waits for the next message of the calling thread: above 0 for a message, 0 for WM_QUIT, -1 on a failure.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetMessageW(out WindowMessage message, IntPtr window, uint first, uint last);

    /// <summary>Turns key presses into characters; nothing is ever typed into this window.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage([In] ref WindowMessage message);

    /// <summary>Hands a message to its window's procedure.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DispatchMessageW([In] ref WindowMessage message);

    /// <summary>Puts a message in the queue of the window's thread, without waiting for it to be handled.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Ends the calling thread's message loop once the messages before it are handled.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void PostQuitMessage(int exitCode);

    /// <summary>The module of the program's own exe, when <paramref name="moduleName"/> is null.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}

/// <summary>
/// Windows' functions for a window's shutdown reason, behind an interface so that tests can stand in for Windows. Windows
/// takes them only on the thread that made the window, so <see cref="SessionWindow"/> calls them there.
/// </summary>
internal interface IShutdownReasonApi
{
    /// <summary>
    /// Makes Windows hold <paramref name="reason"/> for <paramref name="window"/>, so that it asks the user before ending
    /// the session (ShutdownBlockReasonCreate): 0 when Windows took it, otherwise Windows' error code.
    /// </summary>
    int Create(IntPtr window, string reason);

    /// <summary>Removes the reason Windows holds for <paramref name="window"/>, if any (ShutdownBlockReasonDestroy): 0 when Windows did, otherwise Windows' error code.</summary>
    int Destroy(IntPtr window);
}

/// <summary>Windows' own functions for a window's shutdown reason, from user32.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeShutdownReasonApi : IShutdownReasonApi
{
    /// <summary>The one instance; it holds nothing.</summary>
    public static NativeShutdownReasonApi Instance { get; } = new();

    /// <inheritdoc />
    public int Create(IntPtr window, string reason) => ShutdownBlockReasonCreate(window, reason) ? 0 : Marshal.GetLastPInvokeError();

    /// <inheritdoc />
    public int Destroy(IntPtr window) => ShutdownBlockReasonDestroy(window) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>Makes Windows hold a reason for the window; only on the window's thread.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);

    /// <summary>Removes the window's reason, if Windows holds one; only on the window's thread.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
