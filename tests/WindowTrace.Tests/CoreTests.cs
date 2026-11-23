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

    [Theory]
    [InlineData("Microsoft-Windows-DxgKrnl", "QueuePacket/Start", 21948u, 21948u, 2)]
    [InlineData("Microsoft-Windows-DxgKrnl", "VSyncDPC", 4u, 21948u, 100)]
    [InlineData("Microsoft-Windows-Dwm-Core", "SCHEDULE_PRESENT/Start", 4u, 21948u, 100)]
    [InlineData("NT Kernel Logger", "DPC/Start", 4u, 21948u, 5)]
    [InlineData("Microsoft-Windows-DxgKrnl", "QueuePacket/Start", 4u, 21948u, 0)]
    public void EtwSamplingPolicy_RateLimitsHighVolumeFamiliesWithoutChangingTargetScope(
        string provider, string eventName, uint processId, uint targetProcessId, int expectedInterval)
    {
        Assert.Equal(expectedInterval, EtwSamplingPolicy.GetIntervalMilliseconds(
            provider, eventName, processId, targetProcessId));
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
                WriterErrorDroppedRecords = 4,
                EtwTimestampFallbackCount = 2,
                EtwThrottledEventCount = 123,
                PriorityQueueOverflowDroppedRecords = 1,
                RegularBudgetDroppedRecords = 2,
                HardFileLimitDroppedRecords = 1,
                RegularFileBudgetReached = true
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
            Assert.Contains("ETW timestamps using collector-receipt fallback: 2", report);
            Assert.Contains("ETW events intentionally suppressed by sampling policy: 123", report);
            Assert.Contains("Critical-event queue overflow (subset): 1", report);
            Assert.Contains("Regular telemetry budget drops: 2", report);
            Assert.Contains("Hard file-cap drops: 1", report);
            Assert.Contains("Regular telemetry byte budget reached: True", report);
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
    public async Task SessionReportGenerator_CountsOriginalWin32kSampleIndependentOfDetailLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowTrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jsonlPath = Path.Combine(directory, "original-win32k-window.jsonl");
        var sessionId = Guid.NewGuid();
        const long focusQpc = 53626866106;
        const long qpcFrequency = 10_000_000;
        const double focusRelativeMs = 12209.8804;
        var focusUtc = DateTimeOffset.UnixEpoch;

        // Exact QPC offsets and event-family sequence extracted from the 97 Win32k
        // records in the user's original test.zip capture (last FocusGained + 1.5 s).
        var offsets = new long[]
        {
            4192, 4212, 4226, 10275, 10287, 10301, 10322, 10330, 10341, 169122,
            169230, 169336, 335506, 335550, 335791, 335823, 335867, 337517, 337584,
            337667, 337686, 337715, 337761, 346844, 349362, 349381, 351497, 352372,
            352411, 368491, 369184, 369293, 369323, 370775, 372268, 375279, 375294,
            375307, 382877, 391815, 391846, 393930, 397420, 399258, 399318, 404789,
            404805, 404817, 406418, 408252, 408339, 480096, 482876, 484388, 484417,
            638080, 638759, 638839, 638853, 3452974, 3453124, 3453139, 3453156,
            10241165, 10241947, 10242554, 10242955, 10243322, 10243359, 10243417,
            10243594, 10243617, 10243949, 10243978, 10416547, 10416639, 10416978,
            10417082, 10417863, 10417899, 10419868, 12079214, 12082705, 12083347,
            12084499, 12084535, 12085892, 12086208, 12229878, 12229972, 12230308,
            12230459, 12230507, 12230582, 12230945, 14720805, 14720996
        };
        var eventTypeIds = new int[]
        {
            2, 2, 2, 3, 3, 3, 5, 3, 1, 0, 4, 1, 3, 3, 2, 2, 2, 2, 2, 2,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 0, 2, 5, 3, 5, 2, 2, 2, 1, 0,
            4, 2, 1, 0, 5, 2, 2, 2, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0,
            3, 4, 1, 0, 2, 1, 3, 0, 4, 1, 0, 5, 3, 1, 0, 4, 2, 1, 0, 5,
            1, 0, 2, 1, 0, 5, 3, 1, 0, 4, 2, 1, 0, 5, 1, 0, 1
        };
        var eventTypes = new[]
        {
            "Etw:AppMessagePump/停止",
            "Etw:AppMessagePump/开始",
            "Etw:QueueEventMessage",
            "Etw:QueuePostMessage",
            "Etw:RetrievePostMessage",
            "Etw:RetrieveQueueEventMessage"
        };

        Assert.Equal(97, offsets.Length);
        Assert.Equal(offsets.Length, eventTypeIds.Length);

        try
        {
            var events = new List<DiagnosticEvent>
            {
                new(Guid.NewGuid(), "FocusGained", "DiagnosticSessionManager",
                    focusQpc, qpcFrequency, focusUtc, focusRelativeMs,
                    21948, null, null, "TestGame", null,
                    "Target process regained foreground.", sessionId)
            };

            for (var i = 0; i < offsets.Length; i++)
            {
                var offset = offsets[i];
                events.Add(new DiagnosticEvent(
                    Guid.NewGuid(),
                    eventTypes[eventTypeIds[i]],
                    "Microsoft-Windows-Win32k",
                    focusQpc + offset,
                    qpcFrequency,
                    focusUtc.AddTicks(offset),
                    focusRelativeMs + offset / 10_000d,
                    21948,
                    6672,
                    null,
                    "TestGame",
                    null,
                    "TimestampQuality=ETWQPC;PayloadOmittedByPrivacyPolicy",
                    sessionId));
            }

            // This record is outside the 1.5-second window and must not be counted.
            var outsideOffset = 16_000_000L;
            events.Add(new DiagnosticEvent(
                Guid.NewGuid(), "Etw:OutsideWindow", "Microsoft-Windows-Win32k",
                focusQpc + outsideOffset, qpcFrequency, focusUtc.AddTicks(outsideOffset),
                focusRelativeMs + outsideOffset / 10_000d,
                21948, 6672, null, "TestGame", null, "Outside interval", sessionId));

            // Reverse JSONL order so FocusGained appears after the ETW records.
            await File.WriteAllLinesAsync(jsonlPath,
                events.AsEnumerable().Reverse().Select(item => System.Text.Json.JsonSerializer.Serialize(item)));

            var report30Path = await SessionReportGenerator.WriteAsync(
                jsonlPath, maximumPostFocusEtwRows: 30);
            var report30 = await File.ReadAllTextAsync(report30Path);
            Assert.Contains("Win32k ETW events in interval: 97", report30);
            Assert.Contains("Win32k ETW events for target PID 21948: 97; other/unknown PID: 0", report30);
            Assert.Contains("ETW event detail limit reached: 67 additional events", report30);

            var report300Path = await SessionReportGenerator.WriteAsync(
                jsonlPath, maximumPostFocusEtwRows: 300);
            var report300 = await File.ReadAllTextAsync(report300Path);
            Assert.Contains("Win32k ETW events in interval: 97", report300);
            Assert.Contains("Win32k ETW events for target PID 21948: 97; other/unknown PID: 0", report300);
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
            for (var i = 0; i < 10000 && !writer.LimitReached; i++)
            {
                writer.TryWrite(CreateEvent(i + 1) with
                {
                    EventType = "HighVolumeTelemetry",
                    Details = detail
                });
                if (i % 10 == 0)
                    await Task.Delay(2);
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
