using System.IO;
using System.Diagnostics;
using Xunit;
using WindowTrace.Core;
using WindowTrace.Storage;
using WindowTrace.Monitoring;

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
    public void FrameMetricsAggregator_ComputesRollingFpsAndFrameTimeVariance()
    {
        var clock = new QpcClock();
        var aggregator = new FrameMetricsAggregator(clock);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MsBetweenPresents"] = "16",
            ["ProcessID"] = "4242",
            ["Application"] = "TestGame",
            ["PresentMode"] = "Composed: Flip",
            ["MsGPUBusy"] = "8",
            ["MsGPUWait"] = "1",
            ["Dropped"] = "0"
        };

        DiagnosticEvent? summary = null;
        for (var i = 0; i <= 120; i++)
        {
            var qpc = clock.AnchorTimestamp + (long)(clock.Frequency * i / 60d);
            summary = aggregator.AddFrame(qpc, fields, qpcIsFrameTimestamp: false) ?? summary;
        }

        Assert.NotNull(summary);
        Assert.Equal("RenderingSummary", summary!.EventType);
        Assert.Equal((uint)4242, summary.ProcessId);
        Assert.Contains("FPS=62.50", summary.Details);
        Assert.Contains("FrameTimeVarianceMs2=0.0000", summary.Details);
        Assert.Contains("PresentMode=Composed: Flip", summary.Details);
    }

    [Fact]
    public async Task DiagnosticSessionManager_CapturesPreRollAndFocusTransitions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var clock = new QpcClock();
        var manager = new DiagnosticSessionManager(clock, new BoundedEventBuffer(1000), directory);

        try
        {
            manager.SetTarget(111, "TestGame", 111);

            var preRollQpc = clock.GetTimestamp();
            manager.PublishTelemetry(CreateEvent(preRollQpc) with { EventType = "PreRollEvent" });

            var lostQpc = clock.GetTimestamp();
            manager.PublishForeground(
                CreateEvent(lostQpc) with { EventType = "ForegroundChanged", ProcessId = 222 },
                222);

            Assert.True(manager.IsSessionActive);
            Assert.Equal(DiagnosticSessionState.FocusLost, manager.State);

            var gainedQpc = clock.GetTimestamp();
            manager.PublishForeground(
                CreateEvent(gainedQpc) with { EventType = "ForegroundChanged", ProcessId = 111 },
                111);

            Assert.Equal(DiagnosticSessionState.FocusGainedPostCapture, manager.State);
            await manager.StopAsync();

            var jsonlPath = Assert.IsType<string>(manager.LastCompletedSessionPath);
            Assert.True(File.Exists(jsonlPath));
            Assert.True(File.Exists(Path.ChangeExtension(jsonlPath, ".quality.json")));

            var lines = await File.ReadAllLinesAsync(jsonlPath);
            var events = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => System.Text.Json.JsonSerializer.Deserialize<DiagnosticEvent>(line))
                .Where(item => item is not null)
                .ToArray();

            Assert.Contains(events, item => item!.EventType == "PreRollEvent" && item.SessionId is not null);
            Assert.Contains(events, item => item!.EventType == "FocusLost");
            Assert.Contains(events, item => item!.EventType == "FocusGained");
            Assert.Contains(events, item => item!.EventType == "DiagnosticSessionStopped");
        }
        finally
        {
            try { await manager.StopAsync(); } catch { }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
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
