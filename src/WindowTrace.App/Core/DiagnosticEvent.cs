namespace WindowTrace.Core;

public sealed record DiagnosticEvent(
    Guid EventId,
    string EventType,
    string Source,
    long QpcTimestamp,
    long QpcFrequency,
    DateTimeOffset UtcTimestamp,
    double RelativeMilliseconds,
    uint? ProcessId,
    uint? ThreadId,
    ulong? WindowHandle,
    string? ProcessName,
    string? WindowTitle,
    string? Details,
    Guid? SessionId = null);

public sealed record WindowSnapshot(
    long QpcTimestamp,
    DateTimeOffset UtcTimestamp,
    nint Handle,
    uint ProcessId,
    uint ThreadId,
    string ProcessName,
    string WindowTitle,
    bool IsMinimized,
    string EventType = "ForegroundChanged",
    int ObjectId = 0,
    int ChildId = 0,
    nint? FocusedWindowHandle = null,
    uint? GuiThreadFlags = null,
    double? FocusQueryDelayMs = null);
