using System.Text.Json;
using System.Threading.Channels;
using WindowTrace.Core;

namespace WindowTrace.Storage;

/// <summary>
/// Bounded asynchronous JSONL writer. Records are flushed in batches to reduce
/// per-event disk overhead. Queue overflow is counted and exposed to the report.
/// </summary>
public sealed class JsonlEventWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly Channel<DiagnosticEvent> _channel;
    private readonly Task _worker;
    private readonly string _path;
    private long _droppedRecords;
    private Exception? _lastError;

    public JsonlEventWriter(string path, int queueCapacity = 32768)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _channel = Channel.CreateBounded<DiagnosticEvent>(
            new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        _worker = Task.Run(WriteLoopAsync);
    }

    public long DroppedRecords => Interlocked.Read(ref _droppedRecords);
    public Exception? LastError => _lastError;
    public string FilePath => _path;

    public bool TryWrite(DiagnosticEvent item)
    {
        if (_lastError is not null || !_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _droppedRecords);
            return false;
        }

        return true;
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream);

            var batch = new List<DiagnosticEvent>(256);
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 256 && _channel.Reader.TryRead(out var item))
                    batch.Add(item);

                foreach (var item in batch)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(item, JsonOptions)).ConfigureAwait(false);

                await writer.FlushAsync().ConfigureAwait(false);
            }

            if (batch.Count > 0)
                await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            _lastError ??= new TimeoutException("JSONL writer did not drain within 10 seconds.");
        }
    }
}
