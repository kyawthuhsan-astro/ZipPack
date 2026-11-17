using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ZipPackNet.Shell;

/// <summary>The look of the progress indicator on a taskbar button.</summary>
internal enum TaskbarProgressState
{
    /// <summary>No indicator.</summary>
    None = 0,

    /// <summary>A pulsing bar, for work of unknown length.</summary>
    Indeterminate = 0x1,

    /// <summary>A green bar showing <see cref="TaskbarProgress.SetValue"/>.</summary>
    Normal = 0x2,

    /// <summary>A red bar: the operation failed.</summary>
    Error = 0x4,

    /// <summary>A yellow bar: the operation is paused or being cancelled.</summary>
    Paused = 0x8,
}

/// <summary>
/// Shows progress on the window's taskbar button (Windows 7 and later) through the shell's <c>ITaskbarList3</c>,
/// the same interface the Windows API Code Pack's <c>TaskbarManager</c> wraps.
/// </summary>
/// <remarks>
/// <para>Calls the COM interface directly (source-generated COM interop, like <c>SevenZipCore</c>) instead of the
/// Code Pack packages, which target the .NET Framework and would add compatibility warnings to the build.</para>
/// <para>Every call is best effort: where the shell has no taskbar (Server Core, some remote sessions, an
/// Explorer restart) the indicator is simply not shown. Use from the UI thread.</para>
/// </remarks>
internal sealed partial class TaskbarProgress
{
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    private readonly nint _window;

    // TaskbarList is apartment-threaded: each instance belongs to the UI thread that created it.
    private readonly ITaskbarList3? _taskbar;

    /// <summary>Initializes a new instance of the <see cref="TaskbarProgress"/> class for one top-level window.</summary>
    /// <param name="window">The handle of the window that owns the taskbar button.</param>
    public TaskbarProgress(nint window)
    {
        _window = window;
        _taskbar = window == 0 ? null : Create();
    }

    /// <summary>Raised after every change, with the window, state and fraction; lets tests observe the indicator.</summary>
    internal static event Action<nint, TaskbarProgressState, double>? Changed;

    /// <summary>Gets a value indicating whether the taskbar interface is available for this window.</summary>
    public bool IsSupported => _taskbar is not null;

    /// <summary>Gets the state last set, for tests and diagnostics.</summary>
    public TaskbarProgressState State { get; private set; }

    /// <summary>Gets the value last set, as a fraction in 0..1.</summary>
    public double Fraction { get; private set; }

    /// <summary>Shows <paramref name="fraction"/> (0..1) as a green bar.</summary>
    public void SetValue(double fraction)
    {
        Fraction = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0d, 1d);
        if (State is TaskbarProgressState.None or TaskbarProgressState.Indeterminate)
        {
            SetState(TaskbarProgressState.Normal);
        }

        const ulong Total = 10_000;
        Invoke(taskbar => taskbar.SetProgressValue(_window, (ulong)Math.Round(Fraction * Total), Total));
    }

    /// <summary>Changes the indicator; <see cref="TaskbarProgressState.None"/> removes it.</summary>
    public void SetState(TaskbarProgressState state)
    {
        State = state;
        Invoke(taskbar => taskbar.SetProgressState(_window, state));
    }

    /// <summary>Removes the indicator.</summary>
    public void Clear()
    {
        Fraction = 0;
        SetState(TaskbarProgressState.None);
    }

    private static ITaskbarList3? Create()
    {
        try
        {
            Guid clsid = ClsidTaskbarList;
            Guid iid = typeof(ITaskbarList3).GUID;
            int hr = CoCreateInstance(clsid, 0, ClassContextInprocServer, iid, out nint instance);
            if (hr < 0 || instance == 0)
            {
                return null;
            }

            try
            {
                var taskbar = (ITaskbarList3)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(instance, CreateObjectFlags.None);
                taskbar.HrInit();
                return taskbar;
            }
            finally
            {
                Marshal.Release(instance);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private void Invoke(Action<ITaskbarList3> call)
    {
        if (_taskbar is not null)
        {
            try
            {
                call(_taskbar);
            }
            catch (COMException)
            {
                // The taskbar went away (Explorer restarted); progress display is a courtesy.
            }
        }

        Changed?.Invoke(_window, State, Fraction);
    }

    private const uint ClassContextInprocServer = 0x1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    /// <summary>
    /// <c>ITaskbarList3</c> with its inherited <c>ITaskbarList</c> / <c>ITaskbarList2</c> methods, in v-table order.
    /// The methods after <c>SetProgressState</c> are not used and therefore not declared.
    /// </summary>
    [GeneratedComInterface]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    internal partial interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();

        void AddTab(nint window);

        void DeleteTab(nint window);

        void ActivateTab(nint window);

        void SetActiveAlt(nint window);

        // ITaskbarList2
        void MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(nint window, ulong completed, ulong total);

        void SetProgressState(nint window, TaskbarProgressState state);
    }
}
