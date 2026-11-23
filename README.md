# WindowTrace

**WindowTrace** is a Windows 11 diagnostic monitor designed to investigate delayed input or presentation response after a game loses and regains foreground focus.

The project is being developed with:

- C# / .NET 10
- WPF
- Windows 11 x64
- Win32 APIs and QPC
- ETW real-time collection (planned)
- PresentMon as a separate process (planned)

## Development and target environments

The current development machine runs Windows 11 **26H2**. The affected machine runs Windows 11 **25H2**. The developer machine previously ran 25H2 without reproducing the issue.

WindowTrace must therefore record the exact OS edition, display version, build and revision at capture time. It must detect available ETW providers and event capabilities at runtime rather than assuming that all Windows installations expose identical providers, events or payload fields.

A successful run on the development machine is not evidence that the issue is absent or that the affected machine has the same configuration.

## Confirmed architecture

- Layered application architecture.
- ETW events are consumed in real time; capture must not begin only after focus returns.
- PresentMon runs as a separate process.
- QPC is the primary high-resolution event clock.
- A diagnostic session retains at least 3 seconds before focus loss, the background interval, and 5 seconds after focus returns.
- Missing or unsupported telemetry is represented as unavailable, never fabricated as zero.
- Correlation is not causation: WindowTrace reports observations and timing relationships, not unsupported root-cause claims.

## Safety and scope

- No kernel driver.
- No game injection.
- No global keyboard logger.
- No recording of key characters or key contents.
- Input markers, if enabled later, must be explicit and must disclose their measurement limitations.
- No automatic BIOS, registry, driver or system configuration changes.
- Monitoring must remain useful when an optional collector fails.

## Current implementation status

The first implementation milestone focuses on the WPF shell, QPC timestamping, foreground-window monitoring, a bounded event buffer and diagnostic event persistence.

ETW deep tracing, CPU/GPU telemetry and PresentMon integration are not considered implemented until their collectors, capability checks, error reporting and validation are added.

## Build

The application targets `.NET 10` and `net10.0-windows`. Build on Windows with the .NET 10 SDK:

```powershell
dotnet restore .\src\WindowTrace.App\WindowTrace.App.csproj
dotnet build .\src\WindowTrace.App\WindowTrace.App.csproj -c Debug
dotnet run --project .\src\WindowTrace.App\WindowTrace.App.csproj
```

## Project status

Early development. Do not use diagnostic output as proof of a root cause until the relevant data sources and their quality have been validated on the affected machine.
