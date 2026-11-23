namespace WindowTrace.Core;

/// <summary>
/// Thread-safe bounded history buffer. Oldest events are evicted first.
/// This is an application-level history buffer, not an ETW session buffer.
/// </summary>
public sealed class BoundedEventBuffer
{
    private readonly object _gate = new();
    private readonly Queue<DiagnosticEvent> _events = new();

    public BoundedEventBuffer(int capacity = 4096)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity = capacity;
    }

    public int Capacity { get; }

    public void Add(DiagnosticEvent item)
    {
        lock (_gate)
        {
            while (_events.Count >= Capacity)
                _events.Dequeue();

            _events.Enqueue(item);
        }
    }

    public IReadOnlyList<DiagnosticEvent> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _events.Clear();
    }
}
