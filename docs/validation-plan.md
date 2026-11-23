# WindowTrace validation plan

This plan is required before declaring a diagnostic build ready for use on the affected computer.

## Environment comparison

Run the same WindowTrace build on both computers and preserve the generated session metadata.

| Item | Development computer | Affected computer |
|---|---|---|
| Windows display version | 26H2 | 25H2 |
| OS build + UBR | Record from session metadata | Record from session metadata |
| Windows edition | Record | Record |
| .NET runtime | Record | Record |
| CPU model / logical processors | Record separately | Record separately |
| GPU model and driver version | Record separately | Record separately |
| Display topology / refresh rate | Record separately | Record separately |
| Game version and graphics API | Record separately | Record separately |
| PresentMon version | Record separately | Record separately |

Do not treat the OS display version as the only variable. The development computer previously ran 25H2 without reproducing the issue, so hardware, drivers, firmware, game configuration and background software remain possible differences.

## Functional validation

1. Start WindowTrace and select the game while it is foreground.
2. Confirm that the selected PID and process name are correct.
3. Confirm the ETW provider capability entries are written to the rolling buffer.
4. Switch away from the game and confirm a session starts.
5. Keep the game in the background for at least 10 seconds.
6. Return to the game and wait 5 seconds.
7. Confirm the session finalizes and produces JSONL, session metadata and quality JSON files.
8. Repeat at least 20 focus-loss/focus-gain cycles.
9. Export one session to CSV and verify that commas, quotes and Unicode titles survive export.

## Session timing acceptance

Inspect QPC values, not only wall-clock display strings.

- The first session marker must be near the target focus-loss event.
- Pre-roll coverage should be close to 3000 ms. Any shortfall must be visible in the quality report.
- Background events must continue to be recorded.
- The completion marker should be approximately 5000 ms after the most recent target focus-gain event.
- If the target leaves focus again during post-capture, the post-capture timer must be reset and the session must continue.
- Events from different collectors must be correlated by QPC timestamp; JSONL line order is not guaranteed to be global event-time order.

## Collector validation

### Window monitor
- Test Win+Tab, Alt+Tab, minimize/restore and returning to the game.
- Confirm HWND, PID, TID, process name and window title are plausible.
- Compare foreground HWND with the focused child HWND reported by GetGUIThreadInfo; unavailable focus data must remain unavailable.
- Confirm ObjectFocusChanged, minimize and restore events are distinguishable from foreground changes.
- Confirm that a foreground change or child-focus event is not interpreted as proof that the game's input thread has resumed.

### ETW
- Record provider status for each machine.
- Test once as a standard user and, only if policy permits, once from an already elevated terminal.
- Do not change permissions or stop unrelated ETW sessions.
- Record EventsLost counters and inspect quality warnings.
- Confirm that the collector shuts down cleanly and does not leave a WindowTrace ETW session behind.
- Compare available providers and event names between 25H2 and 26H2.

### CPU
- Compare process CPU percentage with Task Manager over a sustained interval.
- Remember that WindowTrace reports process CPU normalized to total logical-processor capacity.
- Confirm sampling stops when the target is cleared or monitoring stops.

### GPU / PDH
- Record which GPU counter groups are available.
- Check engine instance names and PID filtering.
- Verify units: engine utilization is percent; memory counters are bytes.
- Treat unavailable counters as unavailable, not as zero.

### PresentMon
- Use a supported official PresentMon build.
- Confirm the CSV header is recognized and frame rows are received.
- Confirm the QPC field is present when the installed build supports `--qpc_time`.
- If QPC cannot be parsed, verify the event is marked with receipt-time quality instead of claiming a frame-start timestamp.
- Confirm `--no_track_input` is used.
- Test whether PresentMon and WindowTrace ETW sessions coexist on both computers.

### Input marker
- Leave the marker disabled unless it is needed.
- If enabled, verify that only Ctrl+Alt+F12 creates an InputTestMarker event.
- Confirm no other key events or key contents appear in logs.
- Remember the registered chord is consumed and does not measure ordinary game-key delivery.

## Resource and reliability validation

- Measure idle and active CPU/memory overhead with WindowTrace closed and open.
- Observe the event buffer eviction count and ETW EventsLost counter.
- Verify the JSONL session file never exceeds its configured 256 MiB cap.
- Verify a size cap or queue overflow appears in the quality report.
- Test closing WindowTrace during background capture.
- Confirm all child processes and ETW sessions are stopped on normal shutdown.

## Diagnostic interpretation

Do not claim a root cause from a single spike, warning, FPS drop or correlation. A useful report should identify the first measurable point where timing diverges, distinguish input marker time from game input handling, and show whether CPU scheduling, GPU execution, presentation or window/focus events changed around the same QPC interval.


## Manual input-response marker test

When the marker is enabled, press Ctrl+Alt+F12 immediately before the test key, then press Ctrl+Alt+F12 again as soon as the game visibly responds. Each pair is reported as a marker-to-response interval. This is an approximate user-observed interval that includes human timing, not an exact measurement of keyboard hardware or game input-processing latency. The registered chord is consumed and is not sent to the game.
