using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Controls;
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
    private volatile bool _isMonitoring;
    private volatile bool _isClosing;
    private long _displayedEvents;
    private long _presentMonFrameCount;
    private long _gpuSampleCount;
    private int _inputMarkerSequence;
    private int _lastReportedEtwEventsLost = -1;
    private long _lastReportedWindowEventDrops;
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
            var markerSequence = Interlocked.Increment(ref _inputMarkerSequence);
            var markerPair = (markerSequence + 1) / 2;
            var markerPhase = markerSequence % 2 == 1 ? "Start" : "Response";
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
                $"TestPair={markerPair};MarkerPhase={markerPhase};Explicit Ctrl+Alt+F12 marker. The chord is consumed; no key contents or other keyboard events were collected.");

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

        if (_sessions.TargetProcessId is null)
        {
            MessageBox.Show(this,
                "First select the game window using '1. Select game window'. Then click '2. Start monitoring'.",
                "Select a target first", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _history.Reset();
            Interlocked.Exchange(ref _displayedEvents, 0);
            Interlocked.Exchange(ref _presentMonFrameCount, 0);
            Interlocked.Exchange(ref _gpuSampleCount, 0);
            Interlocked.Exchange(ref _inputMarkerSequence, 0);
            Events.Clear();
            EventCountText.Text = "0";
            _lastReportedEtwEventsLost = -1;
            _lastReportedWindowEventDrops = _windowMonitor.DroppedPendingEvents;
            _sessions.SetEtwEventsLost(-1);

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
                PublishForegroundSnapshot(initialForeground);

            if (_sessions.TargetProcessId is uint targetPid)
            {
                EnsureEtwCollectorStarted();
                RegisterTargetProcessThreads(targetPid);
                if (initialForeground is not null)
                    RegisterTargetWindowThread(initialForeground);
                await StartTargetCollectorsAsync(targetPid);
                StatusText.Text = "Preparing capture buffer";
                FooterText.Text = "WindowTrace is warming up the rolling pre-capture buffer. It will tell you when it is ready; no stopwatch is needed.";
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

    private void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _sessions.LastCompletedReportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "No completed diagnostic report is available yet.", "WindowTrace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            OpenReportButton.IsEnabled = false;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to open the report: {ex.Message}", "WindowTrace",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private async void SetTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessions.IsSessionActive)
        {
            MessageBox.Show(this, "A diagnostic session is active. Wait for it to finish or stop monitoring before changing the target.",
                "WindowTrace", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var snapshot = ShowTargetWindowPicker();
        if (snapshot is null)
            return;

        try
        {
            await SetTargetAsync(snapshot);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WindowTrace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private WindowSnapshot? ShowTargetWindowPicker()
    {
        var picker = new Window
        {
            Title = "Select game window",
            Width = 720,
            Height = 480,
            MinWidth = 560,
            MinHeight = 360,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };

        var root = new DockPanel { Margin = new Thickness(16) };
        var instructions = new TextBlock
        {
            Text = "Choose the game's visible window. No countdown is required. If the game is not listed, launch it and refresh.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(instructions, Dock.Top);
        root.Children.Add(instructions);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var list = new ListBox { MinHeight = 240 };
        root.Children.Add(list);

        var selectedSnapshot = (WindowSnapshot?)null;
        void RefreshWindows()
        {
            list.Items.Clear();
            foreach (var candidate in GetVisibleWindowCandidates())
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = $"{candidate.ProcessName}  |  PID {candidate.ProcessId}  |  {candidate.WindowTitle}",
                    Tag = candidate,
                    Padding = new Thickness(6)
                });
            }

            if (list.Items.Count > 0)
                list.SelectedIndex = 0;
            else
                list.Items.Add(new ListBoxItem
                {
                    Content = "No visible application windows found. Launch the game, then click Refresh list.",
                    IsEnabled = false,
                    Padding = new Thickness(8)
                });
        }

        var refreshButton = new Button { Content = "Refresh list", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        refreshButton.Click += (_, _) => RefreshWindows();

        var cancelButton = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var selectButton = new Button { Content = "Select window", Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
        selectButton.Click += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: WindowSnapshot choice })
            {
                MessageBox.Show(picker, "Select a game window first.", "No window selected",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            selectedSnapshot = choice;
            picker.DialogResult = true;
        };

        buttons.Children.Add(refreshButton);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(selectButton);
        root.Children.Add(buttons);
        picker.Content = root;
        RefreshWindows();

        return picker.ShowDialog() == true ? selectedSnapshot : null;
    }

    private IReadOnlyList<WindowSnapshot> GetVisibleWindowCandidates()
    {
        var candidates = new List<WindowSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                    continue;

                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    continue;

                var snapshot = _windowMonitor.CaptureWindow(handle);
                if (snapshot is null || snapshot.ProcessId != process.Id)
                    continue;

                candidates.Add(snapshot);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                // A process can exit or deny window metadata access while the list is being refreshed.
            }
            finally
            {
                process.Dispose();
            }
        }

        return candidates
            .OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.WindowTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task SetTargetAsync(WindowSnapshot snapshot)
    {
        if (_sessions.IsSessionActive)
            throw new InvalidOperationException("A diagnostic session is active. Wait for it to finish before changing the target.");

        var currentForeground = _windowMonitor.CaptureForeground();
        var currentWindow = _windowMonitor.CaptureWindow(snapshot.Handle);
        if (currentWindow is null || currentWindow.ProcessId != snapshot.ProcessId)
            throw new InvalidOperationException("The selected game window has closed or changed. Refresh the window list and select it again.");

        snapshot = currentWindow;
        await StopTargetCollectorsAsync();
        _sessions.SetTarget(snapshot.ProcessId, snapshot.ProcessName, currentForeground?.ProcessId);
        TargetText.Text = $"Selected: {snapshot.ProcessName} (PID {snapshot.ProcessId}) | {snapshot.WindowTitle}";
        StatusText.Text = $"Target selected: {snapshot.ProcessName}";

        var selectedQpc = _clock.GetTimestamp();
        _sessions.PublishTelemetry(new DiagnosticEvent(
            Guid.NewGuid(), "TargetSelected", "WindowTrace",
            selectedQpc, _clock.Frequency, _clock.ToUtc(selectedQpc),
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, selectedQpc),
            snapshot.ProcessId, snapshot.ThreadId, unchecked((ulong)snapshot.Handle),
            snapshot.ProcessName, snapshot.WindowTitle,
            "Target selected explicitly from the visible top-level window list."));

        if (_isMonitoring)
        {
            EnsureEtwCollectorStarted();
            RegisterTargetProcessThreads(snapshot.ProcessId);
            RegisterTargetWindowThread(snapshot);
            await StartTargetCollectorsAsync(snapshot.ProcessId);
        }

        FooterText.Text = _isMonitoring
            ? "Target selected. WindowTrace will show when the rolling pre-capture buffer is ready."
            : "Target selected. Next click '2. Start monitoring'.";
    }

    private async void ClearTargetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sessions.IsSessionActive)
                throw new InvalidOperationException("A diagnostic session is active. Stop monitoring before clearing the target.");

            await StopTargetCollectorsAsync();
            await StopEtwCollectorAsync();
            _sessions.ClearTarget();
            TargetText.Text = "Step 1: Select the game window from the running-window list. Then start monitoring.";
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

        var previousPresentMon = _presentMon;
        _presentMon = null;
        if (previousPresentMon is not null)
            await previousPresentMon.DisposeAsync();

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

            string presentMonVersion;
            try { presentMonVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "Unavailable"; }
            catch { presentMonVersion = "Unavailable"; }

            var qpc = _clock.GetTimestamp();
            _sessions.PublishTelemetry(new DiagnosticEvent(
                Guid.NewGuid(),
                "PresentMonCapability",
                "PresentMonProcess",
                qpc,
                _clock.Frequency,
                _clock.ToUtc(qpc),
                _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                processId,
                null,
                null,
                null,
                null,
                $"Executable={Path.GetFileName(path)};Version={presentMonVersion};QpcTimeRequested=True;InputTracking=False;V2Metrics=True;DisplayMetadata=True"));
        }
        catch (Exception ex)
        {
            _ = collector.DisposeAsync();
            OnCollectorWarning($"PresentMon could not start: {ex.Message}");
        }
    }

    private void RegisterTargetProcessThreads(uint processId)
    {
        if (_etwCollector is null || _sessions.TargetProcessId != processId)
            return;

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var threadCount = 0;
            foreach (ProcessThread thread in process.Threads)
            {
                _etwCollector.RegisterTargetProcessThread(processId, checked((uint)thread.Id));
                threadCount++;
            }

            var kernelStatus = _etwCollector.ProviderStatuses
                .FirstOrDefault(provider => provider.ProviderName.Equals("NT Kernel Logger", StringComparison.OrdinalIgnoreCase));
            var qpc = _clock.GetTimestamp();
            _sessions.PublishTelemetry(new DiagnosticEvent(
                Guid.NewGuid(),
                "TargetThreadEnumeration",
                "ProcessThreadEnumeration",
                qpc,
                _clock.Frequency,
                _clock.ToUtc(qpc),
                _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                processId,
                null,
                null,
                process.ProcessName,
                null,
                $"EnumeratedThreadCount={threadCount};KernelEtwStatus={kernelStatus?.Status ?? "Unavailable"};KernelEtwDetails={kernelStatus?.Details ?? "CollectorUnavailable"}"));

            if (threadCount == 0)
                OnCollectorWarning("Target process thread enumeration returned zero threads. Target-thread scheduling evidence may be unavailable.");
        }
        catch (Exception ex)
        {
            OnCollectorWarning($"Target process thread enumeration unavailable: {ex.Message}");
        }
    }

    private void RegisterTargetWindowThread(WindowSnapshot snapshot)
    {
        if (_sessions.TargetProcessId == snapshot.ProcessId)
            _etwCollector?.RegisterTargetWindowThread(snapshot.ProcessId, snapshot.ThreadId);
    }

    private void EnsureEtwCollectorStarted()
    {
        if (_sessions.TargetProcessId is not uint targetPid)
            return;

        if (_etwCollector is not null)
        {
            _etwCollector.SetTargetProcessId(targetPid);
            return;
        }

        _lastReportedEtwEventsLost = -1;
        var collector = new EtwRealtimeCollector(_clock, targetPid);
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
        var collector = _etwCollector;
        _etwCollector = null;
        if (collector is null)
            return;

        try
        {
            await collector.DisposeAsync();
        }
        finally
        {
            _sessions.SetEtwEventsLost(collector.EventsLost);
            FooterText.Text = collector.EventsLost >= 0
                ? $"ETW collector stopped. Events lost: {collector.EventsLost}."
                : "ETW collector stopped. Event-loss count unavailable.";
        }
    }

    private async Task StopTargetCollectorsAsync()
    {
        var cpu = _cpuSampler;
        _cpuSampler = null;
        if (cpu is not null)
        {
            try { await cpu.DisposeAsync(); }
            catch (Exception ex) { OnCollectorWarning($"CPU collector shutdown failed: {ex.Message}"); }
        }

        var gpu = _gpuSampler;
        _gpuSampler = null;
        if (gpu is not null)
        {
            try { await gpu.DisposeAsync(); }
            catch (Exception ex) { OnCollectorWarning($"GPU collector shutdown failed: {ex.Message}"); }
        }

        var presentMon = _presentMon;
        _presentMon = null;
        if (presentMon is not null)
        {
            try { await presentMon.DisposeAsync(); }
            catch (Exception ex) { OnCollectorWarning($"PresentMon shutdown failed: {ex.Message}"); }
        }
    }

    private void PublishForegroundSnapshot(WindowSnapshot snapshot)
    {
        RegisterTargetWindowThread(snapshot);
        var item = new DiagnosticEvent(
            Guid.NewGuid(),
            "ForegroundSnapshot",
            "WindowPoll",
            snapshot.QpcTimestamp,
            _clock.Frequency,
            snapshot.UtcTimestamp,
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, snapshot.QpcTimestamp),
            snapshot.ProcessId,
            snapshot.ThreadId,
            unchecked((ulong)snapshot.Handle),
            snapshot.ProcessName,
            snapshot.WindowTitle,
            $"IsMinimized={snapshot.IsMinimized};FocusedWindowHandle={FormatWindowHandle(snapshot.FocusedWindowHandle)};GuiThreadFlags={snapshot.GuiThreadFlags?.ToString() ?? "Unavailable"};FocusQueryDelayMs={FormatOptionalMilliseconds(snapshot.FocusQueryDelayMs)};TimestampQuality=GetForegroundWindowSnapshotQpc");

        _sessions.PublishForeground(item, snapshot.ProcessId);
        DisplayEvent(item);
        Dispatcher.InvokeAsync(() =>
        {
            CurrentWindowText.Text =
                $"{snapshot.ProcessName} (PID {snapshot.ProcessId}) | " +
                $"HWND 0x{snapshot.Handle.ToInt64():X} | Focus HWND {FormatWindowHandle(snapshot.FocusedWindowHandle)} | " +
                $"{(string.IsNullOrWhiteSpace(snapshot.WindowTitle) ? "(untitled window)" : snapshot.WindowTitle)}";
        });
    }

    private static string FormatOptionalMilliseconds(double? value)
        => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "Unavailable";

    private static string FormatWindowHandle(nint? handle)
        => handle is nint value ? $"0x{value.ToInt64():X}" : "Unavailable";

    private void OnForegroundChanged(WindowSnapshot snapshot)
    {
        if (!_isMonitoring || _isClosing)
            return;

        RegisterTargetWindowThread(snapshot);
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
            $"IsMinimized={snapshot.IsMinimized};ObjectId={snapshot.ObjectId};ChildId={snapshot.ChildId};FocusedWindowHandle={FormatWindowHandle(snapshot.FocusedWindowHandle)};GuiThreadFlags={snapshot.GuiThreadFlags?.ToString() ?? "Unavailable"};FocusQueryDelayMs={FormatOptionalMilliseconds(snapshot.FocusQueryDelayMs)};WinEventSystemTimeMs={snapshot.WinEventSystemTimeMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"};TimestampQuality=WinEventCallbackReceiptQpc");

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

        RegisterTargetWindowThread(snapshot);
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
            $"IsMinimized={snapshot.IsMinimized};FocusedWindowHandle={FormatWindowHandle(snapshot.FocusedWindowHandle)};GuiThreadFlags={snapshot.GuiThreadFlags?.ToString() ?? "Unavailable"};FocusQueryDelayMs={FormatOptionalMilliseconds(snapshot.FocusQueryDelayMs)};WinEventSystemTimeMs={snapshot.WinEventSystemTimeMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"};TimestampQuality=WinEventCallbackReceiptQpc");

        _sessions.PublishTelemetry(item);
        DisplayEvent(item);
    }

    private void UpdateCaptureReadinessStatus()
    {
        if (!_isMonitoring || _sessions.TargetProcessId is null || _sessions.IsSessionActive ||
            _sessions.State is not (DiagnosticSessionState.Idle or DiagnosticSessionState.Completed))
            return;

        var coverageMs = _sessions.PreRollCoverageMilliseconds;
        if (coverageMs >= 2999)
        {
            StatusText.Text = "Ready to reproduce";
            FooterText.Text =
                $"Capture buffer ready for {_sessions.TargetProcessName}. Reproduce the issue now: Alt+Tab away, then return to the game. " +
                "After return, WindowTrace captures at least 1.5 seconds and finishes when CPU plus GPU or PresentMon evidence arrives (maximum 3 seconds).";
        }
        else
        {
            StatusText.Text = $"Preparing capture buffer ({coverageMs / 1000d:F1}/3.0 s)";
            FooterText.Text =
                "Keep monitoring running. WindowTrace is filling its rolling 3-second pre-capture buffer and will indicate when it is ready.";
        }
    }

    private async void SessionTimer_Tick(object? sender, EventArgs e)
    {
        await _sessions.CheckPostCaptureDeadlineAsync();
        UpdateCaptureReadinessStatus();

        var windowDrops = _windowMonitor.DroppedPendingEvents;
        if (windowDrops != _lastReportedWindowEventDrops)
        {
            _lastReportedWindowEventDrops = windowDrops;
            var windowQpc = _clock.GetTimestamp();
            _sessions.PublishTelemetry(new DiagnosticEvent(
                Guid.NewGuid(),
                "WindowMonitorQueueDrops",
                "WinEvent",
                windowQpc,
                _clock.Frequency,
                _clock.ToUtc(windowQpc),
                _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, windowQpc),
                null,
                null,
                null,
                null,
                null,
                $"TotalDroppedPendingEvents={windowDrops};QueueCapacity=16384;DropPolicy=RejectNewest"));
        }

        if (_etwCollector is null)
            return;

        if (!_etwCollector.IsRunning)
        {
            OnCollectorWarning("ETW realtime consumer stopped unexpectedly. ETW evidence after this point is unavailable; the session report will mark this collection gap.");
            await StopEtwCollectorAsync();
            return;
        }

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
            StatusText.Text = _sessions.State switch
            {
                DiagnosticSessionState.FocusLost or DiagnosticSessionState.Background =>
                    "Recording while game is in background",
                DiagnosticSessionState.FocusGainedPostCapture =>
                    "Game returned — collecting response evidence",
                DiagnosticSessionState.Completed => "Capture saved",
                _ => message
            };
            OpenReportButton.IsEnabled =
                _sessions.LastCompletedReportPath is string reportPath && File.Exists(reportPath);
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

        _isMonitoring = false;
        _windowMonitor.Stop();
        _sessionTimer.Stop();

        var errors = new List<string>();
        try { AddSystemEvent("MonitoringStopped", "Window monitor stopped."); }
        catch (Exception ex) { errors.Add($"Stop marker: {ex.Message}"); }

        try { await StopTargetCollectorsAsync(); }
        catch (Exception ex) { errors.Add($"Target collectors: {ex.Message}"); }

        try { await StopEtwCollectorAsync(); }
        catch (Exception ex) { errors.Add($"ETW collector: {ex.Message}"); }

        try { await _sessions.StopAsync(); }
        catch (Exception ex) { errors.Add($"Session finalization: {ex.Message}"); }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Monitoring stopped";
        FooterText.Text = errors.Count > 0
            ? $"Monitoring stopped with warnings: {string.Join(" | ", errors)}"
            : _sessions.LastCompletedReportPath is string reportPath
                ? $"Monitoring stopped. Diagnostic report: {reportPath}"
                : "Monitoring stopped and active collectors were finalized.";
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing)
            return;

        e.Cancel = true;
        _isClosing = true;

        try
        {
            await StopMonitoringAsync();
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Shutdown warning: {ex.Message}";
        }
        finally
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
                UnregisterHotKey(handle, InputMarkerHotKeyId);
            _windowSource?.RemoveHook(WindowProc);
            _windowMonitor.Dispose();

            Closing -= Window_Closing;
            Close();
        }
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
