using System.IO;
using WindowTrace.Core;
using WindowTrace.Storage;

namespace WindowTrace.Monitoring;

public enum DiagnosticSessionState
{
    Idle,
    FocusLost,
    Background,
    FocusGainedPostCapture,
    Completed
}

/// <summary>
/// Creates a diagnostic session when the selected target process loses foreground.
/// It snapshots the preceding three seconds and adaptively captures at least 1.5 seconds
/// after the target returns to the foreground, with a 3-second hard maximum.
/// </summary>
public sealed class DiagnosticSessionManager
{
    private readonly object _gate = new();
    private readonly QpcClock _clock;
    private readonly BoundedEventBuffer _buffer;
    private readonly string _sessionsDirectory;
    private JsonlEventWriter? _writer;
    private Task<PreRollInitializationResult>? _preRollInitializationTask;
    private Guid? _sessionId;
    private uint? _targetProcessId;
    private string? _targetProcessName;
    private uint? _lastForegroundProcessId;
    private long? _postCaptureMinimumQpc;
    private long? _postCaptureMaximumQpc;
    private long? _postCaptureStartedQpc;
    private bool _postFocusCpuSampleSeen;
    private bool _postFocusGpuSampleSeen;
    private bool _postFocusFrameSampleSeen;
    private long _sessionStartQpc;
    private double _actualPreRollMs;
    private int _preRollEventCount;
    private int _preRollEventsQueued;
    private int _preRollEventsRejected;
    private long _bufferEvictionsAtSessionStart;
    private int? _etwEventsLost;
    private long _etwTimestampFallbackCount;
    private long _etwThrottledEventCount;

