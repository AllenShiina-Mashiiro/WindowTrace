using System.Diagnostics;
using WindowTrace.Core;
using WindowTrace.Storage;

namespace WindowTrace.Tests;

public sealed class CoreTests
{
    [Fact]
    public void BoundedEventBuffer_EvictsOldestEventAndCountsEviction()
    {
        var buffer = new BoundedEventBuffer(2);
        buffer.Add(CreateEvent(1));
        buffer.Add(CreateEvent(2));
        buffer.Add(CreateEvent(3));

        var snapshot = buffer.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(new long[] { 2, 3 }, snapshot.Select(e => e.QpcTimestamp));
        Assert.Equal(1, buffer.DroppedEvents);
    }

    [Fact]
    public void QpcClock_UsesMonotonicTimestamps()
    {
        var clock = new QpcClock();
        var first = clock.GetTimestamp();
        Thread.Sleep(5);
        var second = clock.GetTimestamp();

        Assert.True(second > first);
        Assert.True(clock.Frequency > 0);
        Assert.True(clock.ElapsedMilliseconds(first, second) > 0);
    }

    [Fact]
    public async Task CsvExporter_PreservesFieldsAndEscapesCommasAndQuotes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jsonl = Path.Combine(directory, "events.jsonl");
        var csv = Path.Combine(directory, "events.csv");

        try
        {
            var item = CreateEvent(123) with
            {
                EventType = "FocusLost",
                WindowTitle = "Game, Window",
                Details = "Message contains a \"quoted\" value"
            };

            await File.WriteAllTextAsync(jsonl, System.Text.Json.JsonSerializer.Serialize(item));
            await CsvExporter.ExportJsonlAsync(jsonl, csv);

            var output = await File.ReadAllTextAsync(csv);
            Assert.Contains("\"Game, Window\"", output);
            Assert.Contains("\"Message contains a \"\"quoted\"\" value\"", output);
            Assert.Contains("FocusLost", output);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static DiagnosticEvent CreateEvent(long qpc)
        => new(
            Guid.NewGuid(),
            "TestEvent",
            "UnitTest",
            qpc,
            Stopwatch.Frequency,
            DateTimeOffset.UtcNow,
            0,
            100,
            200,
            null,
            "TestProcess",
            "Test Window",
            "Test details");
}
