using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowTrace.Core;

namespace WindowTrace.Monitoring;

/// <summary>
/// Low-frequency CPU sampler. Process CPU is normalized to total logical
/// machine capacity; system CPU is calculated from GetSystemTimes.
/// Thread scheduling and wait states remain ETW-derived, not guessed here.
/// </summary>
public sealed class CpuSampler : IAsyncDisposable
{
    private readonly QpcClock _clock;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public CpuSampler(QpcClock clock) => _clock = clock;

    public event Action<DiagnosticEvent>? SampleReceived;
    public event Action<string>? Warning;

    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start(uint targetProcessId, TimeSpan? interval = null)
    {
        if (IsRunning)
            throw new InvalidOperationException("CPU sampler is already running.");

        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => SampleLoopAsync(
            targetProcessId,
            interval ?? TimeSpan.FromSeconds(1),
            _cancellation.Token));
    }

    private async Task SampleLoopAsync(uint processId, TimeSpan interval, CancellationToken token)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById((int)processId);
            var previousProcessTime = process.TotalProcessorTime;
            var previousQpc = _clock.GetTimestamp();
            var previousSystem = ReadSystemTimes();

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                var currentQpc = _clock.GetTimestamp();
                var currentSystem = ReadSystemTimes();
                var elapsedSeconds = (double)(currentQpc - previousQpc) / _clock.Frequency;
                if (elapsedSeconds <= 0)
                    continue;

                var systemCpu = CalculateSystemCpu(previousSystem, currentSystem);
                var currentProcessTime = process.TotalProcessorTime;
                var processCpu = (currentProcessTime - previousProcessTime).TotalSeconds /
                                 elapsedSeconds / Environment.ProcessorCount * 100d;

                var details = $"SystemCpuPercent={systemCpu:F2};ProcessCpuPercent={Math.Clamp(processCpu, 0, 100):F2};LogicalProcessors={Environment.ProcessorCount};SampleIntervalMs={elapsedSeconds * 1000:F1}";
                SampleReceived?.Invoke(new DiagnosticEvent(
                    Guid.NewGuid(),
                    "CpuSample",
                    "GetSystemTimes+Process.TotalProcessorTime",
                    currentQpc,
                    _clock.Frequency,
                    _clock.ToUtc(currentQpc),
                    _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, currentQpc),
                    processId,
                    null,
                    null,
                    process.ProcessName,
                    null,
                    details));

                previousProcessTime = currentProcessTime;
                previousQpc = currentQpc;
                previousSystem = currentSystem;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"CPU sampling stopped: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static double CalculateSystemCpu(SystemTimes previous, SystemTimes current)
    {
        var idle = current.Idle - previous.Idle;
        var kernel = current.Kernel - previous.Kernel;
        var user = current.User - previous.User;
        var total = kernel + user;
        if (total == 0 || idle > total)
            return double.NaN;

        return Math.Clamp((1d - (double)idle / total) * 100d, 0d, 100d);
    }

    private static SystemTimes ReadSystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new InvalidOperationException($"GetSystemTimes failed: {Marshal.GetLastWin32Error()}.");

        return new SystemTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static ulong ToUInt64(FileTime value)
        => ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null)
            return;

        _cancellation.Cancel();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { Warning?.Invoke("CPU sampler did not stop within 3 seconds."); }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _worker = null;
    }

    private readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime lpIdleTime,
        out FileTime lpKernelTime,
        out FileTime lpUserTime);
}
