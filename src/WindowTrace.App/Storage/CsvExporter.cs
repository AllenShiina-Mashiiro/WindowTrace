using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WindowTrace.Core;

namespace WindowTrace.Storage;

public static class CsvExporter
{
    private static readonly string[] Headers =
    [
        "EventId", "SessionId", "EventType", "Source", "QpcTimestamp", "QpcFrequency",
        "UtcTimestamp", "RelativeMilliseconds", "ProcessId", "ThreadId", "WindowHandle",
        "ProcessName", "WindowTitle", "Details"
    ];

    public static async Task ExportJsonlAsync(
        string jsonlPath,
        string csvPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(jsonlPath))
            throw new FileNotFoundException("Diagnostic JSONL file was not found.", jsonlPath);

        if (string.Equals(Path.GetFullPath(jsonlPath), Path.GetFullPath(csvPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CSV export path must not overwrite the source JSONL file.");

        var directory = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var input = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        using var reader = new StreamReader(input);
        await using var output = new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(true));

        await writer.WriteLineAsync(string.Join(",", Headers));
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
                throw new InvalidDataException($"Malformed JSONL record at line {lineNumber}.", ex);
            }

            if (item is null)
                throw new InvalidDataException($"JSONL record at line {lineNumber} was empty or null.");

            var values = new string?[]
            {
                item.EventId.ToString(),
                item.SessionId?.ToString(),
                item.EventType,
                item.Source,
                item.QpcTimestamp.ToString(CultureInfo.InvariantCulture),
                item.QpcFrequency.ToString(CultureInfo.InvariantCulture),
                item.UtcTimestamp.ToString("O", CultureInfo.InvariantCulture),
                item.RelativeMilliseconds.ToString("R", CultureInfo.InvariantCulture),
                item.ProcessId?.ToString(CultureInfo.InvariantCulture),
                item.ThreadId?.ToString(CultureInfo.InvariantCulture),
                item.WindowHandle?.ToString(CultureInfo.InvariantCulture),
                item.ProcessName,
                item.WindowTitle,
                item.Details
            };

            await writer.WriteLineAsync(string.Join(",", values.Select(Escape)));
        }

        await writer.FlushAsync(cancellationToken);
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}
