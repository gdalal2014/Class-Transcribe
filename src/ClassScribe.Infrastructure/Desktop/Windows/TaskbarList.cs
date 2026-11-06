using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClassScribe.Desktop.Windows;

/// <summary>Shows a state on a window's taskbar button, behind an interface so that tests can stand in for Windows.</summary>
internal interface ITaskbarProgress : IDisposable
{
    /// <summary>
    /// Shows <paramref name="state"/> on <paramref name="window"/>'s taskbar button, its bar filled to
    /// <paramref name="percent"/> of 100. A call Windows refuses is thrown.
    /// </summary>
    void Set(IntPtr window, TaskbarState state, int percent);
}

/// <summary>
/// Windows' ITaskbarList3, declared as far as the progress calls. Built-in COM interop ignores the inheritance of COM
/// interfaces, so the methods of ITaskbarList and ITaskbarList2 come first, to keep each method in its own slot; the
/// methods after SetProgressState are never called, and are left out.
/// </summary>
/// <remarks>
/// The order is copied from shobjidl_core.h. No call to Windows can check it: Windows answers S_OK to these calls
/// whatever they carry (an unknown flag, a total of 0, no window), so a method in the wrong slot would fail without a
/// sign. TaskbarListTests pins the order to the header's, so that an edit cannot move or drop a method unnoticed.
/// </remarks>
[ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    /// <summary>ITaskbarList::HrInit: readies the list; called once, before anything else.</summary>
    [PreserveSig] int HrInit();

    /// <summary>ITaskbarList::AddTab. Never called.</summary>
    [PreserveSig] int AddTab(IntPtr window);

    /// <summary>ITaskbarList::DeleteTab. Never called.</summary>
    [PreserveSig] int DeleteTab(IntPtr window);

    /// <summary>ITaskbarList::ActivateTab. Never called.</summary>
    [PreserveSig] int ActivateTab(IntPtr window);

    /// <summary>ITaskbarList::SetActiveAlt. Never called.</summary>
    [PreserveSig] int SetActiveAlt(IntPtr window);

    /// <summary>ITaskbarList2::MarkFullscreenWindow. Never called.</summary>
    [PreserveSig] int MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

    /// <summary>ITaskbarList3::SetProgressValue: how far the bar is filled, <paramref name="completed"/> of <paramref name="total"/>.</summary>
    [PreserveSig] int SetProgressValue(IntPtr window, ulong completed, ulong total);

    /// <summary>ITaskbarList3::SetProgressState: the kind of bar, as a TBPFLAG.</summary>
    [PreserveSig] int SetProgressState(IntPtr window, int flags);
}

/// <summary>
/// The classic console's taskbar button, through Windows' taskbar list (ITaskbarList3): a green, yellow or red bar, or
/// one that runs back and forth. The list is made and readied when it is first needed, and made again after a call
/// Windows refuses, as it must be after Explorer has restarted.
/// </summary>
/// <remarks>
/// The taskbar list's class lives in a single-threaded apartment. ClassScribe calls it from thread-pool threads, which
/// are in the multithreaded one, so COM makes the list in an apartment of its own and passes each call to it, through
/// the proxy Windows registers for ITaskbarList3.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TaskbarList(Func<ITaskbarList3> create) : ITaskbarProgress
{
    /// <summary>CLSID_TaskbarList.</summary>
    private static readonly Guid TaskbarListClassId = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    private readonly Lock _gate = new();
    private ITaskbarList3? _list;
    private bool _disposed;

    /// <summary>The taskbar list Windows makes.</summary>
    public TaskbarList() : this(CreateWindowsList)
    {
    }

    /// <summary>Windows' TBPFLAG for <paramref name="state"/>: none 0, indeterminate 1, normal 2, error 4, paused 8.</summary>
    internal static int Flags(TaskbarState state) => state switch
    {
        TaskbarState.None => 0x0,
        TaskbarState.Indeterminate => 0x1,
        TaskbarState.Normal => 0x2,
        TaskbarState.Error => 0x4,
        TaskbarState.Paused => 0x8,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a taskbar state.")
    };

    /// <inheritdoc />
    /// <remarks>
    /// A bar's value is set before its state: setting a value turns a bar green, and the state then colours it, yellow
    /// or red. A state without a bar, none or running, is set alone.
    /// </remarks>
    public void Set(IntPtr window, TaskbarState state, int percent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var list = _list ??= Open();
            try
            {
                if (state is TaskbarState.Normal or TaskbarState.Paused or TaskbarState.Error)
                    Check(list.SetProgressValue(window, (ulong)Math.Clamp(percent, 0, 100), 100));
                Check(list.SetProgressState(window, Flags(state)));
            }
            catch
            {
                // Made again for the next state: a list that Explorer's restart cut off stays cut off.
                _list = null;
                Release(list);
                throw;
            }
        }
    }

    /// <summary>Lets the list go; nothing more is set after that.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Release(_list);
            _list = null;
        }
    }

    /// <summary>Makes the list and readies it.</summary>
    private ITaskbarList3 Open()
    {
        var list = create();
        try
        {
            Check(list.HrInit());
            return list;
        }
        catch
        {
            Release(list);
            throw;
        }
    }

    /// <summary>Makes Windows' own taskbar list.</summary>
    private static ITaskbarList3 CreateWindowsList()
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarListClassId, throwOnError: true)!)!;
        try
        {
            return (ITaskbarList3)instance;
        }
        catch
        {
            Release(instance);
            throw;
        }
    }

    /// <summary>Throws the exception that matches a failed result; does nothing for a success.</summary>
    private static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    /// <summary>Releases a COM object now rather than when the garbage collector gets to it; a test's fake, and null, are left alone.</summary>
    private static void Release(object? list)
    {
        if (list is not null && Marshal.IsComObject(list)) Marshal.ReleaseComObject(list);
    }
}
