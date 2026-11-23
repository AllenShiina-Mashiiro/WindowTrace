# WindowTrace

WindowTrace is a Windows 11 diagnostic monitor for investigating delayed game response after a game loses and regains foreground focus.

It is designed to collect timestamped observations across window focus, CPU scheduling, GPU counters, ETW and frame presentation. It does **not** assume that a correlated event is the root cause.

## Confirmed architecture

- Layered C# / .NET 10 WPF application.
- ETW real-time collection through Microsoft.Diagnostics.Tracing.TraceEvent.
- PresentMon launched as a separate process.
- QPC timestamps as the primary event clock, anchored to `GetSystemTimePreciseAsFileTime` for UTC conversion.
- Focus-triggered diagnostic sessions with a 3-second pre-roll and 5-second post-focus capture.
- Bounded in-memory history and a per-session JSONL size limit.
- JSONL as the authoritative event stream; CSV as a flattened export.
- Runtime detection of ETW providers and GPU performance counter availability.

## Development and target environments

The current development computer runs Windows 11 **26H2**. The affected computer runs Windows 11 **25H2**. The development computer previously ran 25H2 without reproducing the issue.

Every session records Windows version/build, DisplayVersion, UBR (when readable), edition, .NET runtime, process architecture, QPC frequency, CPU model, logical processor count, installed memory, BIOS/board identifiers and display-adapter driver metadata when available. ETW providers and GPU counters are checked on the machine at runtime. A successful run on the development computer does not establish that the affected computer has the same provider, driver, GPU or system configuration.

## Current implementation

### Implemented in the development branch

