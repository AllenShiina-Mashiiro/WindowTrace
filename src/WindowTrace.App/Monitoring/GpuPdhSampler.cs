using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using WindowTrace.Core;

namespace WindowTrace.Monitoring;

/// <summary>
/// Samples documented Windows GPU performance counters through PDH.
/// Missing counters remain unavailable; the sampler never substitutes zero.
/// </summary>
public sealed class GpuPdhSampler : IAsyncDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhValidData = 0;
    private const uint PdhNewData = 1;

    private readonly QpcClock _clock;
    private readonly List<CounterHandle> _counters = [];
    private IntPtr _query;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private uint _targetPid;

    private static readonly (string Key, string Path, bool ProcessScoped)[] CounterDefinitions =
    [
        ("GpuEngine", @"\GPU Engine(*)\Utilization Percentage", true),
        ("GpuProcessLocalMemory", @"\GPU Process Memory(*)\Local Usage", true),
        ("GpuProcessNonLocalMemory", @"\GPU Process Memory(*)\Non Local Usage", true),
        ("GpuAdapterDedicatedMemory", @"\GPU Adapter Memory(*)\Dedicated Usage", false),
        ("GpuAdapterSharedMemory", @"\GPU Adapter Memory(*)\Shared Usage", false)
    ];

    public GpuPdhSampler(QpcClock clock) => _clock = clock;

    public event Action<DiagnosticEvent>? SampleReceived;
    public event Action<string>? Warning;

    public IReadOnlyList<string> AvailableCounters => _counters.Select(c => c.Key).ToArray();
    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start(uint targetProcessId, TimeSpan? interval = null)
    {
        if (IsRunning)
            throw new InvalidOperationException("GPU sampler is already running.");

        _targetPid = targetProcessId;
        var status = PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (status != 0)
            throw new InvalidOperationException($"PDH query creation failed: 0x{status:X8}.");

        foreach (var definition in CounterDefinitions)
        {
            var addStatus = PdhAddEnglishCounter(_query, definition.Path, IntPtr.Zero, out var counter);
            if (addStatus == 0)
                _counters.Add(new CounterHandle(definition.Key, counter, definition.ProcessScoped));
            else
                Warning?.Invoke($"GPU counter unavailable: {definition.Path} (PDH 0x{addStatus:X8}).");
        }

        if (_counters.Count == 0)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            throw new InvalidOperationException("No supported GPU PDH counters were found on this system.");
        }

        _ = PdhCollectQueryData(_query);
        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => SampleLoopAsync(interval ?? TimeSpan.FromSeconds(1), _cancellation.Token));
    }

    private async Task SampleLoopAsync(TimeSpan interval, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                var status = PdhCollectQueryData(_query);
                if (status != 0)
                {
                    Warning?.Invoke($"GPU counter collection failed: PDH 0x{status:X8}.");
                    continue;
                }

                var fields = new List<string>();
                foreach (var counter in _counters)
                {
                    foreach (var item in ReadCounterArray(counter.Handle))
                    {
                        if (counter.ProcessScoped &&
                            !item.Name.Contains($"pid_{_targetPid}_", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var unit = counter.Key == "GpuEngine" ? "percent" : "bytes";
                        fields.Add($"{counter.Key}[{item.Name}]={item.Value.ToString("R", CultureInfo.InvariantCulture)} {unit}");
                    }
                }

                var qpc = _clock.GetTimestamp();
                SampleReceived?.Invoke(new DiagnosticEvent(
                    Guid.NewGuid(),
                    "GpuPdhSample",
                    "PDH",
                    qpc,
                    _clock.Frequency,
                    _clock.ToUtc(qpc),
                    _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, qpc),
                    _targetPid,
                    null,
                    null,
                    null,
                    null,
                    fields.Count == 0 ? "No GPU counter instances returned valid data." : string.Join(";", fields)));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"GPU sampler stopped: {ex.Message}");
        }
    }

    private static IReadOnlyList<CounterValue> ReadCounterArray(IntPtr counter)
    {
        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
            return [];

        var buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
        try
        {
            status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer);
            if (status != 0 || itemCount == 0)
                return [];

            var size = Marshal.SizeOf<PdhFormattedCounterValueItem>();
            var values = new List<CounterValue>(checked((int)itemCount));
            for (var i = 0; i < itemCount; i++)
            {
                var ptr = IntPtr.Add(buffer, checked((int)i * size));
                var item = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(ptr);
                if (item.Value.CStatus is not (PdhValidData or PdhNewData))
                    continue;

                var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                values.Add(new CounterValue(name, item.Value.DoubleValue));
            }

            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            if (_worker is not null)
            {
                try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { Warning?.Invoke("GPU sampler did not stop within 3 seconds."); }
            }

            _cancellation.Dispose();
            _cancellation = null;
            _worker = null;
        }

        foreach (var counter in _counters)
            PdhRemoveCounter(counter.Handle);
        _counters.Clear();

        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    private sealed record CounterHandle(string Key, IntPtr Handle, bool ProcessScoped);
    private sealed record CounterValue(string Name, double Value);

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValueItem
    {
        public IntPtr Name;
        public PdhFormattedCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll", EntryPoint = "PdhRemoveCounter")]
    private static extern uint PdhRemoveCounter(IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
