using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WindowTrace.Core;

namespace WindowTrace.Storage;

/// <summary>
/// Bounded asynchronous JSONL writer. Records are flushed in batches to reduce
/// per-event disk overhead. Both queue overflow and the per-session file-size
/// limit are counted and exposed to the quality report.
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
    private readonly CancellationTokenSource _cancellation = new();
    private long _droppedRecords;
    private long _bytesWritten;
    private int _limitReached;
    private Exception? _lastError;

    public JsonlEventWriter(
        string path,
        int queueCapacity = 32768,
        long maxBytes = 256L * 1024 * 1024)
    {
        if (maxBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Session size limit must be at least 1 MiB.");

        _path = path;
        MaxBytes = maxBytes;
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
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public long MaxBytes { get; }
    public bool LimitReached => Volatile.Read(ref _limitReached) != 0;
    public Exception? LastError => _lastError;
    public string FilePath => _path;

    public bool TryWrite(DiagnosticEvent item)
    {
        if (_lastError is not null || LimitReached || !_channel.Writer.TryWrite(item))
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
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            var batch = new List<DiagnosticEvent>(256);
            while (await _channel.Reader.WaitToReadAsync(_cancellation.Token).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 256 && _channel.Reader.TryRead(out var item))
                    batch.Add(item);

                foreach (var item in batch)
                {
                    if (LimitReached)
                    {
                        Interlocked.Increment(ref _droppedRecords);
                        continue;
                    }

                    var json = JsonSerializer.Serialize(item, JsonOptions);
                    var lineBytes = Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                    if (Interlocked.Read(ref _bytesWritten) + lineBytes > MaxBytes)
                    {
                        Interlocked.Exchange(ref _limitReached, 1);
                        Interlocked.Increment(ref _droppedRecords);
                        continue;
                    }

                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesWritten, lineBytes);
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _lastError = ex;
            while (_channel.Reader.TryRead(out _))
                Interlocked.Increment(ref _droppedRecords);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            _lastError ??= new TimeoutException("JSONL writer did not drain within 15 seconds.");
            _cancellation.Cancel();
            try { await _worker; } catch { }
        }
        finally
        {
            while (_channel.Reader.TryRead(out _))
                Interlocked.Increment(ref _droppedRecords);
            _cancellation.Dispose();
        }
    }
}
