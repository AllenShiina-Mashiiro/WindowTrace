using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowTrace.Core;

/// <summary>
/// Provides a monotonic QPC-based clock. A precise Windows UTC sample is
/// paired with the midpoint of two QPC reads to establish the initial mapping.
/// </summary>
public sealed class QpcClock
{
    private readonly long _anchorQpc;
    private readonly DateTimeOffset _anchorUtc;

    public QpcClock()
    {
        Frequency = Stopwatch.Frequency;

        var before = Stopwatch.GetTimestamp();
        try
        {
            GetSystemTimePreciseAsFileTime(out var fileTime);
            var after = Stopwatch.GetTimestamp();
            _anchorQpc = before + ((after - before) / 2);

            var fileTimeValue = ((long)fileTime.High << 32) | fileTime.Low;
            _anchorUtc = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTimeValue));
        }
        catch (EntryPointNotFoundException)
        {
            _anchorQpc = Stopwatch.GetTimestamp();
            _anchorUtc = DateTimeOffset.UtcNow;
        }
        catch (DllNotFoundException)
        {
            _anchorQpc = Stopwatch.GetTimestamp();
            _anchorUtc = DateTimeOffset.UtcNow;
        }
    }

    public long Frequency { get; }
    public long AnchorTimestamp => _anchorQpc;
    public DateTimeOffset AnchorUtc => _anchorUtc;

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public DateTimeOffset ToUtc(long qpcTimestamp)
    {
        var elapsedTicks = qpcTimestamp - _anchorQpc;
        var elapsedSeconds = (double)elapsedTicks / Frequency;
        return _anchorUtc.AddSeconds(elapsedSeconds);
    }

    public double ElapsedMilliseconds(long startQpc, long endQpc)
        => (double)(endQpc - startQpc) * 1000d / Frequency;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetSystemTimePreciseAsFileTime")]
    private static extern void GetSystemTimePreciseAsFileTime(out NativeFileTime fileTime);
}
