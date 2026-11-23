using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using WindowTrace.Core;

namespace WindowTrace.Storage;

/// <summary>
/// Generates a factual Markdown summary from a completed JSONL session.
/// It reports observations and timing relationships, not a root cause.
/// </summary>
public static class SessionReportGenerator
{
    private const int MaximumDetailRows = 20;

    public static async Task<string> WriteAsync(string jsonlPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(jsonlPath))
            throw new FileNotFoundException("Diagnostic JSONL file was not found.", jsonlPath);

        var focusEvents = new List<DiagnosticEvent>();
        var renderingEvents = new List<DiagnosticEvent>();
        var cpuEvents = new List<DiagnosticEvent>();
        var gpuEvents = new List<DiagnosticEvent>();
        var markerEvents = new List<DiagnosticEvent>();
        var etwCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long? firstQpc = null;
        long? lastQpc = null;
        var totalEvents = 0;

        await using (var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNumber++;
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                DiagnosticEvent? item;
                try { item = JsonSerializer.Deserialize<DiagnosticEvent>(line); }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"Malformed JSONL record at line {lineNumber}; report generation stopped.", ex);
                }

                if (item is null)
                    throw new InvalidDataException($"JSONL record at line {lineNumber} was null.");

                totalEvents++;
                firstQpc = firstQpc is null ? item.QpcTimestamp : Math.Min(firstQpc.Value, item.QpcTimestamp);
                lastQpc = lastQpc is null ? item.QpcTimestamp : Math.Max(lastQpc.Value, item.QpcTimestamp);

                AddRecent(focusEvents, item, IsFocusEvent(item.EventType));
                AddRecent(renderingEvents, item, item.EventType == "RenderingSummary");
                AddRecent(cpuEvents, item, item.EventType == "CpuSample");
                AddRecent(gpuEvents, item, item.EventType == "GpuPdhSample");
                AddRecent(markerEvents, item, item.EventType is "InputTestMarker" or "TargetSelected" or "PresentMonCapability" or "EtwProviderCapability");

                if (item.Source is not ("WindowTrace" or "PresentMonProcess" or "PresentMonMetrics" or "WinEvent" or "WindowPoll" or "PDH" or "ETW" or "GetSystemTimes+Process.TotalProcessorTime"))
                {
                    var key = $"{item.Source} / {item.EventType}";
                    etwCounts[key] = etwCounts.GetValueOrDefault(key) + 1;
                }

                sourceCounts[item.Source] = sourceCounts.GetValueOrDefault(item.Source) + 1;
            }
        }

        var metadataPath = Path.ChangeExtension(jsonlPath, ".session.json");
        var qualityPath = Path.ChangeExtension(jsonlPath, ".quality.json");
        SessionMetadata? metadata = null;
        SessionQualityReport? quality = null;

        if (File.Exists(metadataPath))
        {
            await using var input = File.OpenRead(metadataPath);
            metadata = await JsonSerializer.DeserializeAsync<SessionMetadata>(input, cancellationToken: cancellationToken);
        }

        if (File.Exists(qualityPath))
        {
            await using var input = File.OpenRead(qualityPath);
            quality = await JsonSerializer.DeserializeAsync<SessionQualityReport>(input, cancellationToken: cancellationToken);
        }

        var report = new StringBuilder();
        report.AppendLine("# WindowTrace diagnostic report");
        report.AppendLine();
        report.AppendLine("> This report summarizes observed events and timing relationships. It does not establish causation or identify a root cause by itself.");
        report.AppendLine();

        report.AppendLine("## Session and environment");
        report.AppendLine();
        var sessionIdText = metadata is null ? "Unavailable" : metadata.SessionId.ToString();
        var targetName = metadata?.TargetProcessName ?? quality?.TargetProcessName ?? "Unavailable";
        var targetPid = metadata?.TargetProcessId?.ToString(CultureInfo.InvariantCulture) ?? quality?.TargetProcessId?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";
        var startedUtc = metadata is null ? "Unavailable" : metadata.StartedUtc.ToString("O", CultureInfo.InvariantCulture);
        var buildNumber = metadata is null ? "Unavailable" : metadata.WindowsBuildNumber ?? metadata.OsBuild.ToString(CultureInfo.InvariantCulture);
        var revision = metadata?.WindowsUpdateRevision?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var logicalProcessors = metadata is null ? "Unavailable" : metadata.LogicalProcessorCount.ToString(CultureInfo.InvariantCulture);
        var qpcFrequency = metadata is null ? "Unavailable" : metadata.QpcFrequency.ToString("N0", CultureInfo.InvariantCulture);

        report.AppendLine($"- Session ID: {sessionIdText}");
        report.AppendLine($"- Target: {targetName} (PID {targetPid})");
        report.AppendLine($"- Started (UTC): {startedUtc}");
        report.AppendLine($"- OS: {metadata?.WindowsDisplayVersion ?? "Unknown"} / Build {buildNumber}.{revision}");
        report.AppendLine($"- CPU: {metadata?.CpuModel ?? "Unavailable"}; logical processors: {logicalProcessors}");
        report.AppendLine($"- Installed memory: {FormatBytes(metadata?.InstalledPhysicalMemoryBytes)}");
        report.AppendLine($"- BIOS / system: {metadata?.BiosVersion ?? "Unavailable"} / {metadata?.SystemManufacturer ?? "Unavailable"} {metadata?.SystemProductName ?? string.Empty}");
        report.AppendLine($"- QPC frequency: {qpcFrequency}");
        report.AppendLine();

        report.AppendLine("### Display adapters and drivers");
        report.AppendLine();
        if (metadata is not null && metadata.DisplayAdapters.Count > 0)
        {
            report.AppendLine("| Adapter | Driver version | Driver date | Provider |");
            report.AppendLine("|---|---|---|---|");
            foreach (var adapter in metadata.DisplayAdapters)
                report.AppendLine($"| {EscapeMarkdown(adapter.Name ?? adapter.RegistryKey)} | {EscapeMarkdown(adapter.DriverVersion ?? "Unavailable")} | {EscapeMarkdown(adapter.DriverDate ?? "Unavailable")} | {EscapeMarkdown(adapter.Provider ?? "Unavailable")} |");
        }
        else
        {
            report.AppendLine("No display-adapter registry metadata was available.");
        }
        report.AppendLine();

        report.AppendLine("## Capture quality");
        report.AppendLine();
        report.AppendLine($"- Total events: {totalEvents}");
        var timeSpan = firstQpc is long first && lastQpc is long last && metadata is not null && metadata.QpcFrequency > 0
            ? ((double)(last - first) * 1000d / metadata.QpcFrequency).ToString("F3", CultureInfo.InvariantCulture) + " ms"
            : "Unavailable";
        report.AppendLine($"- Event time span: {timeSpan}");
        report.AppendLine($"- Pre-roll requested / observed: {quality?.RequestedPreRollMilliseconds.ToString("F0", CultureInfo.InvariantCulture) ?? "Unavailable"} / {quality?.ActualPreRollMilliseconds.ToString("F3", CultureInfo.InvariantCulture) ?? "Unavailable"} ms");
        report.AppendLine($"- Buffer evictions (start / end): {quality?.BufferEvictionsAtSessionStart.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"} / {quality?.BufferEvictionsAtFinalization.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- ETW events lost: {quality?.EtwEventsLostAtFinalization?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- Writer queue drops: {quality?.WriterQueueDroppedRecords.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- File size limit reached: {quality?.FileSizeLimitReached.ToString() ?? "Unavailable"}");
        report.AppendLine($"- Writer error: {quality?.WriterError ?? "None reported"}");
        report.AppendLine();

        AppendEventTable(report, "Focus and window transitions", focusEvents);
        AppendEventTable(report, "Input markers and collector capabilities", markerEvents);
        AppendEventTable(report, "Rendering summaries", renderingEvents);
        AppendEventTable(report, "CPU samples", cpuEvents);
        AppendEventTable(report, "GPU samples", gpuEvents);

        report.AppendLine("## ETW event volume");
        report.AppendLine();
        if (etwCounts.Count == 0)
            report.AppendLine("No ETW events were present in this session.");
        else
        {
            report.AppendLine("| Provider / event | Count |");
            report.AppendLine("|---|---:|");
            foreach (var entry in etwCounts.OrderByDescending(x => x.Value).Take(30))
                report.AppendLine($"| {EscapeMarkdown(entry.Key)} | {entry.Value} |");
        }
        report.AppendLine();

        report.AppendLine("## Data-source volume");
        report.AppendLine();
        report.AppendLine("| Source | Count |");
        report.AppendLine("|---|---:|");
        foreach (var entry in sourceCounts.OrderByDescending(x => x.Value))
            report.AppendLine($"| {EscapeMarkdown(entry.Key)} | {entry.Value} |");
        report.AppendLine();

        report.AppendLine("## Interpretation limits");
        report.AppendLine();
        report.AppendLine("- Foreground-window changes and GUI focus metadata do not prove that a game's input thread processed a key.");
        report.AppendLine("- Win32k event payloads are omitted by privacy policy; only event timing and metadata are retained.");
        report.AppendLine("- PresentMon frame metrics describe presentation/rendering behavior, not the exact time a game handled a keyboard input.");
        report.AppendLine("- Missing counters or provider events are unavailable data, not zero activity.");
        report.AppendLine("- Correlation between a CPU/GPU/ETW event and a delayed frame is not proof that the event caused the delay.");
        report.AppendLine();

        var reportPath = Path.ChangeExtension(jsonlPath, ".report.md");
        await File.WriteAllTextAsync(reportPath, report.ToString(), new UTF8Encoding(false), cancellationToken);
        return reportPath;
    }

    private static bool IsFocusEvent(string eventType)
        => eventType is "FocusLost" or "FocusGained" or "FocusLostAgain" or
            "DiagnosticSessionStarted" or "DiagnosticSessionCompleted" or "DiagnosticSessionStopped" or
            "ForegroundChanged" or "ForegroundSnapshot" or "ObjectFocusChanged" or
            "WindowMinimizeStarted" or "WindowRestored";

    private static void AddRecent(List<DiagnosticEvent> list, DiagnosticEvent item, bool condition)
    {
        if (!condition)
            return;

        list.Add(item);
        if (list.Count > MaximumDetailRows)
            list.RemoveAt(0);
    }

    private static void AppendEventTable(StringBuilder report, string heading, IReadOnlyList<DiagnosticEvent> events)
    {
        report.AppendLine($"## {heading}");
        report.AppendLine();
        if (events.Count == 0)
        {
            report.AppendLine("No events recorded.");
            report.AppendLine();
            return;
        }

        report.AppendLine("| Relative time (ms) | Event | PID | Process | Details |");
        report.AppendLine("|---:|---|---:|---|---|");
        foreach (var item in events)
        {
            report.AppendLine($"| {item.RelativeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} | {EscapeMarkdown(item.EventType)} | {item.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—"} | {EscapeMarkdown(item.ProcessName ?? "—")} | {EscapeMarkdown(Truncate(item.Details ?? "—", 360))} |");
        }
        report.AppendLine();
    }

    private static string FormatBytes(ulong? bytes)
        => bytes is ulong value ? $"{value / (1024d * 1024d * 1024d):F2} GiB" : "Unavailable";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string EscapeMarkdown(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
