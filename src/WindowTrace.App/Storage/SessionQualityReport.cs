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
    long WriterQueueDroppedRecords,
    long FileBytesWritten,
    long FileMaximumBytes,
    bool FileSizeLimitReached,
    string? WriterError)
{
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
