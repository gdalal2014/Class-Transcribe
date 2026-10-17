using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// Windows' session messages as a test plays them: the test raises <see cref="Ending"/>, <see cref="Ended"/> and
/// <see cref="EndingCancelled"/> by hand, on its own thread, with the facts the real window passes (what kind of end it is,
/// and whether Windows forces it), and sees the reason held to make Windows ask first, every change of it, and whether
/// anyone still listens.
/// </summary>
public sealed class FakeSessionEvents : ISessionEvents
{
    private readonly Lock _gate = new();

    /// <summary>Every call of <see cref="BlockShutdown"/>, in order.</summary>
    private readonly List<string?> _blocks = [];

    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<SessionEndEventArgs>? Ending;

    /// <inheritdoc />
    public event EventHandler<SessionEndEventArgs>? Ended;

    /// <inheritdoc />
    public event EventHandler? EndingCancelled;

    /// <summary>The reason held now, or null when Windows may end the session without asking.</summary>
    public string? HeldReason
    {
        get { lock (_gate) return _blocks.Count == 0 ? null : _blocks[^1]; }
    }

    /// <summary>Every call of <see cref="BlockShutdown"/>, in order: the reason, or null where the reason was let go.</summary>
    public IReadOnlyList<string?> Blocks
    {
        get { lock (_gate) return [.. _blocks]; }
    }

    /// <summary>True once the session events were disposed.</summary>
    public bool Disposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>How many handlers listen to <see cref="Ending"/>, <see cref="Ended"/> and <see cref="EndingCancelled"/> together, so that a test can see them removed.</summary>
    public int Listeners => Count(Ending) + Count(Ended) + Count(EndingCancelled);

    /// <inheritdoc />
    public void BlockShutdown(string? reason)
    {
        lock (_gate) _blocks.Add(reason);
    }

    /// <summary>
    /// Windows asks whether it may end the session: raises <see cref="Ending"/> with <paramref name="kind"/> and
    /// <paramref name="forced"/>, then gives the answer the real window gives: true (it may) unless a reason is held, and
    /// always when the end is forced.
    /// </summary>
    public bool AskToEnd(bool forced = false, SessionEndKind kind = SessionEndKind.RestartOrShutdown)
    {
        Ending?.Invoke(this, new SessionEndEventArgs(kind, forced));
        return forced || HeldReason is null;
    }

    /// <summary>The session ends now: raises <see cref="Ended"/> with <paramref name="kind"/> and <paramref name="forced"/>.</summary>
    public void End(bool forced = false, SessionEndKind kind = SessionEndKind.RestartOrShutdown) =>
        Ended?.Invoke(this, new SessionEndEventArgs(kind, forced));

    /// <summary>Windows does not end the session after all: raises <see cref="EndingCancelled"/>.</summary>
    public void CancelEnd() => EndingCancelled?.Invoke(this, EventArgs.Empty);

    /// <summary>Notes that the session events were disposed.</summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    /// <summary>How many handlers <paramref name="handlers"/> holds.</summary>
    private static int Count(Delegate? handlers) => handlers?.GetInvocationList().Length ?? 0;
}

/// <summary>
/// Windows' functions for a window's shutdown reason as a test plays them: each call is kept, with the thread it was made
/// on, and Windows can be made to refuse to take a reason, or the call to take it can be made to throw.
/// </summary>
internal sealed class FakeShutdownReasonApi : IShutdownReasonApi
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<int> _threads = [];
    private string? _held;
    private int _createError;
    private Exception? _createFailure;

    /// <summary>The calls made, in order, such as "Create ClassScribe is recording a class", "Refused ...", "Threw ..." and "Destroy".</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>The managed thread each call was made on, in the order of <see cref="Calls"/>.</summary>
    public IReadOnlyList<int> Threads
    {
        get { lock (_gate) return [.. _threads]; }
    }

    /// <summary>The reason held now, or null.</summary>
    public string? Held
    {
        get { lock (_gate) return _held; }
    }

    /// <summary>Makes Windows refuse to take a reason from now on, with <paramref name="error"/>; 0 makes it take them again.</summary>
    public void RefuseToCreate(int error)
    {
        lock (_gate) _createError = error;
    }

    /// <summary>
    /// Makes every call to take a reason throw <paramref name="failure"/> from now on, as Windows' own functions never do,
    /// to see what the window does with a failure; null makes the calls work again.
    /// </summary>
    public void ThrowOnCreate(Exception? failure)
    {
        lock (_gate) _createFailure = failure;
    }

    /// <inheritdoc />
    public int Create(IntPtr window, string reason)
    {
        lock (_gate)
        {
            _threads.Add(Environment.CurrentManagedThreadId);
            if (_createFailure is { } failure)
            {
                _calls.Add($"Threw {reason}");
                throw failure;
            }

            if (_createError != 0)
            {
                _calls.Add($"Refused {reason}");
                return _createError;
            }

            _calls.Add($"Create {reason}");
            _held = reason;
            return 0;
        }
    }

    /// <inheritdoc />
    public int Destroy(IntPtr window)
    {
        lock (_gate)
        {
            _threads.Add(Environment.CurrentManagedThreadId);
            _calls.Add("Destroy");
            _held = null;
            return 0;
        }
    }
}
