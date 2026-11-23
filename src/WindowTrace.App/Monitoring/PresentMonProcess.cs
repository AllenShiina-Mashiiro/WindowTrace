using System.Diagnostics;
using System.Globalization;
using WindowTrace.Core;

namespace WindowTrace.Monitoring;

/// <summary>
/// Launches the official PresentMon console application as a child process.
/// Input tracking is explicitly disabled; raw frame CSV fields are preserved.
/// </summary>
public sealed class PresentMonProcess : IAsyncDisposable
{
    private readonly QpcClock _clock;
    private Process? _process;
    private string[]? _headers;
    private bool _disposed;

    public PresentMonProcess(QpcClock clock) => _clock = clock;

    public event Action<DiagnosticEvent>? FrameReceived;
    public event Action<string>? StatusChanged;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string executablePath, uint processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            throw new InvalidOperationException("PresentMon is already running.");
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("PresentMon executable was not found.", executablePath);
        if (processId == 0)
            throw new ArgumentOutOfRangeException(nameof(processId));

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("--process_id");
        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--output_stdout");
        startInfo.ArgumentList.Add("--qpc_time");
        startInfo.ArgumentList.Add("--no_console_stats");
        startInfo.ArgumentList.Add("--no_track_input");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                StatusChanged?.Invoke($"PresentMon: {args.Data}");
        };
        process.Exited += (_, _) =>
        {
            StatusChanged?.Invoke($"PresentMon exited with code {SafeExitCode(process)}.");
        };

        if (!process.Start())
            throw new InvalidOperationException("PresentMon process could not be started.");

        _headers = null;
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        StatusChanged?.Invoke($"PresentMon started for PID {processId}.");
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
            return;

        var line = args.Data.TrimStart('\uFEFF');
        if (_headers is null)
        {
            var candidate = ParseCsv(line);
            if (candidate.Length > 1 && candidate.Any(h => h.Contains("Time", StringComparison.OrdinalIgnoreCase)))
            {
                _headers = candidate;
                StatusChanged?.Invoke($"PresentMon CSV stream detected ({candidate.Length} columns).");
            }
            return;
        }

        try
        {
            var values = ParseCsv(line);
            if (values.Length != _headers.Length)
                return;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _headers.Length; i++)
                fields[_headers[i]] = values[i];

            var receiptQpc = _clock.GetTimestamp();
            var eventQpc = receiptQpc;
            var timestampQuality = "ConsumerReceiptQpc";

            if (TryGetQpc(fields, out var frameQpc))
            {
                eventQpc = frameQpc;
                timestampQuality = "PresentMonQpc";
            }

            var rawCsv = string.Join(",", _headers.Select(EscapeCsvValue));
            var payload = string.Join(",", values.Select(EscapeCsvValue));
            var details = $"TimestampQuality={timestampQuality};ReceiptQpc={receiptQpc};Header={rawCsv};Row={payload}";

            FrameReceived?.Invoke(new DiagnosticEvent(
                Guid.NewGuid(),
                "PresentMonFrame",
                "PresentMonProcess",
                eventQpc,
                _clock.Frequency,
                _clock.ToUtc(eventQpc),
                _clock.ElapsedMilliseconds(_clock.AnchorTimestamp, eventQpc),
                null,
                null,
                null,
                null,
                null,
                details));
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"PresentMon CSV parse warning: {ex.Message}");
        }
    }

    private static bool TryGetQpc(
        IReadOnlyDictionary<string, string> fields,
        out long qpc)
    {
        foreach (var key in new[] { "CPUStartTime", "CPUStartTimeQPC", "CPUStartTimeQpc" })
        {
            if (fields.TryGetValue(key, out var value) &&
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out qpc) &&
                qpc > 0)
                return true;
        }

        qpc = 0;
        return false;
    }

    private static string[] ParseCsv(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString());
        return result.ToArray();
    }

    private static string EscapeCsvValue(string value)
        => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        var process = _process;
        _process = null;
        if (process is null)
            return;

        process.OutputDataReceived -= OnOutput;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"PresentMon stop warning: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }
}
