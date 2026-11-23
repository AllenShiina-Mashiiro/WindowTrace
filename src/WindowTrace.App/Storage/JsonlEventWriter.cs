using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WindowTrace.Core;

namespace WindowTrace.Storage;

/// <summary>
/// Bounded asynchronous JSONL writer with a separately reserved critical-event
/// queue and file-byte reserve. High-volume telemetry cannot consume the capacity
/// needed for focus/session markers and collector health events.
/// </summary>
public sealed class JsonlEventWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly Channel<DiagnosticEvent> _priorityChannel;
    private readonly Channel<DiagnosticEvent> _normalChannel;
    private readonly Task _worker;
    private readonly string _path;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly long _criticalReserveBytes;
    private long _queueOverflowDroppedRecords;
    private long _priorityQueueOverflowDroppedRecords;
    private long _fileLimitDroppedRecords;
    private long _regularBudgetDroppedRecords;
    private long _hardFileLimitDroppedRecords;
    private long _writerErrorDroppedRecords;
    private long _preRollRecordsWritten;
    private long _bytesWritten;
    private int _regularLimitReached;
    private int _totalLimitReached;
    private Exception? _lastError;

    public JsonlEventWriter(
        string path,
        int queueCapacity = 32768,
        long maxBytes = 256L * 1024 * 1024,
        int priorityQueueCapacity = 4096,
        long criticalReserveBytes = 2L * 1024 * 1024)
    {
        if (maxBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Session size limit must be at least 1 MiB.");
        if (queueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (priorityQueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(priorityQueueCapacity));
        if (criticalReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(criticalReserveBytes));

        _path = path;
        MaxBytes = maxBytes;
        _criticalReserveBytes = Math.Min(criticalReserveBytes, Math.Max(64 * 1024, maxBytes / 8));
        _priorityChannel = Channel.CreateBounded<DiagnosticEvent>(
            new BoundedChannelOptions(priorityQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        _normalChannel = Channel.CreateBounded<DiagnosticEvent>(
            new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        _worker = Task.Run(WriteLoopAsync);
    }

    public long QueueOverflowDroppedRecords => Interlocked.Read(ref _queueOverflowDroppedRecords);
    public long PriorityQueueOverflowDroppedRecords => Interlocked.Read(ref _priorityQueueOverflowDroppedRecords);
    public long FileLimitDroppedRecords => Interlocked.Read(ref _fileLimitDroppedRecords);
    public long RegularBudgetDroppedRecords => Interlocked.Read(ref _regularBudgetDroppedRecords);
    public long HardFileLimitDroppedRecords => Interlocked.Read(ref _hardFileLimitDroppedRecords);
    public long WriterErrorDroppedRecords => Interlocked.Read(ref _writerErrorDroppedRecords);
    public long PreRollRecordsWritten => Interlocked.Read(ref _preRollRecordsWritten);
    public long DroppedRecords =>
        QueueOverflowDroppedRecords + FileLimitDroppedRecords + WriterErrorDroppedRecords;
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public long MaxBytes { get; }
    public long CriticalReserveBytes => _criticalReserveBytes;
    public bool LimitReached =>
        Volatile.Read(ref _regularLimitReached) != 0 || Volatile.Read(ref _totalLimitReached) != 0;
    public bool RegularFileBudgetReached => Volatile.Read(ref _regularLimitReached) != 0;
    public bool TotalFileLimitReached => Volatile.Read(ref _totalLimitReached) != 0;
    public Exception? LastError => _lastError;
    public string FilePath => _path;

    public bool TryWrite(DiagnosticEvent item)
    {
        if (_lastError is not null)
        {
            Interlocked.Increment(ref _writerErrorDroppedRecords);
            return false;
        }

        var isPriority = IsPriorityRecord(item);
        if (TotalFileLimitReached)
        {
            Interlocked.Increment(ref _fileLimitDroppedRecords);
            Interlocked.Increment(ref _hardFileLimitDroppedRecords);
            return false;
        }

        if (!isPriority && RegularFileBudgetReached)
        {
            Interlocked.Increment(ref _fileLimitDroppedRecords);
            Interlocked.Increment(ref _regularBudgetDroppedRecords);
            return false;
        }

        var channel = isPriority ? _priorityChannel : _normalChannel;
        if (!channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _queueOverflowDroppedRecords);
            if (isPriority)
                Interlocked.Increment(ref _priorityQueueOverflowDroppedRecords);
            return false;
        }

        _signal.Release();
        return true;
    }

    /// <summary>
    /// Back-pressured enqueue for background replay work such as pre-roll.
    /// Unlike TryWrite, this waits for queue capacity instead of discarding the
    /// record when the bounded queue is temporarily full.
    /// </summary>
    public async ValueTask<bool> WriteAsync(
        DiagnosticEvent item,
        CancellationToken cancellationToken = default)
    {
        if (_lastError is not null)
        {
            Interlocked.Increment(ref _writerErrorDroppedRecords);
            return false;
        }

        var isPriority = IsPriorityRecord(item);
        if (TotalFileLimitReached)
        {
            Interlocked.Increment(ref _fileLimitDroppedRecords);
            Interlocked.Increment(ref _hardFileLimitDroppedRecords);
            return false;
        }

        if (!isPriority && RegularFileBudgetReached)
        {
            Interlocked.Increment(ref _fileLimitDroppedRecords);
            Interlocked.Increment(ref _regularBudgetDroppedRecords);
            return false;
        }

        var channel = isPriority ? _priorityChannel : _normalChannel;
        try
        {
            await channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            _signal.Release();
            return true;
        }
        catch (ChannelClosedException)
        {
            Interlocked.Increment(ref _writerErrorDroppedRecords);
            return false;
        }
    }

    private static bool IsPriorityRecord(DiagnosticEvent item)
        => item.EventType is
            "DiagnosticSessionStarted" or
            "DiagnosticSessionCompleted" or
            "DiagnosticSessionStopped" or
            "PreRollCaptureSummary" or
            "FocusLost" or
            "FocusGained" or
            "FocusLostAgain" or
            "InputTestMarker" or
            "TargetSelected" or
            "EtwProviderCapability" or
            "PresentMonCapability" or
            "TargetThreadEnumeration" or
            "CollectorWarning" or
            "WindowMonitorWarning" or
            "WindowMonitorQueueDrops" or
            "EtwEventsLostCounter" or
            "ForegroundChanged" or
            "WindowMinimizeStarted" or
            "WindowRestored";

    private async Task WriteLoopAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await using var stream = new FileStream(
                _path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            var batch = new List<(DiagnosticEvent Item, bool IsPriority)>(256);
            while (true)
            {
                batch.Clear();

                // Always drain critical markers first. Normal telemetry is kept
                // below the reserved byte budget even if it arrived earlier.
                while (batch.Count < 256 && _priorityChannel.Reader.TryRead(out var priorityItem))
                    batch.Add((priorityItem, true));
                while (batch.Count < 256 && _normalChannel.Reader.TryRead(out var normalItem))
                    batch.Add((normalItem, false));

                if (batch.Count == 0)
                {
                    if (_priorityChannel.Reader.Completion.IsCompleted &&
                        _normalChannel.Reader.Completion.IsCompleted)
                        break;

                    await _signal.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                    continue;
                }

                long preRollRecordsInBatch = 0;
                foreach (var entry in batch)
                {
                    var json = JsonSerializer.Serialize(entry.Item, JsonOptions);
                    var lineBytes = Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                    var currentBytes = Interlocked.Read(ref _bytesWritten);

                    if (entry.IsPriority)
                    {
                        if (currentBytes + lineBytes > MaxBytes)
                        {
                            Interlocked.Exchange(ref _totalLimitReached, 1);
                            Interlocked.Increment(ref _fileLimitDroppedRecords);
                            Interlocked.Increment(ref _hardFileLimitDroppedRecords);
                            continue;
                        }
                    }
                    else if (Volatile.Read(ref _regularLimitReached) != 0 ||
                             currentBytes + lineBytes > MaxBytes - _criticalReserveBytes)
                    {
                        Interlocked.Exchange(ref _regularLimitReached, 1);
                        Interlocked.Increment(ref _fileLimitDroppedRecords);
                        Interlocked.Increment(ref _regularBudgetDroppedRecords);
                        continue;
                    }

                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesWritten, lineBytes);
                    if (entry.Item.CapturePhase == "PreRoll")
                        preRollRecordsInBatch++;
                }

                await writer.FlushAsync().ConfigureAwait(false);
                if (preRollRecordsInBatch > 0)
                    Interlocked.Add(ref _preRollRecordsWritten, preRollRecordsInBatch);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _lastError = ex;
            _priorityChannel.Writer.TryComplete(ex);
            _normalChannel.Writer.TryComplete(ex);
            while (_priorityChannel.Reader.TryRead(out _))
                Interlocked.Increment(ref _writerErrorDroppedRecords);
            while (_normalChannel.Reader.TryRead(out _))
                Interlocked.Increment(ref _writerErrorDroppedRecords);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _priorityChannel.Writer.TryComplete();
        _normalChannel.Writer.TryComplete();
        _signal.Release();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            _lastError ??= new TimeoutException("JSONL writer did not drain within 15 seconds.");
            _cancellation.Cancel();
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException)
            {
                _lastError ??= new TimeoutException("JSONL writer did not stop after cancellation.");
            }
            catch { }
        }
        finally
        {
            if (_worker.IsCompleted)
            {
                while (_priorityChannel.Reader.TryRead(out _))
                    Interlocked.Increment(ref _writerErrorDroppedRecords);
                while (_normalChannel.Reader.TryRead(out _))
                    Interlocked.Increment(ref _writerErrorDroppedRecords);
            }

            _cancellation.Dispose();
            _signal.Dispose();
        }
    }
}
