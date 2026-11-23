using System.IO;
using System.Text.Json;

namespace WindowTrace.Storage;

public sealed record SessionQualityReport(
    Guid? SessionId,
    DateTimeOffset CompletedUtc,
    string CompletionReason,
    uint? TargetProcessId,
    string? TargetProcessName,
    long SessionStartQpc,
    long SessionEndQpc,
    double RequestedPreRollMilliseconds,
    double ActualPreRollMilliseconds,
    int PreRollEventCount,
    int EventBufferCapacity,
    long BufferEvictionsAtSessionStart,
    long BufferEvictionsAtFinalization,
    int? EtwEventsLostAtFinalization,
    long WriterQueueDroppedRecords,
    long FileBytesWritten,
    long FileMaximumBytes,
    bool FileSizeLimitReached,
    string? WriterError)
{
    public long TotalRecordsNotPersisted => Math.Max(WriterQueueDroppedRecords, QueueOverflowDroppedRecords + FileLimitDroppedRecords + WriterErrorDroppedRecords);
    public long QueueOverflowDroppedRecords { get; init; }
    public long FileLimitDroppedRecords { get; init; }
    public long WriterErrorDroppedRecords { get; init; }
    public long EtwTimestampFallbackCount { get; init; }
    public long PriorityQueueOverflowDroppedRecords { get; init; }
    public int PreRollEventsSelected { get; init; }
    public int PreRollEventsQueued { get; init; }
    public int PreRollEventsRejected { get; init; }
    public long PreRollEventsWritten { get; init; }

    public static async Task WriteAsync(string path, SessionQualityReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(
            stream,
            report,
            new JsonSerializerOptions { WriteIndented = true });
    }
}
