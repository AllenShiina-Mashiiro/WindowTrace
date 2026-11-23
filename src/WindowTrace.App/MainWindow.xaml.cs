using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using WindowTrace.Core;
using WindowTrace.Monitoring;
using WindowTrace.Storage;

namespace WindowTrace;

public partial class MainWindow : Window
{
    private const int UiEventLimit = 500;

    private readonly QpcClock _clock = new();
    private readonly BoundedEventBuffer _history = new(4096);
    private readonly WindowMonitor _windowMonitor;
    private JsonlEventWriter? _writer;
    private long _monitorStartQpc;
    private bool _isMonitoring;
    private bool _isClosing;

    public ObservableCollection<WindowRow> Events { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _windowMonitor = new WindowMonitor(_clock);
        _windowMonitor.ForegroundChanged += OnForegroundChanged;
        _windowMonitor.MonitorError += OnMonitorError;

        EnvironmentText.Text =
            $"Environment: {RuntimeInformation.OSDescription} | " +
            $"Architecture: {RuntimeInformation.OSArchitecture} | " +
            $".NET: {RuntimeInformation.FrameworkDescription} | " +
            $"QPC frequency: {_clock.Frequency:N0} ticks/sec";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMonitoring)
            return;

        try
        {
            var sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowTrace",
                "Sessions");

            var path = Path.Combine(directory, $"windowtrace-{sessionId}.jsonl");
            _writer = new JsonlEventWriter(path);
            _monitorStartQpc = _clock.GetTimestamp();

            _windowMonitor.Start();
            _isMonitoring = true;

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Monitoring foreground-window changes";
            FooterText.Text = $"Writing JSONL diagnostics to: {path}";

            AddSystemEvent("MonitoringStarted", "Foreground monitor started.");
        }
        catch (Exception ex)
        {
            _isMonitoring = false;
            _writer = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = "Unable to start monitoring";
            FooterText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "WindowTrace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopMonitoringAsync();
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
            _clock.ElapsedMilliseconds(_monitorStartQpc, snapshot.QpcTimestamp),
            snapshot.ProcessId,
            snapshot.ThreadId,
            unchecked((ulong)snapshot.Handle),
            snapshot.ProcessName,
            snapshot.WindowTitle,
            $"IsMinimized={snapshot.IsMinimized}");

        AddEvent(diagnosticEvent);

        CurrentWindowText.Text =
            $"{snapshot.ProcessName} (PID {snapshot.ProcessId}) | " +
            $"HWND 0x{snapshot.Handle.ToInt64():X} | " +
            $"{(string.IsNullOrWhiteSpace(snapshot.WindowTitle) ? "(untitled window)" : snapshot.WindowTitle)}";
    }

    private void OnMonitorError(string message)
    {
        if (_isClosing)
            return;

        FooterText.Text = $"Window monitor warning: {message}";
    }

    private void AddSystemEvent(string eventType, string details)
    {
        var qpc = _clock.GetTimestamp();
        AddEvent(new DiagnosticEvent(
            Guid.NewGuid(),
            eventType,
            "WindowTrace",
            qpc,
            _clock.Frequency,
            _clock.ToUtc(qpc),
            _monitorStartQpc == 0 ? 0 : _clock.ElapsedMilliseconds(_monitorStartQpc, qpc),
            Environment.ProcessId,
            null,
            null,
            null,
            null,
            details));
    }

    private void AddEvent(DiagnosticEvent item)
    {
        _history.Add(item);

        if (_writer is not null && !_writer.TryWrite(item))
        {
            FooterText.Text = "Warning: JSONL write queue is full; one or more records could not be queued.";
        }

        Events.Insert(0, new WindowRow(
            item.UtcTimestamp,
            item.RelativeMilliseconds,
            item.ProcessId?.ToString() ?? "—",
            item.ProcessName ?? "—",
            item.WindowTitle ?? item.Details ?? "—",
            item.Details?.Contains("IsMinimized=True", StringComparison.OrdinalIgnoreCase) == true));

        while (Events.Count > UiEventLimit)
            Events.RemoveAt(Events.Count - 1);

        EventCountText.Text = _history.Snapshot().Count.ToString();
    }

    private async Task StopMonitoringAsync()
    {
        if (!_isMonitoring && _writer is null)
            return;

        _windowMonitor.Stop();
        _isMonitoring = false;

        AddSystemEvent("MonitoringStopped", "Foreground monitor stopped.");

        var writer = _writer;
        _writer = null;
        if (writer is not null)
            await writer.DisposeAsync();

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Monitoring stopped";

        if (writer?.LastError is not null)
            FooterText.Text = $"Log writer error: {writer.LastError.Message}";
        else if (writer is not null)
            FooterText.Text = $"Log saved: {writer.FilePath} | Queue drops: {writer.DroppedRecords}";
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
        string ProcessId,
        string ProcessName,
        string WindowTitle,
        bool IsMinimized);
}
