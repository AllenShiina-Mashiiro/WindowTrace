using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
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

    private EtwRealtimeCollector? _etwCollector;
    private CpuSampler? _cpuSampler;
    private GpuPdhSampler? _gpuSampler;
    private PresentMonProcess? _presentMon;
    private WindowSnapshot? _lastForegroundSnapshot;
    private bool _isMonitoring;
    private bool _isClosing;
    private long _displayedEvents;
    private long _presentMonFrameCount;

    public ObservableCollection<WindowRow> Events { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _windowMonitor = new WindowMonitor(_clock);
        _windowMonitor.ForegroundChanged += OnForegroundChanged;
        _windowMonitor.MonitorError += OnMonitorError;

        _sessions = new DiagnosticSessionManager(_clock, _history);
        _sessions.StatusChanged += OnSessionStatusChanged;

        _sessionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _sessionTimer.Tick += async (_, _) => await _sessions.CheckPostCaptureDeadlineAsync();

        EnvironmentText.Text =
            $"Environment: {RuntimeInformation.OSDescription} | " +
            $"OS version/build: {Environment.OSVersion.Version} | " +
            $"Architecture: {RuntimeInformation.OSArchitecture} | " +
            $".NET: {RuntimeInformation.FrameworkDescription} | " +
            $"QPC frequency: {_clock.Frequency:N0} ticks/sec";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMonitoring)
            return;

        try
        {
            _windowMonitor.Start();
            _isMonitoring = true;

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Monitoring window focus and starting realtime collectors";
            FooterText.Text = "Window monitor active.";

            _etwCollector = new EtwRealtimeCollector(_clock);
            _etwCollector.EventReceived += OnEtwEvent;
            _etwCollector.CollectorWarning += OnCollectorWarning;
            try
            {
                _etwCollector.Start();
                var enabled = _etwCollector.ProviderStatuses.Count(p => p.Status == "Enabled");
                FooterText.Text = $"ETW realtime consumer started. Enabled providers: {enabled}/{_etwCollector.ProviderStatuses.Count}. Kernel scheduling events may require administrator privileges.";
            }
            catch (Exception ex)
            {
                OnCollectorWarning($"ETW collector could not start: {ex.Message}");
            }

            _sessionTimer.Start();
            AddSystemEvent("MonitoringStarted", "Window monitor and realtime ETW collection started.");

            var initialForeground = _windowMonitor.CaptureForeground();
            if (initialForeground is not null)
                OnForegroundChanged(initialForeground);

            if (_sessions.TargetProcessId is uint targetPid)
                StartTargetCollectors(targetPid);
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
            await CsvExporter.ExportJsonlAsync(source, dialog.FileName);
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
        try
        {
            var snapshot = _windowMonitor.CaptureForeground();
            if (snapshot is null)
            {
                MessageBox.Show(this, "Unable to identify the current foreground process.", "WindowTrace",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _sessions.SetTarget(snapshot.ProcessId, snapshot.ProcessName, snapshot.ProcessId);
            _lastForegroundSnapshot = snapshot;
            TargetText.Text = $"Target: {snapshot.ProcessName} (PID {snapshot.ProcessId}) | {snapshot.WindowTitle}";
            if (_isMonitoring)
                StartTargetCollectors(snapshot.ProcessId);
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
            await StopTargetCollectorsAsync();
            _sessions.ClearTarget();
            TargetText.Text = "No target selected. Bring the game to the foreground, then select it below.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WindowTrace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowsePresentMonButton_Click(object sender, RoutedEventArgs e)
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
                _ = StopPresentMonAndRestartAsync(targetPid);
            }
        }
    }

    private async Task StopPresentMonAndRestartAsync(uint targetPid)
    {
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

        StartPresentMon(targetPid);
    }

    private void StartTargetCollectors(uint processId)
    {
        _ = StartCpuSamplerAsync(processId);
        _ = StartGpuSamplerAsync(processId);
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

    private async Task StopTargetCollectorsAsync()
    {
        if (_cpuSampler is not null)
        {
            await _cpuSampler.DisposeAsync();
            _cpuSampler = null;
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

        _lastForegroundSnapshot = snapshot;
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
            $"IsMinimized={snapshot.IsMinimized}");

        _sessions.PublishForeground(diagnosticEvent, snapshot.ProcessId);
        DisplayEvent(diagnosticEvent);

        CurrentWindowText.Text =
            $"{snapshot.ProcessName} (PID {snapshot.ProcessId}) | " +
            $"HWND 0x{snapshot.Handle.ToInt64():X} | " +
            $"{(string.IsNullOrWhiteSpace(snapshot.WindowTitle) ? "(untitled window)" : snapshot.WindowTitle)}";
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
        if (DateTimeOffset.UtcNow.Second % 5 == 0)
            Dispatcher.InvokeAsync(() => DisplayEvent(item));
    }

    private void OnPresentMonFrame(DiagnosticEvent item)
    {
        _sessions.PublishTelemetry(item);
        var count = Interlocked.Increment(ref _presentMonFrameCount);
        if (count % 60 == 0)
            Dispatcher.BeginInvoke(() => DisplayEvent(item));
    }

    private void OnCollectorWarning(string message)
    {
        if (_isClosing)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            FooterText.Text = message;
        });
    }

    private void OnSessionStatusChanged(string message)
    {
        if (_isClosing)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            FooterText.Text = message;
            StatusText.Text = _sessions.State.ToString();
        });
    }

    private void OnMonitorError(string message)
    {
        if (_isClosing)
            return;

        Dispatcher.BeginInvoke(() => FooterText.Text = $"Window monitor warning: {message}");
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
            Environment.ProcessId,
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
            Dispatcher.BeginInvoke(() => DisplayEvent(item));
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

        if (_etwCollector is not null)
        {
            await _etwCollector.DisposeAsync();
            var lost = _etwCollector.EventsLost;
            FooterText.Text = lost >= 0
                ? $"ETW collector stopped. Events lost reported by TraceEvent: {lost}."
                : "ETW collector stopped. Event-loss count unavailable.";
            _etwCollector = null;
        }

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

        await StopMonitoringAsync();
        _windowMonitor.Dispose();

        Closing -= Window_Closing;
        Close();
    }

    public sealed record WindowRow(
        DateTimeOffset UtcTimestamp,
        double RelativeMilliseconds,
        string EventType,
        string ProcessId,
        string ProcessName,
        string Details);
}