- WPF monitoring interface.
- Foreground-window changes through `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, plus minimize/restore events through `EVENT_SYSTEM_MINIMIZESTART` and `EVENT_SYSTEM_MINIMIZEEND`, and separate `EVENT_OBJECT_FOCUS` metadata events. Object focus is not treated as proof that the game's input thread has resumed.
- HWND, PID, TID, process name, window title and minimized state; `GetGUIThreadInfo` focused child HWND and GUI flags are recorded separately from the foreground HWND.
- QPC timestamp and UTC conversion.
- Target-process selection from the current foreground window.
- Diagnostic session state transitions:
  - target loses foreground: session starts;
  - previous 3 seconds are copied from the rolling event buffer;
  - background events continue to be written;
  - target regains foreground: capture continues for 5 seconds;
  - leaving again during post-capture returns the session to background capture.
- Real-time ETW consumer for kernel Process, Thread, ContextSwitch and Dispatcher events, plus runtime provider checks for Win32k, DWM, DxgKrnl, DXGI, Kernel-Process and Kernel-Power. ETW is system-wide; the captured trace can include events from other processes.
- 1-second system CPU and target-process CPU sampling.
- Optional PDH GPU Engine utilization and GPU memory counters, where the local Windows counter set supports them.
- Optional PresentMon 2.x child process, with per-swap-chain rolling FPS, average/p95/min/max frame time, frame-time variance and standard deviation derived from frame intervals. Per-frame GPU Busy/Wait and Present Mode fields are preserved. The process is filtered to the selected PID and started with `--qpc_time`, `--output_stdout`, `--no_console_stats` and `--no_track_input`.
- Optional timestamp-only input marker. It is off by default and registers only Ctrl+Alt+F12 when explicitly enabled.
- JSONL event storage, session environment metadata, session quality report and CSV export.
- Bounded event queue, buffer eviction accounting and a 256 MiB per-session JSONL file cap.
- Core unit tests and Windows GitHub Actions build/test workflow.

### Not yet claimed as fully implemented or validated

- Complete reconstruction of every target thread's Running/Ready/Waiting state and wait reason.
- Complete GPU scheduler queue reconstruction and driver-independent GPU Busy metrics.
- Guaranteed availability of all PresentMon frame metrics on every GPU/driver/present path.
- End-to-end input-to-game-response latency measurement.
- Validation of ETW/PresentMon coexistence, event-loss rates and resource impact on both the 25H2 affected computer and the 26H2 development computer.
- A validated root-cause diagnosis for the reported 1.5-second delay.

Missing data is not filled with zero. A provider or counter that is not available must remain unavailable and be reported as such.

## How to use the current development build

1. Run WindowTrace on the computer where the game will be tested.
2. Optionally select the official PresentMon 2.x executable using **Browse PresentMon.exe**.
3. Click **Start monitoring**.
4. Click **Pick game in 3 seconds**, then immediately switch to the game. WindowTrace captures the foreground process after the countdown.
5. Wait at least 3 seconds for target CPU/GPU, ETW and optional PresentMon collectors to warm up.
6. Switch away from the target game, then return to it.
7. Wait at least 5 seconds after returning. The diagnostic session should then be finalized automatically.
8. Use **Export last session CSV** if a spreadsheet-friendly copy is needed.

Starting monitoring before picking the target is recommended. If a target was selected before monitoring starts and is no longer foreground, bring it back to the foreground once after starting monitoring; the next focus loss will trigger the diagnostic session.

### Optional input timestamp marker

The checkbox is off by default. When enabled, WindowTrace registers Ctrl+Alt+F12 as a system hotkey and consumes that exact chord as a timestamp marker. It does not inspect other keyboard events and does not record key contents.

**This marker is not a measurement of a normal key reaching the game.** The registered chord is reserved and may not be delivered to the game. It is only a manual time marker for correlating an operator action with later frame/ETW observations.

### ETW permissions

Kernel scheduling ETW may require an elevated process or suitable system privileges. WindowTrace does not elevate itself automatically. If kernel ETW cannot start, the application reports the failure and continues with any other collectors that are available. Do not stop or reconfigure unrelated ETW sessions to make WindowTrace start.

## Session files

Files are written under:

```text
%LOCALAPPDATA%\WindowTrace\Sessions
```

A completed session normally produces:

- `windowtrace-session-<timestamp>.jsonl` — event stream.
- `windowtrace-session-<timestamp>.session.json` — OS/runtime/target metadata.
- `windowtrace-session-<timestamp>.quality.json` — pre-roll coverage, buffer evictions, ETW event-loss count, queue drops, file cap and writer errors.
- Optional CSV export chosen by the user.

The JSONL event stream preserves source and QPC fields. Event arrival order can differ between independent collectors; sort or correlate by `QpcTimestamp`, not by file line order.

## Build and test

Requirements: Windows x64 and the .NET 10 SDK.

```powershell
dotnet restore .\src\WindowTrace.App\WindowTrace.App.csproj
dotnet build .\src\WindowTrace.App\WindowTrace.App.csproj -c Release
dotnet restore .\tests\WindowTrace.Tests\WindowTrace.Tests.csproj
dotnet test .\tests\WindowTrace.Tests\WindowTrace.Tests.csproj -c Release
dotnet run --project .\src\WindowTrace.App\WindowTrace.App.csproj
```

The Windows GitHub Actions workflow also publishes a self-contained `win-x64` ZIP artifact named `WindowTrace-win-x64` after build and tests pass.

PresentMon 2.x is an optional external dependency and is not bundled in this repository. Obtain it from the official [PresentMon project](https://github.com/GameTechDev/PresentMon).

## Permissions

PresentMon real-time tracing can fail with access denied unless the current user has the required performance-tracing permissions or the application is run elevated. WindowTrace does not automatically request elevation, modify local group membership, or change system security settings. If PresentMon cannot start, its error is recorded and the other collectors continue where possible.

## Safety and diagnostic limitations

- No kernel driver.
- No game injection or process memory reading.
- No global keyboard logger.
- No key-character or key-content recording. Win32k ETW event payloads are deliberately omitted; only event timing and metadata are retained. Process-start payloads are also omitted to avoid persisting command lines.
- No automatic BIOS, registry, driver or Windows configuration changes. Registry access is read-only and limited to OS, CPU, BIOS and display-adapter inventory metadata.
- No automatic elevation.
- Monitoring continues when optional collectors fail.
- CPU percentages, GPU engine counters, ETW payloads and PresentMon metrics represent different measurements and must not be treated as interchangeable.
- Foreground change does not prove that the game's internal input thread has resumed.
- A timestamp marker does not prove that a key was delivered to the game.
- ETW provider payloads and PresentMon fields vary with Windows build, driver, GPU and capture path. Win32k payloads are intentionally redacted, so this source contributes timing/metadata rather than raw input details.

## Project status

Active development on `dev/mvp-01-window-monitor`. The code and tests must pass Windows CI and still require runtime validation on both the 26H2 development computer and the 25H2 affected computer before a release can be considered complete.
