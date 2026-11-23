using System.Text.Json;
using System.Threading.Channels;
using WindowTrace.Core;

namespace WindowTrace.Storage;

/// <summary>
/// Bounded asynchronous JSONL writer. If the bounded queue fills, the
/// dropped-record count is exposed instead of silently pretending the log is complete.
/// </summary>
public sealed class JsonlEventWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly Channel<DiagnosticEvent> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private readonly string _path;
    private long _droppedRecords;
    private Exception? _lastError;

    public JsonlEventWriter(string path, int queueCapacity = 4096)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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
        if (_channel.Writer.TryWrite(item))
            return true;

        Interlocked.Increment(ref _droppedRecords);
        return false;
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
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var writer = new StreamWriter(stream);

            await foreach (var item in _channel.Reader.ReadAllAsync(_cancellation.Token))
            {
                var json = JsonSerializer.Serialize(item, JsonOptions);
                await writer.WriteLineAsync(json);
                await writer.FlushAsync();
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
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
            await _worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _cancellation.Cancel();
            try { await _worker; } catch { }
        }

        _cancellation.Dispose();
    }
}
