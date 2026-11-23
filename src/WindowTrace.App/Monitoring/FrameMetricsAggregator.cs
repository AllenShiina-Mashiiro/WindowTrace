using System.Globalization;
using WindowTrace.Core;

namespace WindowTrace.Monitoring;

/// <summary>
/// Produces rolling rendering summaries from PresentMon 2.x frame rows.
/// Frame intervals are taken from PresentMon metrics when available; QPC deltas
/// are a fallback grouped by swap-chain address.
/// </summary>
public sealed class FrameMetricsAggregator
{
    private const int WindowSize = 120;
    private readonly QpcClock _clock;
    private readonly Queue<double> _frameTimesMs = new();
    private readonly Dictionary<string, long> _previousQpcBySwapChain = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSummaryQpc;

    public FrameMetricsAggregator(QpcClock clock) => _clock = clock;

    public DiagnosticEvent? AddFrame(long qpc, IReadOnlyDictionary<string, string> fields)
    {
        var frameTime = TryReadPositive(fields, "MsBetweenPresents");
        var frameTimeSource = "MsBetweenPresents";

        if (frameTime is null)
        {
            frameTime = TryReadPositive(fields, "MsBetweenDisplayChange");
            frameTimeSource = "MsBetweenDisplayChange";
        }

        if (frameTime is null && qpc > 0)
        {
            var swapChain = Read(fields, "SwapChainAddress") ?? "default";
            if (_previousQpcBySwapChain.TryGetValue(swapChain, out var previousQpc) && qpc > previousQpc)
            {
                frameTime = _clock.ElapsedMilliseconds(previousQpc, qpc);
                frameTimeSource = "SwapChainQpcDelta";
            }

            _previousQpcBySwapChain[swapChain] = qpc;
        }

        if (!frameTime.HasValue || frameTime.Value <= 0 || double.IsNaN(frameTime.Value) || double.IsInfinity(frameTime.Value))
            return null;

        _frameTimesMs.Enqueue(frameTime.Value);
        while (_frameTimesMs.Count > WindowSize)
            _frameTimesMs.Dequeue();

        if (_frameTimesMs.Count < 2)
            return null;

        if (_lastSummaryQpc != 0 && qpc - _lastSummaryQpc < _clock.Frequency)
            return null;

        _lastSummaryQpc = qpc;

        var values = _frameTimesMs.ToArray();
        var average = values.Average();
        var variance = values.Sum(value => Math.Pow(value - average, 2)) / values.Length;
        var ordered = values.OrderBy(value => value).ToArray();
        var p95Index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
        var processId = TryReadUInt(fields, "ProcessID") ?? TryReadUInt(fields, "ProcessId");
        var processName = Read(fields, "Application");
        var presentMode = Read(fields, "PresentMode");
        var gpuBusy = Read(fields, "MsGPUBusy");
        var gpuWait = Read(fields, "MsGPUWait");
        var dropped = Read(fields, "Dropped");

        var details =
            $"FrameTimeSource={frameTimeSource};SampleCount={values.Length};FPS={1000d / average:F2};" +
            $"AverageFrameTimeMs={average:F3};FrameTimeVarianceMs2={variance:F4};" +
            $"FrameTimeStdDevMs={Math.Sqrt(variance):F3};FrameTimeP95Ms={ordered[p95Index]:F3};" +
            $"FrameTimeMinMs={ordered[0]:F3};FrameTimeMaxMs={ordered[^1]:F3};" +
            $"CurrentGPUBusyMs={gpuBusy};CurrentGPUWaitMs={gpuWait};" +
            $"PresentMode={presentMode};Dropped={dropped}";

        return new DiagnosticEvent(
            Guid.NewGuid(),
            "RenderingSummary",
            "PresentMonMetrics",
            qpc,
            _clock.Frequency,
            _clock.ToUtc(qpc),
            _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
            processId,
            null,
            null,
            processName,
            null,
            details);
    }

    private static double? TryReadPositive(IReadOnlyDictionary<string, string> fields, string key)
    {
        var value = Read(fields, key);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static uint? TryReadUInt(IReadOnlyDictionary<string, string> fields, string key)
        => uint.TryParse(Read(fields, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? Read(IReadOnlyDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : null;
}
