using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowTrace.Core;

namespace WindowTrace.Monitoring;

/// <summary>
/// Observes system foreground-window changes using an out-of-context
/// WinEvent hook. It does not install a keyboard hook or inspect input.
/// </summary>
public sealed class WindowMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;

    private readonly QpcClock _clock;
    private readonly WinEventDelegate _callback;
    private nint _hook;
    private bool _disposed;

    public WindowMonitor(QpcClock clock)
    {
        _clock = clock;
        _callback = OnWinEvent;
    }

    public event Action<WindowSnapshot>? ForegroundChanged;
    public event Action<string>? MonitorError;

    public bool IsRunning => _hook != nint.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            return;

        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);

        if (_hook == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"SetWinEventHook failed. Win32 error: {error}.");
        }
    }

    public WindowSnapshot? CaptureForeground()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == nint.Zero ? null : CaptureWindow(hwnd);
    }

    public void Stop()
    {
        if (_hook == nint.Zero)
            return;

        UnhookWinEvent(_hook);
        _hook = nint.Zero;
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
        if (eventType != EventSystemForeground || hwnd == nint.Zero)
            return;

        try
        {
            var snapshot = CaptureWindow(hwnd);
            if (snapshot is not null)
                ForegroundChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            MonitorError?.Invoke(ex.Message);
        }
    }

    private WindowSnapshot? CaptureWindow(nint hwnd)
    {
        var qpc = _clock.GetTimestamp();
        var threadId = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
            return null;

        return new WindowSnapshot(
            qpc,
            _clock.ToUtc(qpc),
            hwnd,
            processId,
            threadId,
            ReadProcessName(processId),
            ReadWindowTitle(hwnd),
            IsIconic(hwnd));
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
        GC.SuppressFinalize(this);
    }

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
