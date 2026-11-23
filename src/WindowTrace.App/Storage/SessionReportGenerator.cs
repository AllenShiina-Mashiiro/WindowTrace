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
    public static async Task<string> WriteAsync(
        string jsonlPath,
        CancellationToken cancellationToken = default,
        int maximumPostFocusEtwRows = 300)
    {
        if (!File.Exists(jsonlPath))
            throw new FileNotFoundException("Diagnostic JSONL file was not found.", jsonlPath);
        if (maximumPostFocusEtwRows < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPostFocusEtwRows));

        var focusEvents = new List<DiagnosticEvent>();
        var renderingEvents = new List<DiagnosticEvent>();
        var cpuEvents = new List<DiagnosticEvent>();
        var gpuEvents = new List<DiagnosticEvent>();
        var markerEvents = new List<DiagnosticEvent>();
        var collectorEvents = new List<DiagnosticEvent>();
        DiagnosticEvent? latestFocusGained = null;
        var etwCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long? firstQpc = null;
        long? lastQpc = null;
        var totalEvents = 0;
        var targetThreadDiscoveryCount = 0;

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
                if (item.EventType == "TargetThreadDiscovered")
                    targetThreadDiscoveryCount++;
                firstQpc = firstQpc is null ? item.QpcTimestamp : Math.Min(firstQpc.Value, item.QpcTimestamp);
                lastQpc = lastQpc is null ? item.QpcTimestamp : Math.Max(lastQpc.Value, item.QpcTimestamp);

                AddRecent(focusEvents, item, IsFocusEvent(item.EventType));
                AddRecent(renderingEvents, item, item.EventType == "RenderingSummary");
                AddRecent(cpuEvents, item, item.EventType == "CpuSample");
                AddRecent(gpuEvents, item, item.EventType == "GpuPdhSample");
                AddRecent(markerEvents, item, item.EventType is "InputTestMarker" or "TargetSelected" or "PresentMonCapability" or "EtwProviderCapability" or "TargetThreadDiscovered" or "TargetThreadEnumeration" or "PreRollCaptureSummary");
                AddRecent(collectorEvents, item, item.EventType is "CollectorWarning" or "WindowMonitorWarning" or "WindowMonitorQueueDrops" or "EtwEventsLostCounter");

                if (item.EventType == "FocusGained" &&
                    (latestFocusGained is null || item.QpcTimestamp > latestFocusGained.QpcTimestamp))
                    latestFocusGained = item;

                if (IsEtwEvent(item))
                {
                    var key = $"{item.Source} / {item.EventType}";
                    etwCounts[key] = etwCounts.GetValueOrDefault(key) + 1;
                }

                sourceCounts[item.Source] = sourceCounts.GetValueOrDefault(item.Source) + 1;
            }
        }

        var postFocusEtwWindow = latestFocusGained is null
            ? PostFocusEtwWindow.Empty
            : await ReadPostFocusEtwWindowAsync(
                jsonlPath,
                latestFocusGained,
                maximumPostFocusEtwRows,
                cancellationToken);

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
        report.AppendLine($"- OS: {metadata?.WindowsDisplayVersion ?? "Unknown"} / {metadata?.WindowsEdition ?? "Unknown"} / Build {buildNumber}.{revision}");
        report.AppendLine($"- Runtime / architecture: {metadata?.Runtime ?? "Unavailable"} / {metadata?.ProcessArchitecture ?? "Unavailable"}");
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
        report.AppendLine($"- Pre-roll events selected / queued / written / rejected: {quality?.PreRollEventsSelected?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"} / {quality?.PreRollEventsQueued?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"} / {quality?.PreRollEventsWritten?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"} / {quality?.PreRollEventsRejected?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- Buffer evictions (start / end): {quality?.BufferEvictionsAtSessionStart.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"} / {quality?.BufferEvictionsAtFinalization.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- ETW events lost: {quality?.EtwEventsLostAtFinalization?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- ETW timestamps using collector-receipt fallback (collector lifetime): {quality?.EtwTimestampFallbackCount?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- ETW events intentionally suppressed by sampling policy (collector lifetime): {quality?.EtwThrottledEventCount?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- Records not persisted (total): {quality?.TotalRecordsNotPersisted.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"  - Queue overflow: {quality?.QueueOverflowDroppedRecords.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"    - Critical-event queue overflow (subset): {quality?.PriorityQueueOverflowDroppedRecords?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"  - Regular telemetry budget drops: {quality?.RegularBudgetDroppedRecords?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"  - Hard file-cap drops: {quality?.HardFileLimitDroppedRecords?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"  - File budget drops (combined): {quality?.FileLimitDroppedRecords.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"  - Writer errors: {quality?.WriterErrorDroppedRecords.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}");
        report.AppendLine($"- Regular telemetry byte budget reached: {quality?.RegularFileBudgetReached?.ToString() ?? "Unavailable"}");
        report.AppendLine($"- Hard file size limit reached: {quality?.FileSizeLimitReached.ToString() ?? "Unavailable"}");
        report.AppendLine($"- Writer error: {quality?.WriterError ?? "None reported"}");
        report.AppendLine();

        AppendEventTable(report, "Focus and window transitions", focusEvents);
        AppendEventTable(report, "Input markers, target threads and collector capabilities", markerEvents);
        AppendInputMarkerIntervals(report, markerEvents);
        AppendEventTable(report, "Collector health and data gaps", collectorEvents);
        AppendEventTable(report, "Rendering summaries", renderingEvents);
        AppendEventTable(report, "CPU samples", cpuEvents);
        AppendEventTable(report, "GPU samples", gpuEvents);
        AppendLatestPostFocusWindow(
            report, focusEvents, renderingEvents, cpuEvents, gpuEvents, markerEvents, collectorEvents,
            postFocusEtwWindow, targetThreadDiscoveryCount);

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
        report.AppendLine("- WinEvent QPC timestamps represent callback receipt time; the raw WinEventSystemTimeMs field is retained separately and is not converted to QPC.");
        report.AppendLine("- FocusLost/FocusGained markers inherit the WinEvent callback-receipt timestamp. Manual input markers use hotkey-message receipt time; neither is the time a game key was processed.");
        report.AppendLine("- Foreground-window changes and GUI focus metadata do not prove that a game's input thread processed a key.");
        report.AppendLine("- Win32k event payloads are omitted by privacy policy; only event timing and metadata are retained.");
        report.AppendLine("- Target-thread scheduling rows require a PID association from process thread enumeration, kernel thread lifecycle data or a matching event header. Unmapped threads are not assumed to belong to the game.");
        report.AppendLine("- NewThreadWaitTime and OldThreadState/WaitReason values are preserved as raw ETW fields; this report does not infer undocumented units or a definitive wait cause.");
        report.AppendLine("- PresentMon frame metrics describe presentation/rendering behavior, not the exact time a game handled a keyboard input.");
        report.AppendLine("- Missing counters or provider events are unavailable data, not zero activity.");
        report.AppendLine("- Correlation between a CPU/GPU/ETW event and a delayed frame is not proof that the event caused the delay.");
        report.AppendLine();

        var reportPath = Path.ChangeExtension(jsonlPath, ".report.md");
        await File.WriteAllTextAsync(reportPath, report.ToString(), new UTF8Encoding(false), cancellationToken);
        return reportPath;
    }

    private static void AppendPostFocusEvidenceSummary(
        StringBuilder report,
        DiagnosticEvent focusGained,
        IReadOnlyList<DiagnosticEvent> renderingEvents,
        IReadOnlyList<DiagnosticEvent> cpuEvents,
        IReadOnlyList<DiagnosticEvent> gpuEvents,
        IReadOnlyList<DiagnosticEvent> collectorEvents,
        PostFocusEtwWindow etwWindow,
        int targetThreadDiscoveryCount)
    {
        var endQpc = focusGained.QpcTimestamp + (long)(focusGained.QpcFrequency * 1.5d);
        var rendering = renderingEvents.Where(item => item.QpcTimestamp >= focusGained.QpcTimestamp && item.QpcTimestamp <= endQpc)
            .OrderBy(item => item.QpcTimestamp).FirstOrDefault();
        var cpu = cpuEvents.Where(item => item.QpcTimestamp >= focusGained.QpcTimestamp && item.QpcTimestamp <= endQpc)
            .OrderBy(item => item.QpcTimestamp).FirstOrDefault();
        var gpu = gpuEvents.Where(item => item.QpcTimestamp >= focusGained.QpcTimestamp && item.QpcTimestamp <= endQpc)
            .OrderBy(item => item.QpcTimestamp).FirstOrDefault();
        var warnings = collectorEvents.Count(item => item.QpcTimestamp >= focusGained.QpcTimestamp && item.QpcTimestamp <= endQpc);
        var summary = etwWindow.Summary;

        report.AppendLine("### Post-focus evidence summary");
        report.AppendLine();
        report.AppendLine($"- First rendering summary: {FormatOffset(rendering, focusGained)}; {(rendering is null ? "no frame summary was recorded in this interval" : Truncate(rendering.Details ?? "No details", 220))}");
        report.AppendLine($"- First target CPU sample: {FormatOffset(cpu, focusGained)}; {(cpu is null ? "no CPU sample was recorded in this interval" : Truncate(cpu.Details ?? "No details", 220))}");
        report.AppendLine($"- First GPU sample: {FormatOffset(gpu, focusGained)}; {(gpu is null ? "no GPU sample was recorded in this interval" : Truncate(gpu.Details ?? "No details", 220))}");
        report.AppendLine($"- ETW events in interval (all providers): {summary.TotalEvents}");
        report.AppendLine($"- Win32k ETW events in interval: {summary.Win32kEvents}");
        report.AppendLine($"- Win32k ETW events for target PID {focusGained.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}: {summary.Win32kTargetProcessEvents}; other/unknown PID: {summary.Win32kOtherProcessEvents}");
        report.AppendLine($"- System-wide DPC events in interval (NT Kernel Logger): {summary.SystemDpcEvents}");
        report.AppendLine($"- System-wide ISR events in interval (NT Kernel Logger): {summary.SystemIsrEvents}");
        report.AppendLine($"- GPU VSync DPC events in interval (DxgKrnl): {summary.GpuVsyncDpcEvents}");
        report.AppendLine($"- Target-thread ReadyThread events in interval: {summary.TargetReadyThreadEvents}");
        report.AppendLine($"- Target-thread ready-to-running delay from QPC (ms): {(summary.ReadyToRunSamples.Count == 0 ? "Unavailable" : $"n={summary.ReadyToRunSamples.Count}; avg={summary.ReadyToRunSamples.Average():F3}; max={summary.ReadyToRunSamples.Max():F3}")}");
        report.AppendLine($"- Target threads mapped during the session: {targetThreadDiscoveryCount}");
        report.AppendLine($"- Target-thread context switches in interval: {summary.TargetContextSwitches} (incoming: {summary.IncomingSwitches}; outgoing: {summary.OutgoingSwitches})");
        report.AppendLine($"- Target-thread incoming NewThreadWaitTime raw values: {(summary.IncomingWaitValues.Count == 0 ? "Unavailable" : string.Join(", ", summary.IncomingWaitValues))}");
        report.AppendLine($"- Target-thread outgoing OldThreadState raw values: {(summary.OutgoingStates.Count == 0 ? "Unavailable" : string.Join(", ", summary.OutgoingStates))}");
        report.AppendLine($"- Collector warnings/data-gap events in interval: {warnings}");
        report.AppendLine("- These observations help distinguish a presentation stall from a delayed game response, but do not prove which component caused the delay.");
        report.AppendLine();
    }

    private static string FormatOffset(DiagnosticEvent? item, DiagnosticEvent focusGained)
        => item is null
            ? "Unavailable"
            : $"{(item.QpcTimestamp - focusGained.QpcTimestamp) * 1000d / focusGained.QpcFrequency:F3} ms after FocusGained";

    private static void AppendInputMarkerIntervals(StringBuilder report, IReadOnlyList<DiagnosticEvent> markers)
    {
        report.AppendLine("## Manual input-response marker intervals");
        report.AppendLine();
        report.AppendLine("These intervals run from the first manual marker to the second marker after the game visibly responds. They include human timing and must not be interpreted as exact keyboard-processing latency.");
        report.AppendLine();

        var starts = new Dictionary<int, DiagnosticEvent>();
        var completed = new List<(int Pair, DiagnosticEvent Start, DiagnosticEvent Response)>();
        foreach (var marker in markers.OrderBy(item => item.QpcTimestamp))
        {
            var pairValue = GetDetailValue(marker.Details, "TestPair");
            var phase = GetDetailValue(marker.Details, "MarkerPhase");
            if (!int.TryParse(pairValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pair))
                continue;

            if (phase == "Start")
                starts[pair] = marker;
            else if (phase == "Response" && starts.Remove(pair, out var start))
                completed.Add((pair, start, marker));
        }

        if (completed.Count == 0)
        {
            report.AppendLine("No complete start/response marker pair was recorded.");
            report.AppendLine();
            return;
        }

        report.AppendLine("| Test pair | Start (ms) | Response (ms) | Marker-to-response interval |");
        report.AppendLine("|---:|---:|---:|---:|");
        foreach (var pair in completed)
        {
            var frequency = pair.Response.QpcFrequency > 0 ? pair.Response.QpcFrequency : pair.Start.QpcFrequency;
            var interval = frequency > 0
                ? ((pair.Response.QpcTimestamp - pair.Start.QpcTimestamp) * 1000d / frequency).ToString("N3", CultureInfo.InvariantCulture) + " ms"
                : "Unavailable";
            report.AppendLine($"| {pair.Pair} | {pair.Start.RelativeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} | {pair.Response.RelativeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} | {interval} |");
        }
        report.AppendLine();
    }

    private static string? GetDetailValue(string? details, string key)
    {
        if (string.IsNullOrWhiteSpace(details))
            return null;

        foreach (var part in details.Split(';'))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part[..separator].Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(separator + 1)..];
        }

        return null;
    }

    private static void AppendLatestPostFocusWindow(
        StringBuilder report,
        IReadOnlyList<DiagnosticEvent> focusEvents,
        IReadOnlyList<DiagnosticEvent> renderingEvents,
        IReadOnlyList<DiagnosticEvent> cpuEvents,
        IReadOnlyList<DiagnosticEvent> gpuEvents,
        IReadOnlyList<DiagnosticEvent> markerEvents,
        IReadOnlyList<DiagnosticEvent> collectorEvents,
        PostFocusEtwWindow etwWindow,
        int targetThreadDiscoveryCount)
    {
        report.AppendLine("## Latest 1.5-second window after focus regained");
        report.AppendLine();

        var focusGained = focusEvents
            .Where(item => item.EventType == "FocusGained")
            .OrderBy(item => item.QpcTimestamp)
            .LastOrDefault();

        if (focusGained is null)
        {
            report.AppendLine("No FocusGained marker was recorded; this window cannot be aligned.");
            report.AppendLine();
            return;
        }

        var start = focusGained.RelativeMilliseconds;
        var end = start + 1500d;
        report.AppendLine($"Reference interval: {start.ToString("F3", CultureInfo.InvariantCulture)} to {end.ToString("F3", CultureInfo.InvariantCulture)} ms relative to session start.");
        report.AppendLine();
        AppendPostFocusEvidenceSummary(report, focusGained, renderingEvents, cpuEvents, gpuEvents, collectorEvents, etwWindow, targetThreadDiscoveryCount);

        var windowEvents = renderingEvents.Concat(cpuEvents).Concat(gpuEvents).Concat(markerEvents).Concat(collectorEvents).Concat(etwWindow.Events)
            .Where(item => item.RelativeMilliseconds >= start && item.RelativeMilliseconds <= end)
            .OrderBy(item => item.QpcTimestamp)
            .ToArray();

        if (windowEvents.Length == 0)
        {
            report.AppendLine("No rendering, CPU, GPU, ETW or marker events were available in this interval.");
            report.AppendLine();
            return;
        }

        report.AppendLine("| Relative time (ms) | Event | Source | Details |");
        report.AppendLine("|---:|---|---|---|");
        foreach (var item in windowEvents)
        {
            report.AppendLine($"| {item.RelativeMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} | {EscapeMarkdown(item.EventType)} | {EscapeMarkdown(item.Source)} | {EscapeMarkdown(Truncate(item.Details ?? "—", 300))} |");
        }
        report.AppendLine();
        if (etwWindow.TruncatedCount > 0)
            report.AppendLine($"ETW event detail limit reached: {etwWindow.TruncatedCount} additional events in this interval are not listed. Counts above include the complete JSONL time window.");
        report.AppendLine();
    }

    private static async Task<PostFocusEtwWindow> ReadPostFocusEtwWindowAsync(
        string jsonlPath,
        DiagnosticEvent focusGained,
        int maximumDetailRows,
        CancellationToken cancellationToken)
    {
        var endQpc = focusGained.QpcTimestamp + (long)(focusGained.QpcFrequency * 1.5d);
        var details = new List<DiagnosticEvent>(Math.Min(maximumDetailRows, 300));
        var total = 0;
        var win32k = 0;
        var win32kTargetProcess = 0;
        var win32kOtherProcess = 0;
        var systemDpc = 0;
        var systemIsr = 0;
        var gpuVsyncDpc = 0;
        var targetReady = 0;
        var targetContextSwitches = 0;
        var incomingSwitches = 0;
        var outgoingSwitches = 0;
        var incomingWaitValues = new HashSet<string>(StringComparer.Ordinal);
        var outgoingStates = new HashSet<string>(StringComparer.Ordinal);
        var readyToRunSamples = new List<double>();

        await using var stream = new FileStream(
            jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
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
            if (!IsEtwEvent(item) ||
                item.QpcTimestamp < focusGained.QpcTimestamp ||
                item.QpcTimestamp > endQpc)
                continue;

            total++;
            if (details.Count < maximumDetailRows)
                details.Add(item);

            if (item.Source.Contains("Win32k", StringComparison.OrdinalIgnoreCase))
            {
                win32k++;
                if (focusGained.ProcessId is uint targetPid && item.ProcessId == targetPid)
                    win32kTargetProcess++;
                else
                    win32kOtherProcess++;
            }
            if (item.Source.Equals("NT Kernel Logger", StringComparison.OrdinalIgnoreCase) &&
                item.EventType.Contains("DPC", StringComparison.OrdinalIgnoreCase))
                systemDpc++;
            if (item.Source.Equals("NT Kernel Logger", StringComparison.OrdinalIgnoreCase) &&
                item.EventType.Contains("ISR", StringComparison.OrdinalIgnoreCase))
                systemIsr++;
            if (item.Source.Contains("DxgKrnl", StringComparison.OrdinalIgnoreCase) &&
                item.EventType.Contains("VSyncDPC", StringComparison.OrdinalIgnoreCase))
                gpuVsyncDpc++;

            if (item.EventType == "TargetThreadReady" && item.ProcessId == focusGained.ProcessId)
                targetReady++;

            if (item.EventType != "TargetThreadContextSwitch" || item.ProcessId != focusGained.ProcessId)
                continue;

            targetContextSwitches++;
            var role = GetDetailValue(item.Details, "TargetThreadRole");
            if (role is "Incoming" or "Both")
            {
                incomingSwitches++;
                var value = GetDetailValue(item.Details, "NewThreadWaitTime");
                if (!string.IsNullOrWhiteSpace(value) && incomingWaitValues.Count < 8)
                    incomingWaitValues.Add(value);
            }
            if (role is "Outgoing" or "Both")
            {
                outgoingSwitches++;
                var value = GetDetailValue(item.Details, "OldThreadState");
                if (!string.IsNullOrWhiteSpace(value) && outgoingStates.Count < 8)
                    outgoingStates.Add(value);
            }

            var readyValue = GetDetailValue(item.Details, "ReadyToRunMs");
            if (double.TryParse(readyValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var readyMs))
                readyToRunSamples.Add(readyMs);
        }

        var summary = new PostFocusEtwSummary(
            total, win32k, win32kTargetProcess, win32kOtherProcess,
            systemDpc, systemIsr, gpuVsyncDpc, targetReady,
            targetContextSwitches, incomingSwitches, outgoingSwitches,
            incomingWaitValues.ToArray(), outgoingStates.ToArray(), readyToRunSamples);
        return new PostFocusEtwWindow(details, summary, Math.Max(0, total - details.Count));
    }

    private sealed record PostFocusEtwSummary(
        int TotalEvents,
        int Win32kEvents,
        int Win32kTargetProcessEvents,
        int Win32kOtherProcessEvents,
        int SystemDpcEvents,
        int SystemIsrEvents,
        int GpuVsyncDpcEvents,
        int TargetReadyThreadEvents,
        int TargetContextSwitches,
        int IncomingSwitches,
        int OutgoingSwitches,
        IReadOnlyList<string> IncomingWaitValues,
        IReadOnlyList<string> OutgoingStates,
        IReadOnlyList<double> ReadyToRunSamples);

    private sealed record PostFocusEtwWindow(
        IReadOnlyList<DiagnosticEvent> Events,
        PostFocusEtwSummary Summary,
        int TruncatedCount)
    {
        public static PostFocusEtwWindow Empty { get; } = new(
            Array.Empty<DiagnosticEvent>(),
            new PostFocusEtwSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<double>()),
            0);
    }

    private static bool IsEtwEvent(DiagnosticEvent item)
        => item.EventType.StartsWith("Etw:", StringComparison.OrdinalIgnoreCase) ||
           item.EventType is "TargetThreadReady" or "TargetThreadContextSwitch";

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
