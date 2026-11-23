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

    [Theory]
    [InlineData("Microsoft-Windows-DxgKrnl", "Profiler/Start", "Profiler", 14968u, 14968u, false)]
    [InlineData("Microsoft-Windows-DxgKrnl", "QueuePacket/Start", "QueuePacket", 14968u, 14968u, true)]
    [InlineData("Microsoft-Windows-DxgKrnl", "QueuePacket/Start", "QueuePacket", 4u, 14968u, false)]
    [InlineData("Microsoft-Windows-DxgKrnl", "VSyncDPC", "VSyncDPC", 4u, 14968u, true)]
    [InlineData("Microsoft-Windows-Win32k", "WakeInputIdle", "WakeInputIdle", 14968u, 14968u, true)]
    [InlineData("Microsoft-Windows-Win32k", "WakeInputIdle", "WakeInputIdle", 27360u, 14968u, false)]
    [InlineData("Microsoft-Windows-Dwm-Core", "SCHEDULE_PRESENT/Start", "SCHEDULE_PRESENT", 2032u, 14968u, true)]
    [InlineData("Microsoft-Windows-DXGI", "IDXGISwapChain_Present/Start", "Present", 14968u, 14968u, true)]
    [InlineData("Microsoft-Windows-DXGI", "IDXGISwapChain_GetDesc/Start", "GetDesc", 14968u, 14968u, false)]
    [InlineData("NT Kernel Logger", "DPC/Start", "DPC", 4u, 14968u, true)]
    [InlineData("NT Kernel Logger", "CSwitch", "CSwitch", 4u, 14968u, false)]
    public void EtwEventFilter_PersistsOnlyDiagnosticEventFamilies(
        string provider,
        string eventName,
        string taskName,
        uint processId,
        uint targetProcessId,
        bool expected)
    {
        Assert.Equal(expected, EtwEventFilter.ShouldPersist(
            provider, eventName, taskName, processId, targetProcessId));
    }

    [Fact]
    public void EtwTimestampNormalizer_DistinguishesEventQpcFromReceiptFallback()
    {
        var eventTimestamp = EtwTimestampNormalizer.Normalize(1234, 5678);
        Assert.Equal(1234, eventTimestamp.QpcTimestamp);
        Assert.Equal("ETWEventQPC", eventTimestamp.Quality);
        Assert.False(eventTimestamp.UsedFallback);

        var fallbackTimestamp = EtwTimestampNormalizer.Normalize(0, 5678);
        Assert.Equal(5678, fallbackTimestamp.QpcTimestamp);
        Assert.Equal("CollectorReceiptQPCFallback", fallbackTimestamp.Quality);
        Assert.True(fallbackTimestamp.UsedFallback);
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
        Assert.True(clock.ToUtc(second) > clock.ToUtc(first));
        Assert.NotEqual(DateTimeOffset.MinValue, clock.AnchorUtc);
    }

    [Fact]
    public async Task SessionReportGenerator_WritesEnvironmentQualityAndTimelineSummary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jsonlPath = Path.Combine(directory, "session.jsonl");
        var clock = new QpcClock();
        var sessionId = Guid.NewGuid();
        var qpc = clock.GetTimestamp();

        try
        {
            var events = new[]
            {
                new DiagnosticEvent(Guid.NewGuid(), "FocusLost", "WindowTrace", qpc, clock.Frequency,
                    clock.ToUtc(qpc), 0, 4242, null, null, "TestGame", null, "Target left foreground.", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "FocusGained", "WindowTrace", qpc + clock.Frequency,
                    clock.Frequency, clock.ToUtc(qpc + clock.Frequency), 1000, 4242, null, null,
                    "TestGame", null, "Target regained foreground.", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "RenderingSummary", "PresentMonMetrics",
                    qpc + clock.Frequency, clock.Frequency, clock.ToUtc(qpc + clock.Frequency), 1000,
                    4242, null, null, "TestGame", null, "FPS=60.00;FrameTimeVarianceMs2=0.1000", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "TargetThreadDiscovered", "NT Kernel Logger",
                    qpc + (long)(clock.Frequency * 0.5d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 0.5d)), 500,
                    4242, 88, null, null, null, "ThreadMappingSource=KernelThreadStart", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "TargetThreadContextSwitch", "NT Kernel Logger",
                    qpc + (long)(clock.Frequency * 1.5d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 1.5d)), 1500,
                    4242, 88, null, null, null, "TargetThreadRole=Incoming;TargetThreadId=88;OldThreadId=12;OldThreadProcessId=4;NewThreadId=88;NewThreadProcessId=4242;OldThreadState=5;OldThreadWaitReason=6;OldThreadWaitMode=1;NewThreadWaitTime=1500;ReadyToRunMs=300.000", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "TargetThreadReady", "NT Kernel Logger",
                    qpc + (long)(clock.Frequency * 1.2d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 1.2d)), 1200,
                    4242, 88, null, null, null, "ReadyThreadId=88;AdjustReason=1;AdjustIncrement=2;Flag=1", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "Etw:DPC/Start", "NT Kernel Logger",
                    qpc + (long)(clock.Frequency * 1.1d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 1.1d)), 1100,
                    0, 0, null, null, null, "DPC metadata only", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "Etw:ISR/Start", "NT Kernel Logger",
                    qpc + (long)(clock.Frequency * 1.2d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 1.2d)), 1200,
                    0, 0, null, null, null, "ISR metadata only", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "Etw:VSyncDPC", "Microsoft-Windows-DxgKrnl",
                    qpc + (long)(clock.Frequency * 1.25d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 1.25d)), 1250,
                    4, 0, null, null, null, "GPU VSync DPC metadata only", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "Etw:ReadyThread", "NT Kernel Logger",
                    qpc + (long)(clock.Frequency * 1.3d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 1.3d)), 1300,
                    4242, 88, null, null, null, "ReadyThread metadata only", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "CollectorWarning", "WindowTrace",
                    qpc + (long)(clock.Frequency * 1.2d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 1.2d)), 1200,
                    null, null, null, null, null, "ETW realtime consumer stopped unexpectedly.", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "InputTestMarker", "RegisterHotKey",
                    qpc + (long)(clock.Frequency * 0.2d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 0.2d)), 200,
                    4242, null, null, "TestGame", null, "TestPair=1;MarkerPhase=Start", sessionId),
                new DiagnosticEvent(Guid.NewGuid(), "InputTestMarker", "RegisterHotKey",
                    qpc + (long)(clock.Frequency * 1.7d), clock.Frequency,
                    clock.ToUtc(qpc + (long)(clock.Frequency * 1.7d)), 1700,
                    4242, null, null, "TestGame", null, "TestPair=1;MarkerPhase=Response", sessionId)
            };
            await File.WriteAllLinesAsync(jsonlPath, events.Select(item => System.Text.Json.JsonSerializer.Serialize(item)));

            var metadata = new SessionMetadata(
                sessionId, clock.ToUtc(qpc), "Windows 11", "10.0.26200", 26200,
                "25H2", "26200", 1, "Professional", ".NET 10", "X64", clock.Frequency,
                4242, "TestGame", "Test CPU", "Test Identifier", 16, 16UL * 1024 * 1024 * 1024,
                "Test Manufacturer", "Test System", "Test Board", "Test BIOS", "2026-01-01",
                Array.Empty<DisplayAdapterMetadata>(), "0.1.0");
            var quality = new SessionQualityReport(
                sessionId, clock.ToUtc(qpc + clock.Frequency), "Test", 4242, "TestGame",
                qpc, qpc + clock.Frequency, 3000, 2900, 10, 50000, 0, 0, 0,
                0, 1024, 256L * 1024 * 1024, false, null)
            {
                QueueOverflowDroppedRecords = 2,
                FileLimitDroppedRecords = 3,
                WriterErrorDroppedRecords = 4
            };

            await using (var metadataStream = File.Create(Path.ChangeExtension(jsonlPath, ".session.json")))
                await System.Text.Json.JsonSerializer.SerializeAsync(metadataStream, metadata);
            await SessionQualityReport.WriteAsync(Path.ChangeExtension(jsonlPath, ".quality.json"), quality);

            var reportPath = await SessionReportGenerator.WriteAsync(jsonlPath);
            var report = await File.ReadAllTextAsync(reportPath);

            Assert.Contains("WindowTrace diagnostic report", report);
            Assert.Contains("25H2", report);
            Assert.Contains("FocusLost", report);
            Assert.Contains("Rendering summaries", report);
            Assert.Contains("Latest 1.5-second window after focus regained", report);
            Assert.Contains("Post-focus evidence summary", report);
            Assert.Contains("Win32k ETW events in interval", report);
            Assert.Contains("System-wide DPC events in interval (NT Kernel Logger): 1", report);
            Assert.Contains("System-wide ISR events in interval (NT Kernel Logger): 1", report);
            Assert.Contains("GPU VSync DPC events in interval (DxgKrnl): 1", report);
            Assert.Contains("Target-thread ReadyThread events in interval: 1", report);
            Assert.Contains("ready-to-running delay from QPC (ms): n=1; avg=300.000; max=300.000", report);
            Assert.Contains("Target threads mapped during the session: 1", report);
            Assert.Contains("Target-thread context switches in interval: 1 (incoming: 1; outgoing: 0)", report);
            Assert.Contains("NewThreadWaitTime raw values: 1500", report);
            Assert.Contains("FPS=60.00", report);
            Assert.Contains("ThreadContextSwitch", report);
            Assert.Contains("NT Kernel Logger", report);
            Assert.Contains("Collector health and data gaps", report);
            Assert.Contains("ETW realtime consumer stopped unexpectedly", report);
            Assert.Contains("Manual input-response marker intervals", report);
            Assert.Contains("1,500.000 ms", report);
            Assert.Contains("ETW events lost", report);
            Assert.Contains("Records not persisted (total): 9", report);
            Assert.Contains("Queue overflow: 2", report);
            Assert.Contains("File budget drops (combined): 3", report);
            Assert.Contains("Writer errors: 4", report);
            Assert.Contains("does not establish causation", report);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SessionReportGenerator_CountsFullPostFocusEtwWindowIndependentOfDetailLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jsonlPath = Path.Combine(directory, "etw-window.jsonl");
        var clock = new QpcClock();
        var sessionId = Guid.NewGuid();
        var focusQpc = clock.GetTimestamp();

        try
        {
            var events = new List<DiagnosticEvent>
            {
                new(Guid.NewGuid(), "FocusGained", "WindowTrace", focusQpc, clock.Frequency,
                    clock.ToUtc(focusQpc), 0, 21948, null, null, "TestGame", null,
                    "Target regained foreground.", sessionId)
            };

            for (var i = 0; i < 97; i++)
            {
                var qpc = focusQpc + (long)(clock.Frequency * (0.001d + i * 0.01d));
                events.Add(new DiagnosticEvent(
                    Guid.NewGuid(), $"Etw:Win32kEvent{i}", "Microsoft-Windows-Win32k",
                    qpc, clock.Frequency, clock.ToUtc(qpc), i * 10,
                    21948, 100u + (uint)i, null, "TestGame", null,
                    "TimestampQuality=ETWQPC", sessionId));
            }

            // An event outside the requested 1.5-second window must not affect its count.
            var outsideQpc = focusQpc + (long)(clock.Frequency * 1.6d);
            events.Add(new DiagnosticEvent(
                Guid.NewGuid(), "Etw:OutsideWindow", "Microsoft-Windows-Win32k",
                outsideQpc, clock.Frequency, clock.ToUtc(outsideQpc), 1600,
                21948, 999u, null, "TestGame", null, "Outside interval", sessionId));

            await File.WriteAllLinesAsync(jsonlPath,
                events.Select(item => System.Text.Json.JsonSerializer.Serialize(item)));

            var report30Path = await SessionReportGenerator.WriteAsync(
                jsonlPath, maximumPostFocusEtwRows: 30);
            var report30 = await File.ReadAllTextAsync(report30Path);
            Assert.Contains("ETW events in interval (all providers): 97", report30);
            Assert.Contains("Win32k ETW events in interval: 97", report30);
            Assert.Contains("Win32k ETW events for target PID 21948: 97; other/unknown PID: 0", report30);
            Assert.Contains("ETW event detail limit reached: 67 additional events", report30);

            var report300Path = await SessionReportGenerator.WriteAsync(
                jsonlPath, maximumPostFocusEtwRows: 300);
            var report300 = await File.ReadAllTextAsync(report300Path);
            Assert.Contains("ETW events in interval (all providers): 97", report300);
            Assert.Contains("Win32k ETW events in interval: 97", report300);
            Assert.DoesNotContain("ETW event detail limit reached:", report300);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
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
    public void FrameMetricsAggregator_KeepsSwapChainFrameRatesSeparate()
    {
        var clock = new QpcClock();
        var aggregator = new FrameMetricsAggregator(clock);
        var chainA = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MsBetweenPresents"] = "16",
            ["SwapChainAddress"] = "0xA",
            ["ProcessID"] = "4242",
            ["Application"] = "TestGame"
        };
        var chainB = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MsBetweenPresents"] = "16",
            ["SwapChainAddress"] = "0xB",
            ["ProcessID"] = "4242",
            ["Application"] = "TestGame"
        };

        DiagnosticEvent? summaryA = null;
        DiagnosticEvent? summaryB = null;
        for (var i = 0; i <= 120; i++)
        {
            var qpc = clock.AnchorTimestamp + (long)(clock.Frequency * i / 60d);
            summaryA = aggregator.AddFrame(qpc, chainA, qpcIsFrameTimestamp: false) ?? summaryA;
            summaryB = aggregator.AddFrame(qpc, chainB, qpcIsFrameTimestamp: false) ?? summaryB;
        }

        Assert.NotNull(summaryA);
        Assert.NotNull(summaryB);
        Assert.Contains("SwapChainAddress=0xA", summaryA!.Details);
        Assert.Contains("SwapChainAddress=0xB", summaryB!.Details);
        Assert.Contains("FPS=62.50", summaryA.Details);
        Assert.Contains("FPS=62.50", summaryB.Details);
    }

    [Fact]
    public void FrameMetricsAggregator_DoesNotTreatReceiptTimeAsFrameInterval()
    {
        var clock = new QpcClock();
        var aggregator = new FrameMetricsAggregator(clock);
        var fields = new Dictionary<string, string>
        {
            ["ProcessID"] = "4242",
            ["Application"] = "TestGame"
        };

        DiagnosticEvent? summary = null;
        for (var i = 0; i <= 120; i++)
        {
            var qpc = clock.AnchorTimestamp + (long)(clock.Frequency * i / 60d);
            summary = aggregator.AddFrame(qpc, fields, qpcIsFrameTimestamp: false) ?? summary;
        }

        Assert.Null(summary);
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

            Assert.Contains(events, item => item!.EventType == "PreRollEvent" && item.SessionId is not null && item.CapturePhase == "PreRoll");
            Assert.Contains(events, item => item!.EventType == "FocusLost");

            var quality = System.Text.Json.JsonSerializer.Deserialize<SessionQualityReport>(
                await File.ReadAllTextAsync(Path.ChangeExtension(jsonlPath, ".quality.json")));
            Assert.NotNull(quality);
            Assert.True(quality!.PreRollEventsSelected >= 1);
            Assert.True(quality.PreRollEventsQueued >= 1);
            Assert.True(quality.PreRollEventsWritten >= 1);
            Assert.True(quality.PreRollEventsSelected >= quality.PreRollEventsQueued);
            Assert.True(quality.PreRollEventsQueued >= quality.PreRollEventsWritten);
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
    public async Task DiagnosticSessionManager_TargetSelectedWhileBackgroundStartsOnlyAfterItsFirstFocusLoss()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var clock = new QpcClock();
        var manager = new DiagnosticSessionManager(clock, new BoundedEventBuffer(1000), directory);

        try
        {
            // The picker dialog belongs to WindowTrace, so the target game is not
            // foreground when the user confirms the selection.
            manager.SetTarget(111, "TestGame", 222);

            manager.PublishForeground(
                CreateEvent(clock.GetTimestamp()) with { EventType = "ForegroundChanged", ProcessId = 111 },
                111);

            Assert.False(manager.IsSessionActive);

            manager.PublishForeground(
                CreateEvent(clock.GetTimestamp()) with { EventType = "ForegroundChanged", ProcessId = 222 },
                222);

            Assert.True(manager.IsSessionActive);
            Assert.Equal(DiagnosticSessionState.FocusLost, manager.State);
        }
        finally
        {
            try { await manager.StopAsync(); } catch { }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DiagnosticSessionManager_ResetsPostCaptureDeadlineAfterRepeatedFocusLoss()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var clock = new QpcClock();
        var manager = new DiagnosticSessionManager(clock, new BoundedEventBuffer(1000), directory);

        try
        {
            manager.SetTarget(111, "TestGame", 111);
            var origin = clock.GetTimestamp();

            var firstLoss = origin + clock.Frequency;
            manager.PublishForeground(
                CreateEvent(firstLoss) with { EventType = "ForegroundChanged", ProcessId = 222 },
                222);

            var firstGain = origin + (long)(clock.Frequency * 2d);
            manager.PublishForeground(
                CreateEvent(firstGain) with { EventType = "ForegroundChanged", ProcessId = 111 },
                111);

            var secondLoss = origin + (long)(clock.Frequency * 3d);
            manager.PublishForeground(
                CreateEvent(secondLoss) with { EventType = "ForegroundChanged", ProcessId = 222 },
                222);

            var secondGain = origin + (long)(clock.Frequency * 4d);
            manager.PublishForeground(
                CreateEvent(secondGain) with { EventType = "ForegroundChanged", ProcessId = 111 },
                111);

            var finalDeadline = secondGain + (long)(clock.Frequency * 3d);
            await manager.CheckPostCaptureDeadlineAsync(finalDeadline - 1);

            Assert.True(manager.IsSessionActive);
            Assert.Equal(DiagnosticSessionState.FocusGainedPostCapture, manager.State);

            await manager.CheckPostCaptureDeadlineAsync(finalDeadline);

            Assert.False(manager.IsSessionActive);
            Assert.Equal(DiagnosticSessionState.Completed, manager.State);

            var jsonlPath = Assert.IsType<string>(manager.LastCompletedSessionPath);
            var lines = await File.ReadAllLinesAsync(jsonlPath);
            var events = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => System.Text.Json.JsonSerializer.Deserialize<DiagnosticEvent>(line))
                .Where(item => item is not null)
                .ToArray();

            Assert.Equal(2, events.Count(item => item!.EventType == "FocusGained"));
            Assert.Contains(events, item => item!.EventType == "FocusLostAgain");
            Assert.Contains(events, item => item!.EventType == "DiagnosticSessionCompleted");
        }
        finally
        {
            try { await manager.StopAsync(); } catch { }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DiagnosticSessionManager_CompletesAfterMinimumWhenCpuAndGpuEvidenceArrive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var clock = new QpcClock();
        var manager = new DiagnosticSessionManager(clock, new BoundedEventBuffer(1000), directory);

        try
        {
            manager.SetTarget(111, "TestGame", 111);
            var origin = clock.GetTimestamp();
            var focusLost = origin + clock.Frequency;
            manager.PublishForeground(
                CreateEvent(focusLost) with { EventType = "ForegroundChanged", ProcessId = 222 },
                222);

            var focusGained = origin + (long)(clock.Frequency * 2d);
            manager.PublishForeground(
                CreateEvent(focusGained) with { EventType = "ForegroundChanged", ProcessId = 111 },
                111);

            var sampleQpc = focusGained + (long)(clock.Frequency * 1.1d);
            manager.PublishTelemetry(CreateEvent(sampleQpc) with
            {
                EventType = "CpuSample",
                QpcTimestamp = sampleQpc
            });
            manager.PublishTelemetry(CreateEvent(sampleQpc + 1) with
            {
                EventType = "GpuPdhSample",
                QpcTimestamp = sampleQpc + 1
            });

            var minimumDeadline = focusGained + (long)(clock.Frequency * 1.5d);
            await manager.CheckPostCaptureDeadlineAsync(minimumDeadline - 1);
            Assert.True(manager.IsSessionActive);

            await manager.CheckPostCaptureDeadlineAsync(minimumDeadline);
            Assert.False(manager.IsSessionActive);
            Assert.Equal(DiagnosticSessionState.Completed, manager.State);

            var jsonlPath = Assert.IsType<string>(manager.LastCompletedSessionPath);
            var lines = await File.ReadAllLinesAsync(jsonlPath);
            Assert.Contains(lines, line => line.Contains("CompletionReason=PostFocusEvidenceReady", StringComparison.Ordinal));
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

    [Fact]
    public async Task JsonlEventWriter_FlushesEveryAcceptedRecordOnDispose()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "writer.jsonl");

        try
        {
            var writer = new JsonlEventWriter(path, queueCapacity: 512);
            const int recordCount = 200;
            for (var i = 0; i < recordCount; i++)
                Assert.True(writer.TryWrite(CreateEvent(i + 1) with { EventType = $"WriterEvent{i}" }));

            await writer.DisposeAsync();

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(recordCount, lines.Length);
            Assert.All(lines, line => Assert.NotNull(
                System.Text.Json.JsonSerializer.Deserialize<DiagnosticEvent>(line)));
            Assert.Equal(0, writer.DroppedRecords);
            Assert.Null(writer.LastError);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task JsonlEventWriter_EnforcesFileLimitWithoutWritingPartialRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "limited.jsonl");

        try
        {
            const long maxBytes = 1024 * 1024;
            var writer = new JsonlEventWriter(path, queueCapacity: 4, maxBytes: maxBytes);
            Assert.True(writer.TryWrite(CreateEvent(1) with { EventType = "BeforeLimit" }));
            Assert.True(writer.TryWrite(CreateEvent(2) with
            {
                EventType = "OversizedRecord",
                Details = new string('x', (int)maxBytes)
            }));
            Assert.True(writer.TryWrite(CreateEvent(3) with { EventType = "AfterLimit" }));

            await writer.DisposeAsync();

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Single(lines);
            Assert.Contains("BeforeLimit", lines[0]);
            Assert.True(writer.LimitReached);
            Assert.True(writer.DroppedRecords >= 2);
            Assert.Equal(0, writer.QueueOverflowDroppedRecords);
            Assert.True(writer.FileLimitDroppedRecords >= 2);
            Assert.Equal(0, writer.WriterErrorDroppedRecords);
            Assert.True(writer.BytesWritten <= maxBytes);
            Assert.Null(writer.LastError);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task JsonlEventWriter_PreservesCriticalMarkersAfterRegularFileBudgetIsReached()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "priority.jsonl");

        try
        {
            var writer = new JsonlEventWriter(
                path, queueCapacity: 4096, maxBytes: 1024 * 1024,
                priorityQueueCapacity: 16, criticalReserveBytes: 128 * 1024);
            var detail = new string('x', 4096);
            for (var i = 0; i < 2000 && !writer.LimitReached; i++)
            {
                writer.TryWrite(CreateEvent(i + 1) with
                {
                    EventType = "HighVolumeTelemetry",
                    Details = detail
                });
                if (i % 20 == 0)
                    await Task.Delay(1);
            }

            Assert.True(writer.LimitReached);
            Assert.True(writer.RegularFileBudgetReached);
            Assert.False(writer.TotalFileLimitReached);
            Assert.True(writer.TryWrite(CreateEvent(9000) with
            {
                EventType = "FocusGained",
                Details = "Critical focus marker"
            }));

            await writer.DisposeAsync();

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Contains(lines, line => line.Contains("Critical focus marker", StringComparison.Ordinal));
            Assert.True(writer.FileLimitDroppedRecords > 0);
            Assert.True(writer.BytesWritten <= writer.MaxBytes);
            Assert.Null(writer.LastError);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DiagnosticSessionManager_DuplicateForegroundNotificationDoesNotExtendPostCaptureDeadline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var clock = new QpcClock();
        var manager = new DiagnosticSessionManager(clock, new BoundedEventBuffer(1000), directory);

        try
        {
            manager.SetTarget(111, "TestGame", 111);
            var origin = clock.GetTimestamp();

            var focusLost = origin + clock.Frequency;
            manager.PublishForeground(
                CreateEvent(focusLost) with { EventType = "ForegroundChanged", ProcessId = 222 },
                222);

            var focusGained = origin + (long)(clock.Frequency * 2d);
            manager.PublishForeground(
                CreateEvent(focusGained) with { EventType = "ForegroundChanged", ProcessId = 111 },
                111);

            var duplicateForeground = origin + (long)(clock.Frequency * 4d);
            manager.PublishForeground(
                CreateEvent(duplicateForeground) with { EventType = "ForegroundChanged", ProcessId = 111 },
                111);

            var expectedDeadline = focusGained + (long)(clock.Frequency * 3d);
            await manager.CheckPostCaptureDeadlineAsync(expectedDeadline - 1);

            Assert.True(manager.IsSessionActive);
            Assert.Equal(DiagnosticSessionState.FocusGainedPostCapture, manager.State);

            await manager.CheckPostCaptureDeadlineAsync(expectedDeadline);

            Assert.False(manager.IsSessionActive);
            Assert.Equal(DiagnosticSessionState.Completed, manager.State);

            var jsonlPath = Assert.IsType<string>(manager.LastCompletedSessionPath);
            var lines = await File.ReadAllLinesAsync(jsonlPath);
            var events = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => System.Text.Json.JsonSerializer.Deserialize<DiagnosticEvent>(line))
                .Where(item => item is not null)
                .ToArray();

            Assert.Single(events.Where(item => item!.EventType == "FocusGained"));
            Assert.Contains(events, item =>
                item!.EventType == "ForegroundChanged" && item.QpcTimestamp == duplicateForeground);
            Assert.Contains(events, item => item!.EventType == "DiagnosticSessionCompleted");
        }
        finally
        {
            try { await manager.StopAsync(); } catch { }
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