    public DiagnosticSessionManager(QpcClock clock, BoundedEventBuffer buffer, string? sessionsDirectory = null)
    {
        _clock = clock;
        _buffer = buffer;
        _sessionsDirectory = sessionsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowTrace",
            "Sessions");
    }

    public event Action<string>? StatusChanged;

    public DiagnosticSessionState State { get; private set; } = DiagnosticSessionState.Idle;
    public string? LastCompletedSessionPath { get; private set; }
    public string? LastCompletedReportPath { get; private set; }
    public bool IsSessionActive { get { lock (_gate) return _writer is not null; } }
    public double PreRollCoverageMilliseconds
    {
        get
        {
            var events = _buffer.Snapshot();
            if (events.Count == 0)
                return 0;

            var earliestQpc = events.Min(item => item.QpcTimestamp);
            return Math.Clamp(_clock.ElapsedMilliseconds(earliestQpc, _clock.GetTimestamp()), 0d, 3000d);
        }
    }
    public uint? TargetProcessId { get { lock (_gate) return _targetProcessId; } }
    public string? TargetProcessName { get { lock (_gate) return _targetProcessName; } }

    public void SetEtwEventsLost(int eventsLost)
    {
        lock (_gate)
            _etwEventsLost = eventsLost >= 0 ? eventsLost : null;
    }

    public void SetEtwTimestampFallbackCount(long count)
    {
        lock (_gate)
            _etwTimestampFallbackCount = Math.Max(0, count);
    }

    public void SetEtwThrottledEventCount(long count)
    {
        lock (_gate)
            _etwThrottledEventCount = Math.Max(0, count);
    }

    public void SetTarget(uint processId, string processName, uint? currentForegroundProcessId)
    {
        if (processId == 0)
            throw new ArgumentOutOfRangeException(nameof(processId));

        lock (_gate)
        {
            if (_writer is not null)
                throw new InvalidOperationException("Stop the active diagnostic session before changing the target process.");

            _targetProcessId = processId;
            _targetProcessName = processName;
            _lastForegroundProcessId = currentForegroundProcessId;
            State = DiagnosticSessionState.Idle;
        }

        StatusChanged?.Invoke($"Target selected: {processName} (PID {processId}). Switch away from this window to trigger a diagnostic session.");
    }

    public void UpdateForegroundBaseline(uint? currentForegroundProcessId)
    {
        lock (_gate)
        {
            if (_writer is not null)
                return;

            _lastForegroundProcessId = currentForegroundProcessId;
        }
    }

    public void ClearTarget()
    {
        lock (_gate)
        {
            if (_writer is not null)
                throw new InvalidOperationException("A diagnostic session is active. Stop monitoring before clearing the target.");

            _targetProcessId = null;
            _targetProcessName = null;
            _lastForegroundProcessId = null;
            State = DiagnosticSessionState.Idle;
        }

        StatusChanged?.Invoke("Target process cleared.");
    }

    public void PublishTelemetry(DiagnosticEvent item)
    {
        // Keep the rolling-buffer lock independent from the session-state lock:
        // a pre-roll snapshot must never hold the same lock needed by ETW writers.
        _buffer.Add(item);
        lock (_gate)
        {
            if (_writer is not null)
            {
                var accepted = _writer.TryWrite(ForActiveSession(item));
                if (accepted &&
                    State == DiagnosticSessionState.FocusGainedPostCapture &&
                    _postCaptureStartedQpc is long focusQpc &&
                    item.QpcTimestamp >= focusQpc)
                {
                    if (item.EventType == "CpuSample")
                        _postFocusCpuSampleSeen = true;
                    else if (item.EventType == "GpuPdhSample" &&
                             !(item.Details?.Contains("No GPU counter instances returned valid data", StringComparison.OrdinalIgnoreCase) ?? false))
                        _postFocusGpuSampleSeen = true;
                    else if (item.EventType is "PresentMonFrame" or "RenderingSummary")
                        _postFocusFrameSampleSeen = true;
                }
            }
        }
    }

    public void PublishForeground(DiagnosticEvent item, uint foregroundProcessId)
    {
        string? status = null;

        _buffer.Add(item);
        lock (_gate)
        {
            if (_targetProcessId is uint targetPid)
            {
                var wasTargetForeground = _lastForegroundProcessId == targetPid;
                var isTargetForeground = foregroundProcessId == targetPid;

                if (_writer is null && wasTargetForeground && !isTargetForeground)
                {
                    StartSessionLocked(item);
                    status = $"Focus lost by target {_targetProcessName} (PID {targetPid}); diagnostic capture started.";
                }
                else if (_writer is not null)
                {
                    _writer.TryWrite(ForActiveSession(item));

                    if (isTargetForeground && !wasTargetForeground)
                    {
                        _postCaptureStartedQpc = item.QpcTimestamp;
                        _postCaptureMinimumQpc = item.QpcTimestamp + (long)(_clock.Frequency * 1.5d);
                        _postCaptureMaximumQpc = item.QpcTimestamp + (long)(_clock.Frequency * 3d);
                        _postFocusCpuSampleSeen = false;
                        _postFocusGpuSampleSeen = false;
                        _postFocusFrameSampleSeen = false;
                        State = DiagnosticSessionState.FocusGainedPostCapture;
                        WriteSessionMarkerLocked("FocusGained", item.QpcTimestamp,
                            $"Target regained foreground. Adaptive post-focus capture: minimum 1500 ms; maximum 3000 ms;{GetWindowTimestampDetails(item)}");
                        status = "Game returned. Capturing the critical 1.5-second response window; will finish as soon as CPU and GPU samples are available (maximum 3 seconds).";
                    }
                    else if (!isTargetForeground && State == DiagnosticSessionState.FocusGainedPostCapture)
                    {
                        _postCaptureMinimumQpc = null;
                        _postCaptureMaximumQpc = null;
                        _postCaptureStartedQpc = null;
                        _postFocusCpuSampleSeen = false;
                        _postFocusGpuSampleSeen = false;
                        _postFocusFrameSampleSeen = false;
                        State = DiagnosticSessionState.Background;
                        WriteSessionMarkerLocked("FocusLostAgain", item.QpcTimestamp,
                            $"Target left foreground again before post-capture completed;{GetWindowTimestampDetails(item)}");
                        status = "Target left foreground again during post-capture; session remains active.";
                    }
                    else if (!isTargetForeground)
                    {
                        State = DiagnosticSessionState.Background;
                    }
                    // Duplicate foreground notifications do not extend the post-capture window.
                }

                _lastForegroundProcessId = foregroundProcessId;
            }
            else
            {
                _lastForegroundProcessId = foregroundProcessId;
            }
        }

        if (status is not null)
            StatusChanged?.Invoke(status);
    }

    private void StartSessionLocked(DiagnosticEvent focusLostEvent)
    {
        var focusLostQpc = focusLostEvent.QpcTimestamp;
        var triggerTimestampDetails = GetWindowTimestampDetails(focusLostEvent);
        var sessionId = Guid.NewGuid();
        var startedUtc = _clock.ToUtc(focusLostQpc);
        var stamp = startedUtc.ToString("yyyyMMdd-HHmmss-fff");
        var shortSessionId = sessionId.ToString("N")[..8];
        var uniqueStamp = $"{stamp}-{shortSessionId}";
        var jsonlPath = Path.Combine(_sessionsDirectory, $"windowtrace-session-{uniqueStamp}.jsonl");
        var metadataPath = Path.Combine(_sessionsDirectory, $"windowtrace-session-{uniqueStamp}.session.json");

        var writer = new JsonlEventWriter(jsonlPath);
        _writer = writer;
        _sessionId = sessionId;
        LastCompletedReportPath = null;
        _sessionStartQpc = focusLostQpc;
        _postCaptureMinimumQpc = null;
        _postCaptureMaximumQpc = null;
        _postCaptureStartedQpc = null;
        _postFocusCpuSampleSeen = false;
        _postFocusGpuSampleSeen = false;
        _postFocusFrameSampleSeen = false;
        State = DiagnosticSessionState.FocusLost;

        _preRollEventCount = 0;
        _preRollEventsQueued = 0;
        _preRollEventsRejected = 0;
        _bufferEvictionsAtSessionStart = 0;
        _actualPreRollMs = 0;
        var marker = new DiagnosticEvent(
            Guid.NewGuid(),
            "DiagnosticSessionStarted",
            "WindowTrace",
            focusLostQpc,
            _clock.Frequency,
            _clock.ToUtc(focusLostQpc),
            0,
            _targetProcessId,
            null,
            null,
            _targetProcessName,
            null,
            $"PreRollInitialization=Pending;RequestedPreRollMs=3000;{triggerTimestampDetails}",
            _sessionId);
        writer.TryWrite(marker);
        WriteSessionMarkerLocked("FocusLost", focusLostQpc,
            $"Selected target process lost foreground;{triggerTimestampDetails}");

        var initialization = new SessionInitialization(
            writer, metadataPath, sessionId, startedUtc, focusLostQpc,
            _targetProcessId, _targetProcessName);
        _preRollInitializationTask = Task.Run(() => InitializeSessionAsync(initialization));
    }

    private async Task<PreRollInitializationResult> InitializeSessionAsync(SessionInitialization initialization)
    {
        var preRollStart = initialization.FocusLostQpc - (long)(_clock.Frequency * 3d);
        var snapshot = _buffer.Snapshot();
        var preRollEvents = snapshot
            .Where(item => item.QpcTimestamp >= preRollStart && item.QpcTimestamp <= initialization.FocusLostQpc)
            .OrderBy(item => item.QpcTimestamp)
            .ToArray();

        var queued = 0;
        var rejected = 0;
        foreach (var priorEvent in preRollEvents)
        {
            var tagged = priorEvent with
            {
                SessionId = initialization.SessionId,
                RelativeMilliseconds = _clock.ElapsedMilliseconds(initialization.FocusLostQpc, priorEvent.QpcTimestamp),
                CapturePhase = "PreRoll"
            };
            if (await initialization.Writer.WriteAsync(tagged))
                queued++;
            else
                rejected++;
        }

        var earliestQpc = preRollEvents.Length == 0
            ? initialization.FocusLostQpc
            : preRollEvents[0].QpcTimestamp;
        var actualPreRollMs = Math.Max(0, _clock.ElapsedMilliseconds(earliestQpc, initialization.FocusLostQpc));
        var evictionsAtStart = _buffer.DroppedEvents;
        var qualityDetails =
            $"RequestedPreRollMs=3000;ActualPreRollMs={actualPreRollMs:F3};PreRollEventsSelected={preRollEvents.Length};PreRollEventsQueued={queued};PreRollEventsRejected={rejected};BufferCapacity={_buffer.Capacity};BufferEvictionsSinceStartup={evictionsAtStart}";

        try
        {
            await SessionMetadata.WriteAsync(
                initialization.MetadataPath,
                initialization.SessionId,
                initialization.StartedUtc,
                _clock.Frequency,
                initialization.TargetProcessId,
                initialization.TargetProcessName);
        }
        catch (Exception ex)
        {
            var qpc = _clock.GetTimestamp();
            initialization.Writer.TryWrite(new DiagnosticEvent(
                Guid.NewGuid(), "CollectorWarning", "SessionMetadata",
                qpc, _clock.Frequency, _clock.ToUtc(qpc),
                _clock.ElapsedMilliseconds(initialization.FocusLostQpc, qpc),
                initialization.TargetProcessId, null, null, initialization.TargetProcessName, null,
                $"Session metadata write failed: {ex.Message}", initialization.SessionId));
        }

        var qpcQuality = _clock.GetTimestamp();
        initialization.Writer.TryWrite(new DiagnosticEvent(
            Guid.NewGuid(), "PreRollCaptureSummary", "DiagnosticSessionManager",
            qpcQuality, _clock.Frequency, _clock.ToUtc(qpcQuality),
            _clock.ElapsedMilliseconds(initialization.FocusLostQpc, qpcQuality),
            initialization.TargetProcessId, null, null, initialization.TargetProcessName, null,
            qualityDetails + ";TimestampQuality=WindowTraceReceiptQPC", initialization.SessionId));

        return new PreRollInitializationResult(
            actualPreRollMs, preRollEvents.Length, queued, rejected, evictionsAtStart);
    }

    private static string GetWindowTimestampDetails(DiagnosticEvent item)
    {
        var timestampQuality = GetDetailValue(item.Details, "TimestampQuality") ?? "Unavailable";
        var winEventTime = GetDetailValue(item.Details, "WinEventSystemTimeMs") ?? "Unavailable";
        return $"TriggerTimestampQuality={timestampQuality};WinEventSystemTimeMs={winEventTime}";
    }

    private static string? GetDetailValue(string? details, string key)
    {
        if (string.IsNullOrWhiteSpace(details))
            return null;

        foreach (var part in details.Split(';'))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part[..separator].Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(separator + 1)..];
        }

        return null;
    }

    private void WriteSessionMarkerLocked(string eventType, long qpc, string details)
    {
        if (_writer is null)
            return;

        _writer.TryWrite(new DiagnosticEvent(
            Guid.NewGuid(),
            eventType,
            "DiagnosticSessionManager",
            qpc,
            _clock.Frequency,
            _clock.ToUtc(qpc),
            _clock.ElapsedMilliseconds(_sessionStartQpc, qpc),
            _targetProcessId,
            null,
            null,
            _targetProcessName,
            null,
            details,
            _sessionId));
    }

    private DiagnosticEvent ForActiveSession(DiagnosticEvent item)
        => item with
        {
            SessionId = _sessionId,
            RelativeMilliseconds = _clock.ElapsedMilliseconds(_sessionStartQpc, item.QpcTimestamp)
        };

    public async Task CheckPostCaptureDeadlineAsync(long? currentQpc = null)
    {
        JsonlEventWriter? completedWriter = null;
        SessionContext? context = null;
        long completionQpc = 0;
        string completionReason = string.Empty;
        Task<PreRollInitializationResult>? preRollInitializationTask = null;
        lock (_gate)
        {
            if (_writer is null ||
                _postCaptureMinimumQpc is not long minimumQpc ||
                _postCaptureMaximumQpc is not long maximumQpc)
                return;

            var nowQpc = currentQpc ?? _clock.GetTimestamp();
            if (nowQpc < minimumQpc)
                return;

            var graphicsEvidenceReady = _postFocusGpuSampleSeen || _postFocusFrameSampleSeen;
            var evidenceReady = _postFocusCpuSampleSeen && graphicsEvidenceReady;
            var maximumElapsed = nowQpc >= maximumQpc;
            if (!evidenceReady && !maximumElapsed)
                return;

            completionReason = evidenceReady ? "PostFocusEvidenceReady" : "PostFocusMaximumElapsed";
            completedWriter = _writer;
            context = CaptureSessionContextLocked();
            preRollInitializationTask = _preRollInitializationTask;
            _preRollInitializationTask = null;
            completionQpc = nowQpc;
            completedWriter.TryWrite(new DiagnosticEvent(
                Guid.NewGuid(), "DiagnosticSessionCompleted", "WindowTrace",
                completionQpc, _clock.Frequency, _clock.ToUtc(completionQpc),
                _clock.ElapsedMilliseconds(_sessionStartQpc, completionQpc),
                _targetProcessId, null, null, _targetProcessName, null,
                $"CompletionReason={completionReason};TimestampQuality=WindowTraceTimerReceiptQPC;PostFocusCpuSampleSeen={_postFocusCpuSampleSeen};PostFocusGpuSampleSeen={_postFocusGpuSampleSeen};PostFocusFrameSampleSeen={_postFocusFrameSampleSeen};MinimumCaptureMs=1500;MaximumCaptureMs=3000;WriterDropsBeforeFinalize={completedWriter.DroppedRecords}",
                _sessionId));
            LastCompletedSessionPath = completedWriter.FilePath;
            _writer = null;
            _preRollInitializationTask = null;
            _postCaptureMinimumQpc = null;
            _postCaptureMaximumQpc = null;
            _postCaptureStartedQpc = null;
            _postFocusCpuSampleSeen = false;
            _postFocusGpuSampleSeen = false;
            _postFocusFrameSampleSeen = false;
            State = DiagnosticSessionState.Completed;
        }

        var preRollResult = preRollInitializationTask is null
            ? PreRollInitializationResult.Empty
            : await preRollInitializationTask;
        context = context! with
        {
            ActualPreRollMs = preRollResult.ActualPreRollMs,
            PreRollEventCount = preRollResult.Selected,
            PreRollEventsQueued = preRollResult.Queued,
            PreRollEventsRejected = preRollResult.Rejected,
            BufferEvictionsAtSessionStart = preRollResult.BufferEvictionsAtStart
        };
        await completedWriter.DisposeAsync();
        await WriteQualityReportAsync(completedWriter, context, completionQpc, completionReason);
        StatusChanged?.Invoke($"Diagnostic session completed ({completionReason}): {completedWriter.FilePath}. Records not persisted: {completedWriter.DroppedRecords}; regular telemetry budget reached: {completedWriter.RegularFileBudgetReached}; hard file cap reached: {completedWriter.TotalFileLimitReached}.");
    }

    public async Task StopAsync()
    {
        JsonlEventWriter? writer;
        SessionContext? context = null;
        Task<PreRollInitializationResult>? preRollInitializationTask = null;
        long completionQpc = 0;
        lock (_gate)
        {
            writer = _writer;
            if (writer is not null)
            {
                context = CaptureSessionContextLocked();
                preRollInitializationTask = _preRollInitializationTask;
                var stopQpc = _clock.GetTimestamp();
                completionQpc = stopQpc;
                writer.TryWrite(new DiagnosticEvent(
                    Guid.NewGuid(), "DiagnosticSessionStopped", "WindowTrace",
                    stopQpc, _clock.Frequency, _clock.ToUtc(stopQpc),
                    _clock.ElapsedMilliseconds(_sessionStartQpc, stopQpc),
                    _targetProcessId, null, null, _targetProcessName, null,
                    $"CompletionReason=MonitoringStopped;TimestampQuality=WindowTraceStopRequestQPC;WriterDropsBeforeFinalize={writer.DroppedRecords}",
                    _sessionId));
                LastCompletedSessionPath = writer.FilePath;
            }
            _writer = null;
            _preRollInitializationTask = null;
            _postCaptureMinimumQpc = null;
            _postCaptureMaximumQpc = null;
            _postCaptureStartedQpc = null;
            _postFocusCpuSampleSeen = false;
            _postFocusGpuSampleSeen = false;
            _postFocusFrameSampleSeen = false;
            if (writer is not null)
                State = DiagnosticSessionState.Completed;
        }

        if (writer is not null)
        {
            var preRollResult = preRollInitializationTask is null
                ? PreRollInitializationResult.Empty
                : await preRollInitializationTask;
            context = context! with
            {
                ActualPreRollMs = preRollResult.ActualPreRollMs,
                PreRollEventCount = preRollResult.Selected,
                PreRollEventsQueued = preRollResult.Queued,
                PreRollEventsRejected = preRollResult.Rejected,
                BufferEvictionsAtSessionStart = preRollResult.BufferEvictionsAtStart
            };
            await writer.DisposeAsync();
            await WriteQualityReportAsync(writer, context, completionQpc, "MonitoringStopped");
            StatusChanged?.Invoke($"Diagnostic session stopped and saved: {writer.FilePath}. Records not persisted: {writer.DroppedRecords}; regular telemetry budget reached: {writer.RegularFileBudgetReached}; hard file cap reached: {writer.TotalFileLimitReached}.");
        }
    }

    private SessionContext CaptureSessionContextLocked()
        => new(
            _sessionId,
            _targetProcessId,
            _targetProcessName,
            _sessionStartQpc,
            _actualPreRollMs,
            _preRollEventCount,
            _bufferEvictionsAtSessionStart,
            _buffer.DroppedEvents,
            _etwEventsLost,
            _buffer.Capacity,
            _etwTimestampFallbackCount,
            _etwThrottledEventCount,
            _preRollEventsQueued,
            _preRollEventsRejected);

    private async Task WriteQualityReportAsync(
        JsonlEventWriter writer,
        SessionContext context,
        long completionQpc,
        string reason)
    {
        var report = new SessionQualityReport(
            context.SessionId,
            _clock.ToUtc(completionQpc),
            reason,
            context.TargetProcessId,
            context.TargetProcessName,
            context.SessionStartQpc,
            completionQpc,
            3000,
            context.ActualPreRollMs,
            context.PreRollEventCount,
            context.BufferCapacity,
            context.BufferEvictionsAtSessionStart,
            context.BufferEvictionsAtFinalization,
            context.EtwEventsLostAtFinalization,
            writer.DroppedRecords,
            writer.BytesWritten,
            writer.MaxBytes,
            writer.TotalFileLimitReached,
            writer.LastError?.ToString())
        {
            QueueOverflowDroppedRecords = writer.QueueOverflowDroppedRecords,
            PriorityQueueOverflowDroppedRecords = writer.PriorityQueueOverflowDroppedRecords,
            RegularBudgetDroppedRecords = writer.RegularBudgetDroppedRecords,
            HardFileLimitDroppedRecords = writer.HardFileLimitDroppedRecords,
            RegularFileBudgetReached = writer.RegularFileBudgetReached,
            FileLimitDroppedRecords = writer.FileLimitDroppedRecords,
            WriterErrorDroppedRecords = writer.WriterErrorDroppedRecords,
            EtwTimestampFallbackCount = context.EtwTimestampFallbackCount,
            EtwThrottledEventCount = context.EtwThrottledEventCount,
            PreRollEventsSelected = context.PreRollEventCount,
            PreRollEventsQueued = context.PreRollEventsQueued,
            PreRollEventsRejected = context.PreRollEventsRejected,
            PreRollEventsWritten = writer.PreRollRecordsWritten
        };

        try
        {
            var qualityPath = Path.ChangeExtension(writer.FilePath, ".quality.json");
            await SessionQualityReport.WriteAsync(qualityPath, report);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Unable to write session quality report: {ex.Message}");
        }

        try
        {
            LastCompletedReportPath = await Task.Run(() => SessionReportGenerator.WriteAsync(writer.FilePath));
            StatusChanged?.Invoke($"Human-readable diagnostic report saved: {LastCompletedReportPath}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Unable to generate diagnostic report: {ex.Message}");
        }
    }

    private sealed record SessionInitialization(
        JsonlEventWriter Writer,
        string MetadataPath,
        Guid SessionId,
        DateTimeOffset StartedUtc,
        long FocusLostQpc,
        uint? TargetProcessId,
        string? TargetProcessName);

    private sealed record PreRollInitializationResult(
        double ActualPreRollMs,
        int Selected,
        int Queued,
        int Rejected,
        long BufferEvictionsAtStart)
    {
        public static PreRollInitializationResult Empty { get; } = new(0, 0, 0, 0, 0);
    }

    private sealed record SessionContext(
        Guid? SessionId,
        uint? TargetProcessId,
        string? TargetProcessName,
        long SessionStartQpc,
        double ActualPreRollMs,
        int PreRollEventCount,
        long BufferEvictionsAtSessionStart,
        long BufferEvictionsAtFinalization,
        int? EtwEventsLostAtFinalization,
        int BufferCapacity,
        long EtwTimestampFallbackCount,
        long EtwThrottledEventCount,
        int PreRollEventsQueued,
        int PreRollEventsRejected);
}
