using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using WindowTrace.Core;

namespace WindowTrace.Monitoring;

/// <summary>
/// Observes foreground-window changes. The WinEvent callback only captures
/// QPC/HWND/PID/TID and queues the work; title and process lookups happen on
/// a dedicated worker so the WPF message loop is not blocked by diagnostics.
/// </summary>
public sealed class WindowMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectFocus = 0x8005;
    private const uint WineventOutOfContext = 0x0000;

    private readonly QpcClock _clock;
    private readonly WinEventDelegate _callback;
    private readonly Channel<PendingWindowEvent> _pendingEvents;
    private readonly Task _worker;
    private nint _foregroundHook;
    private nint _minimizeHook;
    private nint _objectFocusHook;
    private volatile bool _disposed;
    private volatile bool _acceptEvents;
    private int _generation;

    public WindowMonitor(QpcClock clock)
    {
        _clock = clock;
        _callback = OnWinEvent;
        _pendingEvents = Channel.CreateUnbounded<PendingWindowEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _worker = Task.Run(ProcessPendingEventsAsync);
    }

    public event Action<WindowSnapshot>? ForegroundChanged;
    public event Action<WindowSnapshot>? WindowStateChanged;
    public event Action<string>? MonitorError;

    public bool IsRunning => _foregroundHook != nint.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;

        Interlocked.Increment(ref _generation);
        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);

        if (_foregroundHook == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"SetWinEventHook for foreground events failed. Win32 error: {error}.");
        }

        _minimizeHook = SetWinEventHook(
            EventSystemMinimizeStart,
            EventSystemMinimizeEnd,
            nint.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);

        if (_minimizeHook == nint.Zero)
            MonitorError?.Invoke($"Minimize/restore hook unavailable. Win32 error: {Marshal.GetLastWin32Error()}.");

        _objectFocusHook = SetWinEventHook(
            EventObjectFocus,
            EventObjectFocus,
            nint.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);

        if (_objectFocusHook == nint.Zero)
            MonitorError?.Invoke($"Object-focus hook unavailable. Win32 error: {Marshal.GetLastWin32Error()}.");

        _acceptEvents = true;
    }

    public WindowSnapshot? CaptureForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero)
            return null;

        var qpc = _clock.GetTimestamp();
        var threadId = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
            return null;

        return BuildSnapshot(new PendingWindowEvent(qpc, hwnd, processId, threadId, "ForegroundChanged", 0, 0, Volatile.Read(ref _generation)));
    }

    public void Stop()
    {
        _acceptEvents = false;
        Interlocked.Increment(ref _generation);

        if (_foregroundHook != nint.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = nint.Zero;
        }

        if (_minimizeHook != nint.Zero)
        {
            UnhookWinEvent(_minimizeHook);
            _minimizeHook = nint.Zero;
        }

        if (_objectFocusHook != nint.Zero)
        {
            UnhookWinEvent(_objectFocusHook);
            _objectFocusHook = nint.Zero;
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || !_acceptEvents || hwnd == nint.Zero)
            return;

        var windowEventType = eventType switch
        {
            EventSystemForeground => "ForegroundChanged",
            EventSystemMinimizeStart => "WindowMinimizeStarted",
            EventSystemMinimizeEnd => "WindowRestored",
            EventObjectFocus => "ObjectFocusChanged",
            _ => null
        };
        if (windowEventType is null)
            return;

        try
        {
            var qpc = _clock.GetTimestamp();
            var threadId = GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
                return;

            var generation = Volatile.Read(ref _generation);
            _pendingEvents.Writer.TryWrite(new PendingWindowEvent(qpc, hwnd, processId, threadId, windowEventType, idObject, idChild, generation));
        }
        catch (Exception ex)
        {
            MonitorError?.Invoke(ex.Message);
        }
    }

    private async Task ProcessPendingEventsAsync()
    {
        await foreach (var pending in _pendingEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (!_acceptEvents || pending.Generation != Volatile.Read(ref _generation))
                    continue;

                var snapshot = BuildSnapshot(pending);
                if (snapshot is null)
                    continue;

                if (pending.EventType == "ForegroundChanged")
                    ForegroundChanged?.Invoke(snapshot);
                else
                    WindowStateChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                MonitorError?.Invoke(ex.Message);
            }
        }
    }

    private WindowSnapshot? BuildSnapshot(PendingWindowEvent pending)
    {
        if (pending.ProcessId == 0)
            return null;

        nint? focusedWindowHandle = null;
        uint? guiThreadFlags = null;
        var guiInfo = new GuiThreadInfo
        {
            Size = (uint)Marshal.SizeOf<GuiThreadInfo>()
        };

        if (GetGUIThreadInfo(pending.ThreadId, ref guiInfo))
        {
            focusedWindowHandle = guiInfo.FocusWindow == nint.Zero ? null : guiInfo.FocusWindow;
            guiThreadFlags = guiInfo.Flags;
        }

        return new WindowSnapshot(
            pending.QpcTimestamp,
            _clock.ToUtc(pending.QpcTimestamp),
            pending.Handle,
            pending.ProcessId,
            pending.ThreadId,
            ReadProcessName(pending.ProcessId),
            ReadWindowTitle(pending.Handle),
            IsIconic(pending.Handle),
            pending.EventType,
            pending.ObjectId,
            pending.ChildId,
            focusedWindowHandle,
            guiThreadFlags);
    }

    private static string ReadWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;

        var builder = new StringBuilder(Math.Min(length + 1, 2048));
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string ReadProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
        _pendingEvents.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); }
        catch { }
        GC.SuppressFinalize(this);
    }

    private readonly record struct PendingWindowEvent(
        long QpcTimestamp,
        nint Handle,
        uint ProcessId,
        uint ThreadId,
        string EventType,
        int ObjectId,
        int ChildId,
        int Generation);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwnerWindow;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    private delegate void WinEventDelegate(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);
}
