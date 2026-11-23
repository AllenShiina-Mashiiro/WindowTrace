using System.Diagnostics;

namespace WindowTrace.Core;

/// <summary>
/// Provides a monotonic QPC-based clock. UTC is mapped from a UTC/QPC
/// anchor captured when the application clock is created.
/// </summary>
public sealed class QpcClock
{
    private readonly long _anchorQpc;
    private readonly DateTimeOffset _anchorUtc;

    public QpcClock()
    {
        Frequency = Stopwatch.Frequency;
        _anchorQpc = Stopwatch.GetTimestamp();
        _anchorUtc = DateTimeOffset.UtcNow;
    }

    public long Frequency { get; }
    public long AnchorTimestamp => _anchorQpc;

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public DateTimeOffset ToUtc(long qpcTimestamp)
    {
        var elapsedTicks = qpcTimestamp - _anchorQpc;
        var elapsedSeconds = (double)elapsedTicks / Frequency;
        return _anchorUtc.AddSeconds(elapsedSeconds);
    }

    public double ElapsedMilliseconds(long startQpc, long endQpc)
        => (double)(endQpc - startQpc) * 1000d / Frequency;
}
