using System.IO;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using WindowTrace.Core;
using WindowTrace.Monitoring;
using WindowTrace.Storage;

namespace WindowTrace;

public partial class MainWindow : Window
{
    private const int UiEventLimit = 500;

    private readonly QpcClock _clock = new();
    private readonly BoundedEventBuffer _history = new(50_000);
    private readonly WindowMonitor _windowMonitor;
    private readonly DiagnosticSessionManager _sessions;
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _targetPickTimer;
    private int _targetPickRemaining;

    private EtwRealtimeCollector? _etwCollector;
    private CpuSampler? _cpuSampler;
    private GpuPdhSampler? _gpuSampler;
    private PresentMonProcess? _presentMon;
    private volatile bool _isMonitoring;
    private volatile bool _isClosing;
    private long _displayedEvents;
    private long _presentMonFrameCount;
    private long _gpuSampleCount;
    private int _lastReportedEtwEventsLost = -1;
    private HwndSource? _windowSource;
    private const int InputMarkerHotKeyId = 0x5741;
    private const int WmHotKey = 0x0312;

    public ObservableCollection<WindowRow> Events { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _windowMonitor = new WindowMonitor(_clock);
        _windowMonitor.ForegroundChanged += OnForegroundChanged;
        _windowMonitor.WindowStateChanged += OnWindowStateChanged;
        _windowMonitor.MonitorError += OnMonitorError;

        _sessions = new DiagnosticSessionManager(_clock, _history);
        _sessions.StatusChanged += OnSessionStatusChanged;

        _sessionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _sessionTimer.Tick += SessionTimer_Tick;

        _targetPickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _targetPickTimer.Tick += TargetPickTimer_Tick;

        EnvironmentText.Text =
            $"Environment: {RuntimeInformation.OSDescription} | " +
            $"OS version/build: {Environment.OSVersion.Version} | " +
            $"Architecture: {RuntimeInformation.OSArchitecture} | " +
            $".NET: {RuntimeInformation.FrameworkDescription} | " +
            $"QPC frequency: {_clock.Frequency:N0} ticks/sec";
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProc);
    }

    private void InputMarkerCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero ||
            !RegisterHotKey(handle, InputMarkerHotKeyId, 0x0002 | 0x0001 | 0x4000, 0x7B))
        {
            MessageBox.Show(this,
                $"Unable to register Ctrl+Alt+F12. Win32 error: {Marshal.GetLastWin32Error()}. The marker was not enabled.",
                "WindowTrace input marker", MessageBoxButton.OK, MessageBoxImage.Warning);
            InputMarkerCheckBox.IsChecked = false;
            return;
        }

        FooterText.Text = "Input timestamp marker enabled. Ctrl+Alt+F12 is reserved and consumed as a marker; no other keyboard input is observed.";
    }

    private void InputMarkerCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            UnregisterHotKey(handle, InputMarkerHotKeyId);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == InputMarkerHotKeyId)
        {
            var qpc = _clock.GetTimestamp();
            var item = new DiagnosticEvent(
                Guid.NewGuid(),
                "InputTestMarker",
                "RegisterHotKey",
                qpc,
                _clock.Frequency,
                _clock.ToUtc(qpc),
                _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                _sessions.TargetProcessId,
                null,
                null,
                _sessions.TargetProcessName,
                null,
                "Explicit Ctrl+Alt+F12 timestamp marker. No key contents or other keyboard events were collected.");

            _sessions.PublishTelemetry(item);
            DisplayEvent(item);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMonitoring)
            return;

        try
        {
            _history.Reset();
            Interlocked.Exchange(ref _totalEvents, 0);
            Interlocked.Exchange(ref _presentMonFrameCount, 0);
            Interlocked.Exchange(ref _gpuSampleCount, 0);
            Events.Clear();
            EventCountText.Text = "0";
            _lastReportedEtwEventsLost = -1;

            _windowMonitor.Start();
            _isMonitoring = true;

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Monitoring window focus and starting realtime collectors";
            FooterText.Text = "Window monitor active.";

            _sessionTimer.Start();
            AddSystemEvent("MonitoringStarted", "Window monitor started; ETW starts when a target process is selected.");

            var initialForeground = _windowMonitor.CaptureForeground();
            if (_sessions.TargetProcessId is not null)
                _sessions.UpdateForegroundBaseline(initialForeground?.ProcessId);
            if (initialForeground is not null)
                OnForegroundChanged(initialForeground);

            if (_sessions.TargetProcessId is uint targetPid)
            {
                EnsureEtwCollectorStarted();
                await StartTargetCollectorsAsync(targetPid);
            }
            else
            {
                FooterText.Text = "Window monitor active. Select the game process to enable target-process CPU/GPU sampling and system-wide ETW diagnostics.";
            }
        }
        catch (Exception ex)
        {
            _isMonitoring = false;
            _windowMonitor.Stop();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = "Unable to start monitoring";
            FooterText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "WindowTrace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var source = _sessions.LastCompletedSessionPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            MessageBox.Show(this, "No completed diagnostic session is available to export yet.", "WindowTrace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export diagnostic session",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = Path.GetFileNameWithoutExtension(source) + ".csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await CsvExporter.ExportJsonlAsync(source!, dialog.FileName);
            FooterText.Text = $"CSV exported: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CSV export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopMonitoringAsync();
    }

    private void SetTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessions.IsSessionActive)
        {
            MessageBox.Show(this, "A diagnostic session is active. Stop monitoring before changing the target.",
                "WindowTrace", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _targetPickRemaining = 3;
        StatusText.Text = "Switch to the game now";
        FooterText.Text = "Target picker armed. Switch to the game within 3 seconds; WindowTrace will capture the foreground process.";
        _targetPickTimer.Stop();
        _targetPickTimer.Start();
    }

    private async void TargetPickTimer_Tick(object? sender, EventArgs e)
    {
        _targetPickRemaining--;
        if (_targetPickRemaining > 0)
        {
            StatusText.Text = $"Capturing foreground process in {_targetPickRemaining}...";
            return;
        }

        _targetPickTimer.Stop();
        var snapshot = _windowMonitor.CaptureForeground();
        if (snapshot is null || snapshot.ProcessId == Environment.ProcessId)
        {
            StatusText.Text = "Target selection not completed";
            FooterText.Text = "WindowTrace is still foreground. Click the target picker and switch to the game during the 3-second countdown.";
            return;
        }

        try
        {
            if (_sessions.IsSessionActive)
                throw new InvalidOperationException("A diagnostic session is active. Stop monitoring before changing the target.");

            await StopTargetCollectorsAsync();
            _sessions.SetTarget(snapshot.ProcessId, snapshot.ProcessName, snapshot.ProcessId);
            TargetText.Text = $"Target: {snapshot.ProcessName} (PID {snapshot.ProcessId}) | {snapshot.WindowTitle}";
            StatusText.Text = $"Target selected: {snapshot.ProcessName}";
            if (_isMonitoring)
            {
                EnsureEtwCollectorStarted();
                await StartTargetCollectorsAsync(snapshot.ProcessId);
                FooterText.Text = "Target selected. CPU/GPU/PresentMon and ETW collectors are starting; wait at least 3 seconds before reproducing the focus switch.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WindowTrace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ClearTargetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sessions.IsSessionActive)
                throw new InvalidOperationException("A diagnostic session is active. Stop monitoring before clearing the target.");

            _targetPickTimer.Stop();
            await StopTargetCollectorsAsync();
            await StopEtwCollectorAsync();
            _sessions.ClearTarget();
            TargetText.Text = "No target selected. Click the delayed picker, then switch to the game within 3 seconds.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WindowTrace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BrowsePresentMonButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select PresentMon executable",
            Filter = "PresentMon executable (PresentMon.exe)|PresentMon.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            PresentMonPathText.Text = dialog.FileName;
            if (_isMonitoring && _sessions.TargetProcessId is uint targetPid)
            {
                await StopPresentMonAndRestartAsync(targetPid);
            }
        }
    }

    private async Task StopPresentMonAndRestartAsync(uint targetPid)
    {
        if (_presentMon is not null)
        {
            await _presentMon.DisposeAsync();
            _presentMon = null;
        }

        StartPresentMon(targetPid);
    }

    private async Task StartTargetCollectorsAsync(uint processId)
    {
        await StartCpuSamplerAsync(processId);
        await StartGpuSamplerAsync(processId);
        StartPresentMon(processId);
    }

    private async Task StartCpuSamplerAsync(uint processId)
    {
        if (_cpuSampler is not null)
        {
            await _cpuSampler.DisposeAsync();
            _cpuSampler = null;
        }

        var sampler = new CpuSampler(_clock);
        sampler.SampleReceived += OnCpuSample;
        sampler.Warning += OnCollectorWarning;
        _cpuSampler = sampler;

        try
        {
            sampler.Start(processId);
        }
        catch (Exception ex)
        {
            OnCollectorWarning($"CPU sampler could not start: {ex.Message}");
        }
    }

    private async Task StartGpuSamplerAsync(uint processId)
    {
        if (_gpuSampler is not null)
        {
            await _gpuSampler.DisposeAsync();
            _gpuSampler = null;
        }

        var sampler = new GpuPdhSampler(_clock);
        sampler.SampleReceived += OnGpuSample;
        sampler.Warning += OnCollectorWarning;
        _gpuSampler = sampler;

        try
        {
            sampler.Start(processId);
            OnCollectorWarning($"GPU PDH sampler started. Available counter groups: {string.Join(", ", sampler.AvailableCounters)}");
        }
        catch (Exception ex)
        {
            await sampler.DisposeAsync();
            _gpuSampler = null;
            OnCollectorWarning($"GPU counters unavailable: {ex.Message}");
        }
    }

    private void StartPresentMon(uint processId)
    {
        var path = PresentMonPathText.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_presentMon is { IsRunning: true })
            return;

        var collector = new PresentMonProcess(_clock);
        collector.FrameReceived += OnPresentMonFrame;
        collector.RenderingSummaryReceived += OnRenderingSummary;
        collector.StatusChanged += OnCollectorWarning;

        try
        {
            collector.Start(path, processId);
            _presentMon = collector;
        }
        catch (Exception ex)
        {
            _ = collector.DisposeAsync();
            OnCollectorWarning($"PresentMon could not start: {ex.Message}");
        }
    }

    private void EnsureEtwCollectorStarted()
    {
        if (_etwCollector is not null)
            return;

        _lastReportedEtwEventsLost = -1;
        var collector = new EtwRealtimeCollector(_clock);
        collector.EventReceived += OnEtwEvent;
        collector.CollectorWarning += OnCollectorWarning;
        try
        {
            collector.Start();
            _etwCollector = collector;
            var enabled = collector.ProviderStatuses.Count(p => p.Status == "Enabled");
            FooterText.Text = $"ETW realtime consumer started. Enabled providers: {enabled}/{collector.ProviderStatuses.Count}. Kernel scheduling events may require administrator privileges.";

            foreach (var provider in collector.ProviderStatuses)
            {
                var qpc = _clock.GetTimestamp();
                _sessions.PublishTelemetry(new DiagnosticEvent(
                    Guid.NewGuid(),
                    "EtwProviderCapability",
                    "ETW",
                    qpc,
                    _clock.Frequency,
                    _clock.ToUtc(qpc),
                    _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                    null,
                    null,
                    null,
                    null,
                    null,
                    $"Provider={provider.ProviderName};Status={provider.Status};Details={provider.Details}"));
            }
        }
        catch (Exception ex)
        {
            _ = collector.DisposeAsync();
            OnCollectorWarning($"ETW collector could not start: {ex.Message}");
        }
    }

    private async Task StopEtwCollectorAsync()
    {
        if (_etwCollector is null)
            return;

        await _etwCollector.DisposeAsync();
        _sessions.SetEtwEventsLost(_etwCollector.EventsLost);
        FooterText.Text = _etwCollector.EventsLost >= 0
            ? $"ETW collector stopped. Events lost: {_etwCollector.EventsLost}."
            : "ETW collector stopped. Event-loss count unavailable.";
        _etwCollector = null;
    }

    private async Task StopTargetCollectorsAsync()
    {
        if (_cpuSampler is not null)
        {
            await _cpuSampler.DisposeAsync();
            _cpuSampler = null;
        }

        if (_gpuSampler is not null)
        {
            await _gpuSampler.DisposeAsync();
            _gpuSampler = null;
        }

        if (_presentMon is not null)
        {
            await _presentMon.DisposeAsync();
            _presentMon = null;
        }
    }

    private void OnForegroundChanged(WindowSnapshot snapshot)
    {
        if (!_isMonitoring || _isClosing)
            return;

        var diagnosticEvent = new DiagnosticEvent(
            Guid.NewGuid(),
            "ForegroundChanged",
            "WinEvent",
            snapshot.QpcTimestamp,
            _clock.Frequency,
            snapshot.UtcTimestamp,
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, snapshot.QpcTimestamp),
            snapshot.ProcessId,
            snapshot.ThreadId,
            unchecked((ulong)snapshot.Handle),
            snapshot.ProcessName,
            snapshot.WindowTitle,
            $"IsMinimized={snapshot.IsMinimized};ObjectId={snapshot.ObjectId};ChildId={snapshot.ChildId};TimestampQuality=WinEventCallbackReceiptQpc");

        _sessions.PublishForeground(diagnosticEvent, snapshot.ProcessId);
        DisplayEvent(diagnosticEvent);

        Dispatcher.InvokeAsync(() =>
        {
            CurrentWindowText.Text =
                $"{snapshot.ProcessName} (PID {snapshot.ProcessId}) | " +
                $"HWND 0x{snapshot.Handle.ToInt64():X} | " +
                $"{(string.IsNullOrWhiteSpace(snapshot.WindowTitle) ? "(untitled window)" : snapshot.WindowTitle)}";
        });
    }

    private void OnWindowStateChanged(WindowSnapshot snapshot)
    {
        if (!_isMonitoring || _isClosing)
            return;

        var item = new DiagnosticEvent(
            Guid.NewGuid(),
            snapshot.EventType,
            "WinEvent",
            snapshot.QpcTimestamp,
            _clock.Frequency,
            snapshot.UtcTimestamp,
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, snapshot.QpcTimestamp),
            snapshot.ProcessId,
            snapshot.ThreadId,
            unchecked((ulong)snapshot.Handle),
            snapshot.ProcessName,
            snapshot.WindowTitle,
            $"IsMinimized={snapshot.IsMinimized};TimestampQuality=WinEventCallbackReceiptQpc");

        _sessions.PublishTelemetry(item);
        DisplayEvent(item);
    }

    private async void SessionTimer_Tick(object? sender, EventArgs e)
    {
        await _sessions.CheckPostCaptureDeadlineAsync();

        if (_etwCollector is null)
            return;

        var lost = _etwCollector.EventsLost;
        if (lost >= 0 && lost != _lastReportedEtwEventsLost)
        {
            _lastReportedEtwEventsLost = lost;
            _sessions.SetEtwEventsLost(lost);
            var qpc = _clock.GetTimestamp();
            _sessions.PublishTelemetry(new DiagnosticEvent(
                Guid.NewGuid(),
                "EtwEventsLostCounter",
                "ETW",
                qpc,
                _clock.Frequency,
                _clock.ToUtc(qpc),
                _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                null,
                null,
                null,
                null,
                null,
                $"EventsLost={lost}"));
        }
    }

    private void OnEtwEvent(DiagnosticEvent item)
    {
        _sessions.PublishTelemetry(item);
    }

    private void OnCpuSample(DiagnosticEvent item)
    {
        _sessions.PublishTelemetry(item);
        Dispatcher.InvokeAsync(() => DisplayEvent(item));
    }

    private void OnGpuSample(DiagnosticEvent item)
    {
        _sessions.PublishTelemetry(item);
        if (Interlocked.Increment(ref _gpuSampleCount) % 5 == 0)
            Dispatcher.InvokeAsync(() => DisplayEvent(item));
    }

    private void OnPresentMonFrame(DiagnosticEvent item)
    {
        _sessions.PublishTelemetry(item);
        var count = Interlocked.Increment(ref _presentMonFrameCount);
        if (count % 60 == 0)
            Dispatcher.InvokeAsync(() => DisplayEvent(item));
    }

    private void OnRenderingSummary(DiagnosticEvent item)
    {
        _sessions.PublishTelemetry(item);
        Dispatcher.InvokeAsync(() => DisplayEvent(item));
    }

    private void OnCollectorWarning(string message)
    {
        if (_isClosing)
            return;

        var qpc = _clock.GetTimestamp();
        _sessions.PublishTelemetry(new DiagnosticEvent(
            Guid.NewGuid(),
            "CollectorWarning",
            "WindowTrace",
            qpc,
            _clock.Frequency,
            _clock.ToUtc(qpc),
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
            null,
            null,
            null,
            null,
            null,
            message));

        Dispatcher.InvokeAsync(() => FooterText.Text = message);
    }

    private void OnSessionStatusChanged(string message)
    {
        if (_isClosing)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            FooterText.Text = message;
            StatusText.Text = _sessions.State.ToString();
        });
    }

    private void OnMonitorError(string message)
    {
        if (_isClosing)
            return;

        var qpc = _clock.GetTimestamp();
        _sessions.PublishTelemetry(new DiagnosticEvent(
            Guid.NewGuid(),
            "WindowMonitorWarning",
            "WinEvent",
            qpc,
            _clock.Frequency,
            _clock.ToUtc(qpc),
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
            null,
            null,
            null,
            null,
            null,
            message));

        Dispatcher.InvokeAsync(() => FooterText.Text = $"Window monitor warning: {message}");
    }

    private void AddSystemEvent(string eventType, string details)
    {
        var qpc = _clock.GetTimestamp();
        var item = new DiagnosticEvent(
            Guid.NewGuid(),
            eventType,
            "WindowTrace",
            qpc,
            _clock.Frequency,
            _clock.ToUtc(qpc),
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
            (uint)Environment.ProcessId,
            null,
            null,
            null,
            null,
            details);

        _sessions.PublishTelemetry(item);
        DisplayEvent(item);
    }

    private void DisplayEvent(DiagnosticEvent item)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => DisplayEvent(item));
            return;
        }

        Events.Insert(0, new WindowRow(
            item.UtcTimestamp,
            item.RelativeMilliseconds,
            item.EventType,
            item.ProcessId?.ToString() ?? "—",
            item.ProcessName ?? "—",
            item.WindowTitle ?? item.Details ?? "—"));

        while (Events.Count > UiEventLimit)
            Events.RemoveAt(Events.Count - 1);

        EventCountText.Text = Interlocked.Increment(ref _displayedEvents).ToString();
    }

    private async Task StopMonitoringAsync()
    {
        if (!_isMonitoring && _etwCollector is null && _cpuSampler is null &&
            _gpuSampler is null && _presentMon is null && !_sessions.IsSessionActive)
            return;

        _windowMonitor.Stop();
        _isMonitoring = false;
        _sessionTimer.Stop();

        AddSystemEvent("MonitoringStopped", "Window monitor stopped.");

        await StopTargetCollectorsAsync();

        await StopEtwCollectorAsync();
        await _sessions.StopAsync();

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Monitoring stopped";
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing)
            return;

        e.Cancel = true;
        _isClosing = true;

        _targetPickTimer.Stop();
        await StopMonitoringAsync();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            UnregisterHotKey(handle, InputMarkerHotKeyId);
        _windowSource?.RemoveHook(WindowProc);
        _windowMonitor.Dispose();

        Closing -= Window_Closing;
        Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public sealed record WindowRow(
        DateTimeOffset UtcTimestamp,
        double RelativeMilliseconds,
        string EventType,
        string ProcessId,
        string ProcessName,
        string Details);
}
