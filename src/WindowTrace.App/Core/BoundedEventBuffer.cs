namespace WindowTrace.Core;

/// <summary>
/// Thread-safe bounded history buffer. Oldest events are evicted first.
/// Evictions are counted so a pre-roll shortfall is visible in session metadata.
/// </summary>
public sealed class BoundedEventBuffer
{
    private readonly object _gate = new();
    private readonly Queue<DiagnosticEvent> _events = new();
    private long _droppedEvents;

    public BoundedEventBuffer(int capacity = 4096)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity = capacity;
    }

    public int Capacity { get; }
    public long DroppedEvents { get { lock (_gate) return _droppedEvents; } }

    public void Add(DiagnosticEvent item)
    {
        lock (_gate)
        {
            while (_events.Count >= Capacity)
            {
                _events.Dequeue();
                _droppedEvents++;
            }

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
