using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
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
        "Microsoft-Windows-DXGI",
        "Microsoft-Windows-Kernel-Process",
        "Microsoft-Windows-Kernel-Power"
    ];

    private readonly QpcClock _clock;
    private TraceEventSession? _session;
    private Task? _processingTask;
    private readonly List<EtwProviderStatus> _providerStatuses = [];
    private bool _disposed;
    private int _eventsLost = -1;
    private int _targetProcessId;
    private readonly ConcurrentDictionary<uint, uint> _threadProcessIds = new();
    private readonly ConcurrentDictionary<uint, long> _targetThreadReadyQpc = new();

    public EtwRealtimeCollector(QpcClock clock, uint targetProcessId)
    {
        _clock = clock;
        SetTargetProcessId(targetProcessId);
    }

    public uint TargetProcessId => unchecked((uint)Volatile.Read(ref _targetProcessId));

    public void SetTargetProcessId(uint processId)
    {
        if (processId == 0)
            throw new ArgumentOutOfRangeException(nameof(processId));

        Volatile.Write(ref _targetProcessId, checked((int)processId));
        _targetThreadReadyQpc.Clear();
        foreach (var mapping in _threadProcessIds.Where(pair => pair.Value == processId))
            PublishTargetThreadDiscovered(mapping.Key, processId, "ExistingKernelThreadMap", _clock.GetTimestamp());
    }

    public void RegisterTargetWindowThread(uint processId, uint threadId)
    {
        if (processId == TargetProcessId)
            RegisterThreadMapping(threadId, processId, "WinEventWindowThread", _clock.GetTimestamp());
    }

    public void RegisterTargetProcessThread(uint processId, uint threadId)
    {
        if (processId == TargetProcessId)
            RegisterThreadMapping(threadId, processId, "ProcessThreadEnumeration", _clock.GetTimestamp());
    }

    public event Action<DiagnosticEvent>? EventReceived;
    public event Action<string>? CollectorWarning;

    public IReadOnlyList<EtwProviderStatus> ProviderStatuses => _providerStatuses;
    public int EventsLost
    {
        get
        {
            try { return _session?.Source.EventsLost ?? _eventsLost; }
            catch { return _eventsLost; }
        }
    }
    public bool IsRunning => _session is not null && _processingTask is { IsCompleted: false };

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;

        var sessionName = $"WindowTrace-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var session = new TraceEventSession(sessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 64
        };

        _providerStatuses.Clear();

        try
        {
            session.Source.Kernel.All += OnTraceEvent;
            session.Source.Kernel.ThreadCSwitch += OnContextSwitch;
            session.Source.Dynamic.All += OnTraceEvent;

            try
            {
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Process |
                    KernelTraceEventParser.Keywords.Thread |
                    KernelTraceEventParser.Keywords.ContextSwitch |
                    KernelTraceEventParser.Keywords.Dispatcher |
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                    KernelTraceEventParser.Keywords.Interrupt);
                _providerStatuses.Add(new EtwProviderStatus(
                    "NT Kernel Logger",
                    "Enabled",
                    "Process, Thread, ContextSwitch, Dispatcher, DPC and Interrupt keywords"));
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

            if (_providerStatuses.All(p => p.Status != "Enabled"))
                throw new InvalidOperationException("No requested ETW provider could be enabled on this system.");

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
            if (eventName.Contains("CSwitch", StringComparison.OrdinalIgnoreCase))
                return;

            if (TryHandleThreadLifecycleEvent(data, qpc))
                return;

            var payloadDetails = BuildPrivacySafeDetails(providerName, eventName, data);
            var details = $"TimestampQuality=ETWQPC;EventId={data.ID};Task={data.TaskName};Opcode={data.OpcodeName};{payloadDetails}";

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
                details));
        }
        catch (Exception ex)
        {
            CollectorWarning?.Invoke($"ETW event normalization failed: {ex.Message}");
        }
    }

    private void OnContextSwitch(TraceEvent data)
    {
        try
        {
#pragma warning disable CS0618
            long qpc = data.TimeStampQPC;
#pragma warning restore CS0618
            if (qpc <= 0)
                qpc = _clock.GetTimestamp();

            var payload = ReadPayload(data);
            var processIdValue = data.ProcessID;
            var threadIdValue = data.ThreadID;
            if (processIdValue >= 0 && threadIdValue >= 0)
                RegisterThreadMapping((uint)threadIdValue, (uint)processIdValue, "ContextSwitchEventHeader", qpc);

            var oldThreadId = GetPayloadUInt32(payload, "OldThreadId");
            var newThreadId = GetPayloadUInt32(payload, "NewThreadId");
            var oldProcessId = oldThreadId is uint oldId && _threadProcessIds.TryGetValue(oldId, out var oldPid)
                ? oldPid
                : (uint?)null;
            var newProcessId = newThreadId is uint newId && _threadProcessIds.TryGetValue(newId, out var newPid)
                ? newPid
                : (uint?)null;
            var targetPid = TargetProcessId;
            var oldIsTarget = oldProcessId == targetPid;
            var newIsTarget = newProcessId == targetPid;
            var headerIsTarget = processIdValue == targetPid &&
                threadIdValue >= 0 &&
                (oldThreadId == (uint)threadIdValue || newThreadId == (uint)threadIdValue);

            if (!oldIsTarget && !newIsTarget && !headerIsTarget)
                return;

            var targetRole = oldIsTarget && newIsTarget ? "Both" :
                oldIsTarget ? "Outgoing" :
                newIsTarget ? "Incoming" : "HeaderMatched";
            var targetThreadId = oldIsTarget ? oldThreadId :
                newIsTarget ? newThreadId :
                threadIdValue >= 0 ? (uint?)threadIdValue : null;

            var readyToRunDetail = "ReadyToRunMs=Unavailable";
            if (newIsTarget && newThreadId is uint scheduledThreadId &&
                _targetThreadReadyQpc.TryRemove(scheduledThreadId, out var readyQpc) &&
                qpc >= readyQpc)
            {
                var readyToRunMs = (qpc - readyQpc) * 1000d / _clock.Frequency;
                readyToRunDetail = $"ReadyToRunMs={readyToRunMs.ToString("F3", CultureInfo.InvariantCulture)}";
            }

            var details = string.Join(";",
                "TimestampQuality=ETWQPC",
                $"TargetThreadRole={targetRole}",
                $"TargetThreadId={targetThreadId?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}",
                $"OldThreadId={oldThreadId?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}",
                $"OldThreadProcessId={oldProcessId?.ToString(CultureInfo.InvariantCulture) ?? "Unmapped"}",
                $"NewThreadId={newThreadId?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}",
                $"NewThreadProcessId={newProcessId?.ToString(CultureInfo.InvariantCulture) ?? "Unmapped"}",
                $"HeaderProcessId={(processIdValue >= 0 ? processIdValue.ToString(CultureInfo.InvariantCulture) : "Unavailable")}",
                $"HeaderThreadId={(threadIdValue >= 0 ? threadIdValue.ToString(CultureInfo.InvariantCulture) : "Unavailable")}",
                FormatPayloadValue(payload, "OldThreadState"),
                FormatPayloadValue(payload, "OldThreadWaitReason"),
                FormatPayloadValue(payload, "OldThreadWaitMode"),
                FormatPayloadValue(payload, "NewThreadWaitTime"),
                readyToRunDetail);

            EventReceived?.Invoke(new DiagnosticEvent(
                Guid.NewGuid(),
                "TargetThreadContextSwitch",
                "NT Kernel Logger",
                qpc,
                _clock.Frequency,
                _clock.ToUtc(qpc),
                _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                targetPid,
                targetThreadId,
                null,
                null,
                null,
                details));
        }
        catch (Exception ex)
        {
            CollectorWarning?.Invoke($"Context-switch event parsing failed: {ex.Message}");
        }
    }

    private bool TryHandleThreadLifecycleEvent(TraceEvent data, long qpc)
    {
        var taskName = data.TaskName ?? string.Empty;
        var opcode = data.OpcodeName ?? string.Empty;
        var eventName = data.EventName ?? string.Empty;
        var isReadyThread = opcode.Equals("ReadyThread", StringComparison.OrdinalIgnoreCase) ||
            eventName.Contains("ReadyThread", StringComparison.OrdinalIgnoreCase);
        if (!taskName.Equals("Thread", StringComparison.OrdinalIgnoreCase) && !isReadyThread)
            return false;

        var payload = ReadPayload(data);
        if (isReadyThread)
        {
            var readyThreadId = GetPayloadUInt32(payload, "TThreadId", "ThreadId");
            if (readyThreadId is uint threadId &&
                _threadProcessIds.TryGetValue(threadId, out var readyProcessId) &&
                readyProcessId == TargetProcessId)
            {
                _targetThreadReadyQpc[threadId] = qpc;
                EventReceived?.Invoke(new DiagnosticEvent(
                    Guid.NewGuid(),
                    "TargetThreadReady",
                    "NT Kernel Logger",
                    qpc,
                    _clock.Frequency,
                    _clock.ToUtc(qpc),
                    _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                    readyProcessId,
                    threadId,
                    null,
                    null,
                    null,
                    $"TimestampQuality=ETWQPC;ReadyThreadId={threadId};{FormatPayloadValue(payload, "AdjustReason")};{FormatPayloadValue(payload, "AdjustIncrement")};{FormatPayloadValue(payload, "Flag")}"));
            }

            return true;
        }

        var lifecycleThreadId = GetPayloadUInt32(payload, "ThreadId", "TThreadId");
        var processId = GetPayloadUInt32(payload, "ProcessId");
        if (lifecycleThreadId is null)
            return true;

        if (opcode.Equals("Start", StringComparison.OrdinalIgnoreCase) ||
            opcode.Equals("DCStart", StringComparison.OrdinalIgnoreCase))
        {
            if (processId is uint pid)
                RegisterThreadMapping(lifecycleThreadId.Value, pid, "KernelThread" + opcode, qpc);
        }
        else if (opcode.Equals("End", StringComparison.OrdinalIgnoreCase) ||
                 opcode.Equals("DCEnd", StringComparison.OrdinalIgnoreCase))
        {
            _threadProcessIds.TryRemove(lifecycleThreadId.Value, out _);
            _targetThreadReadyQpc.TryRemove(lifecycleThreadId.Value, out _);
        }

        return true;
    }

    private void RegisterThreadMapping(uint threadId, uint processId, string mappingSource, long qpc)
    {
        if (threadId == 0 || processId == 0)
            return;

        if (_threadProcessIds.TryGetValue(threadId, out var previousPid) && previousPid == processId)
            return;

        _threadProcessIds[threadId] = processId;
        if (processId == TargetProcessId)
            PublishTargetThreadDiscovered(threadId, processId, mappingSource, qpc);
    }

    private void PublishTargetThreadDiscovered(uint threadId, uint processId, string mappingSource, long qpc)
    {
        EventReceived?.Invoke(new DiagnosticEvent(
            Guid.NewGuid(),
            "TargetThreadDiscovered",
            "NT Kernel Logger",
            qpc,
            _clock.Frequency,
            _clock.ToUtc(qpc),
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
            processId,
            threadId,
            null,
            null,
            null,
            $"ThreadMappingSource={mappingSource};ThreadProcessAssociation={mappingSource}"));
    }

    private static Dictionary<string, object?> ReadPayload(TraceEvent data)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var names = data.PayloadNames;
        for (var i = 0; i < names.Length; i++)
            payload[names[i]] = data.PayloadValue(i);
        return payload;
    }

    private static uint? GetPayloadUInt32(IReadOnlyDictionary<string, object?> payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetValue(name, out var value) || value is null)
                continue;

            try
            {
                var converted = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
                if (converted > 0)
                    return converted;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
            }
        }

        return null;
    }

    private static string FormatPayloadValue(IReadOnlyDictionary<string, object?> payload, string name)
        => payload.TryGetValue(name, out var value)
            ? $"{name}={Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Unavailable"}"
            : $"{name}=Unavailable";

    private static string BuildPrivacySafeDetails(string providerName, string eventName, TraceEvent data)
    {
        if (providerName.Contains("Win32k", StringComparison.OrdinalIgnoreCase))
            return "PayloadOmittedByPrivacyPolicy;Win32k timing and event metadata only.";

        if (eventName.Contains("Process", StringComparison.OrdinalIgnoreCase))
            return "PayloadOmittedToAvoidCommandLineCapture;Process event metadata only.";

        return data.ToString();
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

            try { _eventsLost = session.Source.EventsLost; } catch { _eventsLost = -1; }
            session.Dispose();
        }

        _processingTask = null;
    }
}
