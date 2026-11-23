using System.Globalization;
using WindowTrace.Core;

namespace WindowTrace.Monitoring;

/// <summary>
/// Produces rolling rendering summaries from PresentMon 2.x frame rows.
/// Frame intervals are aggregated per swap chain so overlays or secondary
/// swap chains are not mixed into the game's primary frame-rate series.
/// </summary>
public sealed class FrameMetricsAggregator
{
    private const int WindowSize = 120;
    private const int MaximumSwapChains = 32;
    private readonly QpcClock _clock;
    private readonly Dictionary<string, SwapChainState> _swapChains = new(StringComparer.OrdinalIgnoreCase);

    public FrameMetricsAggregator(QpcClock clock) => _clock = clock;

    public DiagnosticEvent? AddFrame(long qpc, IReadOnlyDictionary<string, string> fields, bool qpcIsFrameTimestamp)
    {
        var swapChain = Read(fields, "SwapChainAddress") ?? "default";
        if (!_swapChains.TryGetValue(swapChain, out var state))
        {
            if (_swapChains.Count >= MaximumSwapChains)
                _swapChains.Remove(_swapChains.Keys.First());

            state = new SwapChainState();
            _swapChains[swapChain] = state;
        }

        var frameTime = TryReadPositive(fields, "MsBetweenPresents");
        var frameTimeSource = "MsBetweenPresents";

        if (frameTime is null)
        {
            frameTime = TryReadPositive(fields, "MsBetweenDisplayChange");
            frameTimeSource = "MsBetweenDisplayChange";
        }

        if (frameTime is null && qpcIsFrameTimestamp && qpc > 0)
        {
            if (state.PreviousQpc > 0 && qpc > state.PreviousQpc)
            {
                frameTime = _clock.ElapsedMilliseconds(state.PreviousQpc, qpc);
                frameTimeSource = "SwapChainQpcDelta";
            }
        }

        if (qpcIsFrameTimestamp && qpc > 0)
            state.PreviousQpc = qpc;

        if (!frameTime.HasValue || frameTime.Value <= 0 || double.IsNaN(frameTime.Value) || double.IsInfinity(frameTime.Value))
            return null;

        state.FrameTimesMs.Enqueue(frameTime.Value);
        while (state.FrameTimesMs.Count > WindowSize)
            state.FrameTimesMs.Dequeue();

        if (state.FrameTimesMs.Count < 2)
            return null;

        if (state.LastSummaryQpc != 0 && qpc - state.LastSummaryQpc < _clock.Frequency)
            return null;

        state.LastSummaryQpc = qpc;

        var values = state.FrameTimesMs.ToArray();
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
        var timestampQuality = qpcIsFrameTimestamp ? "PresentMonQpc" : "ConsumerReceiptQpc";

        var details =
            $"SwapChainAddress={swapChain};FrameTimeSource={frameTimeSource};SummaryTimestampQuality={timestampQuality};" +
            $"SampleCount={values.Length};FPS={1000d / average:F2};" +
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

    private sealed class SwapChainState
    {
        public Queue<double> FrameTimesMs { get; } = new();
        public long PreviousQpc { get; set; }
        public long LastSummaryQpc { get; set; }
    }
}
