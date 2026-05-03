# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build (Debug)
dotnet build

# Build (Release)
dotnet build -c Release

# Publish as single exe (self-contained, no .NET runtime needed)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "./publish/"

# Run
dotnet run
```

No test projects exist. No linter configured.

## Architecture

MVVM pattern, .NET 8 WPF app for monitoring 4 Mitsubishi FX3U PLCs (via FX3U-ENET-ADP Ethernet modules) through wireless bridges.

### Data Flow

```
PLC (MC Protocol 1E Frame)
  → HslCommunication MelsecA1ENet (TCP)
    → MitsubishiPlcService (per-device polling)
      → DeviceManagerService (orchestrates 4 devices)
        → DeviceListViewModel / DeviceDetailViewModel
          → MainWindow / DeviceDetailWindow
```

### Key Files

- **`Models/PlcConfig.cs`** — Per-device PLC configuration: IP, addresses, I/O labels, M-read blocks, polling intervals. Device 1 and 3 have hardcoded custom configs in `DeviceManagerService.CreateDevice1Config()` / `CreateDevice3Config()`. Other devices use default `PlcConfig`.
- **`Services/MitsubishiPlcService.cs`** — Core PLC communication. Two timers: XY interval (1s) for I/O polling, Temperature interval (10s). Compares previous/current values to detect state changes → fires `StateChanged` event. Mitsubishi octal addressing handled by `PlcConfig.GetXAddress()`/`GetYAddress()`.
- **`Services/DeviceManagerService.cs`** — Creates 4 `Device` + `MitsubishiPlcService` pairs. Subscribes to state changes → queues operation logs via `LogBufferService`. 5s monitor timer detects offline/online transitions → DingTalk alerts.
- **`Services/LogBufferService.cs`** — Batch-writes logs to SQLite via `ConcurrentQueue` every 3 seconds. Prevents DB contention from high-frequency state changes.
- **`Data/MonitorDbContext.cs`** — EF Core SQLite context. Tables: `TemperatureLogs`, `OperationLogs`. Auto-cleanup: records older than 15 days deleted hourly.

### Network Setup (Wireless Bridge)

4 PLCs connected via wireless bridges (1 master + 4 slaves, iPoll 3 point-to-multipoint). Bridges are Layer 2 transparent — PLC IPs unchanged.

- PLCs: `192.168.1.5`, `.10`, `.15`, `.20` (port 5000)
- IPC primary IP: `192.168.1.100` (PLC communication)
- IPC secondary IP: `192.168.2.65` (bridge management)
- Bridge master: `192.168.2.66`, slaves: `.67`–`.70`

### UI Structure

- **MainWindow** — Dashboard with 4 `DeviceCard` controls showing online status, temperature, alerts
- **DeviceDetailWindow** — Per-device detail: LiveCharts temperature graphs, I/O point panels, operation logs, SSR diagnostics, process stages, Excel export
- Dark theme: background `#0A0E17`, accent `#00D4FF`, custom title bar

### Tech Stack

| Component | Library |
|-----------|---------|
| MVVM | CommunityToolkit.Mvvm 8.4 (source generators) |
| PLC Communication | HslCommunication 12.3 (MelsecA1ENet) |
| Charts | LiveCharts.Wpf 0.9.7 |
| Database | EF Core 8 + SQLite |
| Excel Export | EPPlus 7.5 |
| Alerts | DingTalk robot webhooks |

### Important Patterns

- **Mitsubishi octal addressing** — X/Y points use octal numbering: index 0-7 → X0-X7, index 8+ → X10+ (no X8/X9). Handled by `PlcConfig.GetXAddress()`.
- **`PlcConfig.MReadBlocks`** — M points are scattered (e.g., M1-M6 then M102-M103), read as separate contiguous blocks then merged into a single `bool[]`.
- **LogBufferService write-behind** — `ConcurrentQueue` + 3-second batch flush prevents SQLite write contention from 4 PLCs' high-frequency state changes. `Dispose()` synchronously flushes remaining entries.
- **SSR fault detection** — Heuristic in `MitsubishiPlcService`: if avg thermocouple voltage > 0.1V, PID output (Y17) is off, and temp exceeds target + 5, flags `IsSsrFault`.
- **UI event throttling** — `DeviceDetailViewModel.OnPlcStateChanged()` buffers events in `_pendingLogs`, flushes to UI every 500ms.
- **PlcStatus manual INPC** — Implements `INotifyPropertyChanged` directly (not CommunityToolkit) because setting the X/Y/M arrays fires individual property notifications (X0, X1, ..., Y0, ...) for per-point WPF binding.
- **Dual ViewModels** — `MainViewModel` is legacy single-device code; active app uses `DeviceListViewModel` (multi-device). Both exist in the codebase.
- No DI container — `Microsoft.Extensions.DependencyInjection` is in csproj but unused. All services manually constructed in constructors.
- All device config is hardcoded (no appsettings.json). Device 1/3 have custom configs via `CreateDevice1Config()`/`CreateDevice3Config()` in `DeviceManagerService`.
- `nullable disable` — project-wide nullable is disabled. Don't add nullable reference type annotations.

### Namespace

All code uses `MitsubishiMonitor.Demo.*` namespaces (Models, Services, ViewModels, Data, etc.).
