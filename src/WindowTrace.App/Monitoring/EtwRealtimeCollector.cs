using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using WindowTrace.Core;

namespace WindowTrace.Monitoring;

public sealed record EtwProviderStatus(string ProviderName, string Status, string? Details);

/// <summary>
/// Real-time ETW consumer. Kernel scheduling events are enabled as a bounded,
/// focused set; selected user-mode providers are discovered at runtime.
/// This collector does not write an unbounded ETL file.
/// </summary>
public sealed class EtwRealtimeCollector : IAsyncDisposable
{
    private static readonly string[] UserProviders =
    [
        "Microsoft-Windows-Win32k",
        "Microsoft-Windows-Dwm-Core",
        "Microsoft-Windows-DxgKrnl",
        "Microsoft-Windows-DXGI"
    ];

    private readonly QpcClock _clock;
    private TraceEventSession? _session;
    private Task? _processingTask;
    private readonly List<EtwProviderStatus> _providerStatuses = [];
    private bool _disposed;

    public EtwRealtimeCollector(QpcClock clock) => _clock = clock;

    public event Action<DiagnosticEvent>? EventReceived;
    public event Action<string>? CollectorWarning;

    public IReadOnlyList<EtwProviderStatus> ProviderStatuses => _providerStatuses;
    public int EventsLost { get; private set; }
    public bool IsRunning => _session is not null;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;

        var sessionName = $"WindowTrace-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var session = new TraceEventSession(sessionName)
        {
            StopOnDispose = true
        };

        _providerStatuses.Clear();

        try
        {
            session.Source.Kernel.All += OnTraceEvent;
            session.Source.Dynamic.All += OnTraceEvent;

            try
            {
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Process |
                    KernelTraceEventParser.Keywords.Thread |
                    KernelTraceEventParser.Keywords.ContextSwitch |
                    KernelTraceEventParser.Keywords.Dispatcher);
                _providerStatuses.Add(new EtwProviderStatus(
                    "NT Kernel Logger",
                    "Enabled",
                    "Process, Thread, ContextSwitch and Dispatcher keywords"));
            }
            catch (Exception ex)
            {
                _providerStatuses.Add(new EtwProviderStatus(
                    "NT Kernel Logger",
                    "Unavailable",
                    ex.Message));
                CollectorWarning?.Invoke($"Kernel ETW unavailable: {ex.Message}");
            }

            foreach (var providerName in UserProviders)
            {
                try
                {
                    var providerGuid = TraceEventProviders.GetProviderGuidByName(providerName);
                    if (providerGuid == Guid.Empty)
                    {
                        _providerStatuses.Add(new EtwProviderStatus(
                            providerName,
                            "NotRegistered",
                            "Provider GUID was not found in the local provider registry."));
                        continue;
                    }

                    session.EnableProvider(providerGuid, TraceEventLevel.Informational, ulong.MaxValue);
                    _providerStatuses.Add(new EtwProviderStatus(providerName, "Enabled", providerGuid.ToString()));
                }
                catch (Exception ex)
                {
                    _providerStatuses.Add(new EtwProviderStatus(providerName, "Unavailable", ex.Message));
                    CollectorWarning?.Invoke($"{providerName} could not be enabled: {ex.Message}");
                }
            }

            _session = session;
            _processingTask = Task.Factory.StartNew(
                () => session.Source.Process(),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void OnTraceEvent(TraceEvent data)
    {
        try
        {
#pragma warning disable CS0618
            var qpc = data.TimeStampQPC;
#pragma warning restore CS0618
            if (qpc <= 0)
                qpc = _clock.GetTimestamp();

            var processId = data.ProcessID >= 0 ? (uint?)data.ProcessID : null;
            var threadId = data.ThreadID >= 0 ? (uint?)data.ThreadID : null;
            var providerName = data.ProviderName ?? "UnknownProvider";
            var eventName = data.EventName ?? "UnknownEvent";

            EventReceived?.Invoke(new DiagnosticEvent(
                Guid.NewGuid(),
                "Etw:" + eventName,
                providerName,
                qpc,
                _clock.Frequency,
                _clock.ToUtc(qpc),
                _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                processId,
                threadId,
                null,
                data.ProcessName,
                null,
                data.ToString()));
        }
        catch (Exception ex)
        {
            CollectorWarning?.Invoke($"ETW event normalization failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        var session = _session;
        _session = null;

        if (session is not null)
        {
            try
            {
                session.Stop(true);
            }
            catch (Exception ex)
            {
                CollectorWarning?.Invoke($"ETW session stop warning: {ex.Message}");
            }

            if (_processingTask is not null)
            {
                try { await _processingTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { CollectorWarning?.Invoke("ETW consumer did not stop within 5 seconds."); }
                catch (Exception ex) { CollectorWarning?.Invoke($"ETW consumer stopped with an error: {ex.Message}"); }
            }

            try { EventsLost = session.Source.EventsLost; } catch { EventsLost = -1; }
            session.Dispose();
        }

        _processingTask = null;
    }
}
