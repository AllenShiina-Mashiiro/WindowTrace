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

    public DiagnosticSessionManager(QpcClock clock, BoundedEventBuffer buffer)
    {
        _clock = clock;
        _buffer = buffer;
        _sessionsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowTrace",
            "Sessions");
    }

    public event Action<string>? StatusChanged;

    public DiagnosticSessionState State { get; private set; } = DiagnosticSessionState.Idle;
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
        _buffer.Add(item);

        lock (_gate)
        {
            _writer?.TryWrite(item);
        }
    }

    public void PublishForeground(DiagnosticEvent item, uint foregroundProcessId)
    {
        _buffer.Add(item);
        string? status = null;

        lock (_gate)
        {
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
                    _writer.TryWrite(item);

                    if (isTargetForeground)
                    {
                        _postCaptureDeadlineQpc = item.QpcTimestamp + (long)(_clock.Frequency * 5d);
                        State = DiagnosticSessionState.FocusGainedPostCapture;
                        status = "Target regained foreground; capturing the next 5 seconds.";
                    }
                    else if (State == DiagnosticSessionState.FocusGainedPostCapture)
                    {
                        _postCaptureDeadlineQpc = null;
                        State = DiagnosticSessionState.Background;
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
        var jsonlPath = Path.Combine(_sessionsDirectory, $"windowtrace-session-{stamp}.jsonl");
        var metadataPath = Path.Combine(_sessionsDirectory, $"windowtrace-session-{stamp}.session.json");

        SessionMetadata.WriteAsync(metadataPath, sessionId, startedUtc, _clock.Frequency)
            .GetAwaiter().GetResult();

        _writer = new JsonlEventWriter(jsonlPath);
        _sessionId = sessionId;
        _sessionStartQpc = focusLostQpc;
        _postCaptureDeadlineQpc = null;
        State = DiagnosticSessionState.FocusLost;

        var preRollStart = focusLostQpc - (long)(_clock.Frequency * 3d);
        foreach (var priorEvent in _buffer.Snapshot().Where(e => e.QpcTimestamp >= preRollStart))
            _writer.TryWrite(priorEvent);
    }

    public async Task CheckPostCaptureDeadlineAsync()
    {
        JsonlEventWriter? completedWriter = null;
        lock (_gate)
        {
            if (_writer is null || _postCaptureDeadlineQpc is not long deadline)
                return;

            if (_clock.GetTimestamp() < deadline)
                return;

            completedWriter = _writer;
            _writer = null;
            _postCaptureDeadlineQpc = null;
            State = DiagnosticSessionState.Completed;
        }

        await completedWriter.DisposeAsync();
        StatusChanged?.Invoke($"Diagnostic session completed: {completedWriter.FilePath}. Queue drops: {completedWriter.DroppedRecords}.");
    }

    public async Task StopAsync()
    {
        JsonlEventWriter? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
            _postCaptureDeadlineQpc = null;
            if (writer is not null)
                State = DiagnosticSessionState.Completed;
        }

        if (writer is not null)
        {
            await writer.DisposeAsync();
            StatusChanged?.Invoke($"Diagnostic session stopped and saved: {writer.FilePath}. Queue drops: {writer.DroppedRecords}.");
        }
    }
}
