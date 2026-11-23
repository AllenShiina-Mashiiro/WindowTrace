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
/// It snapshots the preceding three seconds and captures through five seconds after
/// the target returns to the foreground. All sources can publish into the same buffer.
/// </summary>
public sealed class DiagnosticSessionManager
{
    private readonly object _gate = new();
    private readonly QpcClock _clock;
    private readonly BoundedEventBuffer _buffer;
    private readonly string _sessionsDirectory;
    private JsonlEventWriter? _writer;
    private Guid? _sessionId;
    private uint? _targetProcessId;
    private string? _targetProcessName;
    private uint? _lastForegroundProcessId;
    private long? _postCaptureDeadlineQpc;
    private long _sessionStartQpc;
    private double _actualPreRollMs;
    private int _preRollEventCount;
    private long _bufferEvictionsAtSessionStart;

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
    public bool IsSessionActive { get { lock (_gate) return _writer is not null; } }
    public uint? TargetProcessId { get { lock (_gate) return _targetProcessId; } }
    public string? TargetProcessName { get { lock (_gate) return _targetProcessName; } }

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
        lock (_gate)
        {
            _buffer.Add(item);
            _writer?.TryWrite(ForActiveSession(item));
        }
    }

    public void PublishForeground(DiagnosticEvent item, uint foregroundProcessId)
    {
        string? status = null;

        lock (_gate)
        {
            _buffer.Add(item);
            if (_targetProcessId is uint targetPid)
            {
                var wasTargetForeground = _lastForegroundProcessId == targetPid;
                var isTargetForeground = foregroundProcessId == targetPid;

                if (_writer is null && wasTargetForeground && !isTargetForeground)
                {
                    StartSessionLocked(item.QpcTimestamp);
                    status = $"Focus lost by target {_targetProcessName} (PID {targetPid}); diagnostic capture started.";
                }
                else if (_writer is not null)
                {
                    _writer.TryWrite(ForActiveSession(item));

                    if (isTargetForeground)
                    {
                        _postCaptureDeadlineQpc = item.QpcTimestamp + (long)(_clock.Frequency * 5d);
                        State = DiagnosticSessionState.FocusGainedPostCapture;
                        WriteSessionMarkerLocked("FocusGained", item.QpcTimestamp, "Target process regained foreground.");
                        status = "Target regained foreground; capturing the next 5 seconds.";
                    }
                    else if (State == DiagnosticSessionState.FocusGainedPostCapture)
                    {
                        _postCaptureDeadlineQpc = null;
                        State = DiagnosticSessionState.Background;
                        WriteSessionMarkerLocked("FocusLostAgain", item.QpcTimestamp, "Target left foreground again before post-capture completed.");
                        status = "Target left foreground again during post-capture; session remains active.";
                    }
                    else
                    {
                        State = DiagnosticSessionState.Background;
                    }
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

    private void StartSessionLocked(long focusLostQpc)
    {
        Directory.CreateDirectory(_sessionsDirectory);

        var sessionId = Guid.NewGuid();
        var startedUtc = _clock.ToUtc(focusLostQpc);
        var stamp = startedUtc.ToString("yyyyMMdd-HHmmss-fff");
        var shortSessionId = sessionId.ToString("N")[..8];
        var uniqueStamp = $"{stamp}-{shortSessionId}";
        var jsonlPath = Path.Combine(_sessionsDirectory, $"windowtrace-session-{uniqueStamp}.jsonl");
        var metadataPath = Path.Combine(_sessionsDirectory, $"windowtrace-session-{uniqueStamp}.session.json");

        SessionMetadata.WriteAsync(metadataPath, sessionId, startedUtc, _clock.Frequency, _targetProcessId, _targetProcessName)
            .GetAwaiter().GetResult();

        _writer = new JsonlEventWriter(jsonlPath);
        _sessionId = sessionId;
        _sessionStartQpc = focusLostQpc;
        _postCaptureDeadlineQpc = null;
        State = DiagnosticSessionState.FocusLost;

        var preRollStart = focusLostQpc - (long)(_clock.Frequency * 3d);
        var preRollEvents = _buffer.Snapshot()
            .Where(e => e.QpcTimestamp >= preRollStart && e.QpcTimestamp <= focusLostQpc)
            .OrderBy(e => e.QpcTimestamp)
            .ToArray();

        _preRollEventCount = preRollEvents.Length;
        _bufferEvictionsAtSessionStart = _buffer.DroppedEvents;
        foreach (var priorEvent in preRollEvents)
            _writer.TryWrite(ForActiveSession(priorEvent));

        var earliestQpc = preRollEvents.Length == 0 ? focusLostQpc : preRollEvents[0].QpcTimestamp;
        var actualPreRollMs = Math.Max(0, _clock.ElapsedMilliseconds(earliestQpc, focusLostQpc));
        _actualPreRollMs = actualPreRollMs;
        var qualityDetails = $"RequestedPreRollMs=3000;ActualPreRollMs={actualPreRollMs:F3};PreRollEventCount={preRollEvents.Length};BufferCapacity={_buffer.Capacity};BufferEvictionsSinceStartup={_buffer.DroppedEvents}";
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
            qualityDetails,
            _sessionId);
        _writer.TryWrite(marker);
        WriteSessionMarkerLocked("FocusLost", focusLostQpc, "Selected target process lost foreground.");
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

    public async Task CheckPostCaptureDeadlineAsync()
    {
        JsonlEventWriter? completedWriter = null;
        SessionContext? context = null;
        long completionQpc = 0;
        lock (_gate)
        {
            if (_writer is null || _postCaptureDeadlineQpc is not long deadline)
                return;

            if (_clock.GetTimestamp() < deadline)
                return;

            completedWriter = _writer;
            context = CaptureSessionContextLocked();
            completionQpc = _clock.GetTimestamp();
            completedWriter.TryWrite(new DiagnosticEvent(
                Guid.NewGuid(), "DiagnosticSessionCompleted", "WindowTrace",
                completionQpc, _clock.Frequency, _clock.ToUtc(completionQpc),
                _clock.ElapsedMilliseconds(_sessionStartQpc, completionQpc),
                _targetProcessId, null, null, _targetProcessName, null,
                $"CompletionReason=PostCaptureElapsed;WriterDropsBeforeFinalize={completedWriter.DroppedRecords}",
                _sessionId));
            LastCompletedSessionPath = completedWriter.FilePath;
            _writer = null;
            _postCaptureDeadlineQpc = null;
            State = DiagnosticSessionState.Completed;
        }

        await completedWriter.DisposeAsync();
        await WriteQualityReportAsync(completedWriter, context!, completionQpc, "PostCaptureElapsed");
        StatusChanged?.Invoke($"Diagnostic session completed: {completedWriter.FilePath}. Queue drops: {completedWriter.DroppedRecords}; size limit reached: {completedWriter.LimitReached}.");
    }

    public async Task StopAsync()
    {
        JsonlEventWriter? writer;
        SessionContext? context = null;
        long completionQpc = 0;
        lock (_gate)
        {
            writer = _writer;
            if (writer is not null)
            {
                context = CaptureSessionContextLocked();
                var stopQpc = _clock.GetTimestamp();
                completionQpc = stopQpc;
                writer.TryWrite(new DiagnosticEvent(
                    Guid.NewGuid(), "DiagnosticSessionStopped", "WindowTrace",
                    stopQpc, _clock.Frequency, _clock.ToUtc(stopQpc),
                    _clock.ElapsedMilliseconds(_sessionStartQpc, stopQpc),
                    _targetProcessId, null, null, _targetProcessName, null,
                    $"CompletionReason=MonitoringStopped;WriterDropsBeforeFinalize={writer.DroppedRecords}",
                    _sessionId));
                LastCompletedSessionPath = writer.FilePath;
            }
            _writer = null;
            _postCaptureDeadlineQpc = null;
            if (writer is not null)
                State = DiagnosticSessionState.Completed;
        }

        if (writer is not null)
        {
            await writer.DisposeAsync();
            await WriteQualityReportAsync(writer, context!, completionQpc, "MonitoringStopped");
            StatusChanged?.Invoke($"Diagnostic session stopped and saved: {writer.FilePath}. Queue drops: {writer.DroppedRecords}; size limit reached: {writer.LimitReached}.");
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
            _buffer.Capacity);

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
            writer.DroppedRecords,
            writer.BytesWritten,
            writer.MaxBytes,
            writer.LimitReached,
            writer.LastError?.ToString());

        try
        {
            var qualityPath = Path.ChangeExtension(writer.FilePath, ".quality.json");
            await SessionQualityReport.WriteAsync(qualityPath, report);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Unable to write session quality report: {ex.Message}");
        }
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
        int BufferCapacity);
}
