# Implementation Notes

> Historical record: the LiveCharts 0.9.7, EPPlus, WinForms DPI, and legacy ViewModel warnings mentioned below were resolved in the 2026-08-29 adversarial remediation. Keep the dated entries as history rather than current build status.

## 2026-06-10 Temperature Display Drift

Problem: Operators reported that after the upper computer has been connected for an unknown period, the displayed temperature no longer matches the actual PLC/HMI temperature. Disconnecting and reconnecting makes it normal again.

Initial finding: the UI shows the last value stored in `PlcStatus.Temperature` / `Device.CurrentTemperature`. If the 10s temperature polling path stalls while the PLC connection still looks online, the 5s monitor can keep the card refreshed and hide the fact that no fresh temperature sample has arrived.

Decision: add a dedicated temperature sample freshness signal and have the device manager mark the PLC connection failed when a connected/acquiring PLC has no fresh temperature sample for several temperature intervals. This reuses the existing auto-reconnect path instead of inventing a separate recovery loop.

Tradeoff: stale detection is intentionally conservative to avoid reconnecting during brief wireless jitter. It should catch frozen temperature reads while allowing normal 10s polling delays.

Verification: `dotnet build` passed with existing warnings only: LiveCharts/LiveCharts.Wpf NU1701 compatibility warnings, two CS4014 unawaited-call warnings, and WFAC010 high-DPI manifest warning.

## 2026-06-11 Code Review Fix Round

Scope: fix the issues found in the uncommitted-diff review (4 bugs, 4 behavior items, 4 minor items). Items intentionally NOT fixed are listed at the end with reasons.

### Bug fixes

1. **AutoExportService duplicate HTML header (+ sync IO on PLC event thread)** — `Append*` previously did synchronous `File.AppendAllText`-style IO directly on the PLC state-change thread, and tracked "header written" in an in-memory flag, so restarting the app on the same day wrote a second `<html><head>...` block into the daily file. Rewritten as a write-behind queue identical in pattern to LogBufferService: `ConcurrentQueue` + 3s `System.Timers.Timer` + `Interlocked` re-entry gate + `MaxQueueSize = 20000` FIFO drop. Header decision is now purely `!File.Exists(filePath)`, so same-day restarts append rows without a new header. Batches are grouped by `LogTime.Date` so a flush spanning midnight splits into the correct daily files. `Dispose()` stops the timer, waits up to 3s for an in-flight flush, then drains. Tradeoff: up to ~3s of export rows can be lost on hard crash — acceptable because SQLite (LogBufferService) is the authoritative store; HTML export is a convenience view.
2. **DeviceManagerService compare-after-assignment** — the monitor callback assigned `device.CurrentTemperature = snapshot.Temperature` and *then* compared them (always equal → `tempChanged` always false → `LastUpdateTime` refresh logic dead). Now computes `tempChanged` (epsilon 0.05f) before assigning.
3. **LogBufferService exit flush capped at one batch** — `Flush()` called `FlushCoreAsync()` once, writing at most `MaxBatchSize` (1000) rows; a backlog above that was silently dropped on exit, and a timer-driven flush in flight could interleave. Now: `FlushCoreAsync()` returns `Task<bool>` (false = DB not ready / write failed, logs already requeued); `Flush()` loops until both queues empty with a 5s `Environment.TickCount64` deadline, acquires the same `_isFlushing` gate as the timer path (sleep-50ms retry while held), and breaks early on a false result instead of spinning.
4. **AutoExportService disposal** — `DeviceManagerService.StopMonitoring()` now disposes `_autoExport` between LogBuffer and data-service disposal.

### Behavior changes

5. **Consecutive-failure offline tolerance (N=2)** — wireless-bridge packet loss caused a single failed read to immediately mark the device offline and fire DingTalk alerts. `HandleConnectionFailure` now increments `_consecutiveIoFailures` (Interlocked) and only goes offline at 2 consecutive failures. Two design hazards handled:
   - The stale-temperature watchdog (2026-06-10 fix) calls `HandleConnectionFailure` once per detection but already represents multiple elapsed periods → it passes `immediate: true` to bypass the counter, preserving the drift fix.
   - With tolerance, a tolerated failure no longer disconnects, so the polling cycle's already-read zeroed arrays (HslCommunication failure path returns all-false/0f) would be assigned to `PlcStatus` and fire fake OFF events. Each polling cycle snapshots `Volatile.Read(ref _consecutiveIoFailures)` before reading and discards the whole round if the counter moved (or `_isConnected` dropped) before assignment. Counter resets only after a fully successful round (and on connect).
   - Known acceptable interleaving: XY successes can keep resetting the counter while temperature reads fail repeatedly; the stale-temperature watchdog still force-disconnects after ~4 temperature intervals, so the failure cannot hide indefinitely.
6. **config.json excluded from publish** (`CopyToPublishDirectory="Never"`) — publishing no longer overwrites the site's config with the dev machine's. Safe because `AppConfig` auto-creates a default config.json on first run. The file stays git-tracked and stays `CopyToOutputDirectory=PreserveNewest` for local debugging (csproj `<None Include>` requires it to exist).
7. **UI exception storm advisory** — `DispatcherUnhandledException` sets `args.Handled = true`, which can mask a crash loop. App.xaml.cs now counts dispatcher exceptions in a 60s sliding window (lock + `Queue<DateTime>`); >10 in a minute shows a one-time (per process) restart-advisory MessageBox via `Dispatcher.BeginInvoke`.
8. **Settings-page tower-light test vs. main-service port conflict** — the serial port is exclusive; while DeviceManagerService holds it, the settings page's `new TowerLightService(port).TryConnect()` always failed and showed a false "打开失败". Added pass-throughs on DeviceManagerService (`TowerLightPortName`, `IsTowerLightSerialOpen`, `TestTowerLightAsync()` = Red→Yellow→Green→Off via the shared instance's `SendAsync`, 800ms apart, `ForceUpdateTowerLight()` in finally to restore the real light state). SettingsDialog routes scan-verification and both test buttons through these when `IsPortHeldByMainService(port)` matches (case-insensitive port compare); otherwise keeps the original new-instance path (covers the app-not-monitoring case). ManualTest result string keeps containing "成功" because the success/failure UI branch string-matches on it.

### Minor

9. Renamed `DiagnosisWindowSeconds` → `DiagnosisWindowSamples` (DeviceDetailViewModel) — the constant is a sample count (20 samples × 3s = 60s), not seconds.
10. `TowerLightService.Send()` comment updated — it is also used by the settings page background-thread tests, not only Dispose/TurnOff.
11. Log query status bar now appends "已达单次加载上限 5000 条…" when either result set hits its row cap, so truncation is visible.
12. `.gitignore` adds `publish123/`.

### Intentionally not fixed

- **Alarm semantics (threshold vs PLC target temp)** — whether the over-temp alarm should compare against the configured threshold or the PLC's own target register is a site/process decision; left as-is.
- **Reconnect log throttling** — reconnect chatter goes through `Debug.WriteLine`, compiled out in Release; no production impact.
- **CSV/HTML formula injection prefixing** — blanket `'`-prefixing would corrupt negative numbers in Excel exports; exported strings are internal config labels, not untrusted input. HTML export already encodes via `WebUtility.HtmlEncode`.

Verification: `dotnet build` could NOT be run by the agent this round (the agent's shell tool was unavailable — classifier outage, retried 5×). Static verification done instead: all edited regions re-read for syntax/brace balance; repo-wide grep confirms no stale `DiagnosisWindowSeconds` references in code; new DeviceManagerService members confirmed present and referenced consistently from SettingsDialog. **Run `dotnet build` before committing** — expected result is success with the same pre-existing warnings as 2026-06-10 (NU1701 ×2, CS4014 ×2, WFAC010).

## 2026-07-10 PLC Reliability and UI Freeze Repair

Problem evidence: `diagnostic-20260701.log` showed all four temperature polling paths becoming stale while the UI heartbeat remained healthy (`0.1–0.2s` lag). The stale watchdog then closed the PLC connection. This separated the communication stall from the independent detail-window rendering pressure.

Decisions and fixes:

1. Added a per-service connection generation and monotonic I/O failure version. Results from an older TCP connection are discarded after reconnect, and a failure cannot be hidden when another polling loop resets the consecutive-failure counter.
2. Serialized delayed `ConnectClose` with PLC I/O and made reconnect wait for the previous close. Delayed close tasks verify their generation before closing so they cannot close a newer connection. `SemaphoreSlim` instances are intentionally not disposed while timer callbacks may still be completing.
3. Reconnect now resets temperature freshness and immediately requests a temperature sample. Temperature freshness is committed only after the complete temperature/target/voltage/register round succeeds.
4. Automatic reconnect rechecks the user reconnect whitelist both before and after `ConnectAsync`, preventing an in-flight reconnect from undoing an explicit manual disconnect.
5. Reduced business M-point lists from 56 to 17 points for device 1 and from 181 to 20 points for device 3. Wide `MReadBlocks` remain for TCP efficiency; intermediate PLC bits no longer become UI items or operation logs.
6. `PlcPointPanel` filters before posting to the Dispatcher, coalesces X/Y/M updates, and unsubscribes when unloaded. LiveCharts animations are disabled, history replacement uses `AddRange`, stale overlapping history loads are ignored, and charts update only when a real PLC temperature sample arrives.
7. Removed generated random temperature history from the production detail page. Added `Device.HasTemperatureSample` so valid zero/negative temperatures display correctly without showing `0°C` before the first real sample.
8. Added persistent, throttled diagnostic records for slow PLC calls and communication failures, corrected OS-thread identification in freeze snapshots, serialized diagnostic-file writes, stopped swallowing non-cancellation Dispatcher exceptions, and added a 5-second SQLite busy timeout.

Tradeoffs:

- A connection close now waits behind the current bounded PLC read instead of racing the HSL client from another thread. With `ReceiveTimeOut = 2000`, reconnect may be delayed by the current request but avoids corrupting the shared connection object.
- LiveCharts remains version 0.9.7, so the existing `NU1701` compatibility warning remains. Animations were disabled to reduce risk, but a future chart-library migration is still advisable.
- No real PLC or wireless bridge was available for an end-to-end disconnect/reconnect test.

Verification:

- `dotnet build --no-restore`: success, 0 errors. The previous CS4014 warnings are gone.
- `dotnet build -c Release --no-restore`: success, 0 errors.
- Remaining warnings are pre-existing `NU1701` warnings for LiveCharts/LiveCharts.Wpf and `WFAC010` for manifest-based high-DPI configuration.
- `git diff --check`: clean after correcting existing trailing whitespace findings.

## 2026-08-06 Communication Freeze Recovery v1.1.0

Field symptom: the real process temperature could reach about 50°C while the WPF card remained at an older 20–30°C value. The process had to be fully exited before temperature display recovered.

Root cause: HslCommunication exposes synchronous PLC reads. A half-open TCP connection or wireless-bridge fault could leave one read blocked while holding the service-wide I/O path. The old close/reconnect flow then waited behind the same blocked path, while global polling flags could keep a replacement connection from starting its first automatic sample.

Reliability changes:

1. Each TCP connection generation now owns an independent transport and `SemaphoreSlim`. An application hard deadline abandons the exact generation; a new generation never waits for its old lock or client.
2. `Abort()` closes the HSL communication pipe without waiting for the old I/O lock. `Abort` and `ConnectClose` are best-effort background cleanup; late operations receive a terminal second close so a late `ConnectServer` cannot leave a ghost socket.
3. Every read and every commit validates the exact session generation. Each acquisition cycle also has a unique token, so an old XY/temperature/auxiliary task cannot block or clear the new cycle's single-flight state, including a same-connection Stop→Start.
4. The primary actual-temperature register is committed and published immediately after it succeeds. Target temperature, thermocouple voltage, and C/T/D diagnostic registers run on a separate auxiliary lane. Ordinary auxiliary failures are logged but do not disconnect a healthy primary-temperature connection; a genuinely hung socket still triggers the global hard timeout.
5. General, temperature, and auxiliary failure counters are separated per session. XY success cannot hide repeated temperature failure.
6. Empty/invalid primary payloads return `NaN` and are never published as 0°C. Zero and negative temperatures remain valid samples.
7. Temperature freshness uses a monotonic clock. New TCP sessions start with no current-generation sample, stale sessions are atomically invalidated, and connection-state events immediately mark the UI stale and start authorized reconnects.
8. The UI retains the last valid value for diagnosis but displays an explicit warning and stale/offline color. Dispatcher-delayed old events must still match the service's current sample timestamp before they can update the card. `LastUpdateTime` is derived only from a real temperature sample.

Verification:

- 15 xUnit regression tests pass, including permanently blocked read, hard timeout replacement, blocked Abort/Close, delayed Connect ghost cleanup, automatic resampling after reconnect, late old-generation isolation, same-connection Stop→Start isolation, repeated auxiliary failure, Word/DINT empty payloads, lane-counter isolation, timer lifecycle, and UI freshness behavior.
- The 14-test suite was also run five consecutive times before the final immediate-reconnect addition (70/70 passed).
- Debug and Release builds pass with 0 errors.
- Remaining warnings are the existing LiveCharts/LiveCharts.Wpf `NU1701` compatibility warning and `WFAC010` manifest high-DPI guidance.
- Kimi Code, GLM through Claude Code, Grok CLI, and AGY CLI performed read-only reviews. Their final focused verdicts reported no P0/P1 code issue for the stale-temperature/restart symptom and recommended release.
- No real FX3U PLC or wireless bridge was connected during this repair. Software proof is not field acceptance; use `docs/通信卡死修复与现场验收说明.md` for the read-only field check.

This section supersedes the 2026-07-10 tradeoff that reconnect should wait behind the current bounded read. A permanently blocked third-party call cannot be made safe by waiting; v1.1.0 instead abandons the whole connection generation and isolates its resources.

## 2026-08-30 Communication Stability Closure v1.2.0

This round executed the remaining adversarial-review plan against the dirty working tree and preserved the pre-existing user changes.

1. **Connection truth is explicit** — `PlcConnectionPhase` now distinguishes TCP connect, MC read-only protocol verification, first-sample wait, fresh/stale data, communication fault, backoff, stopping and disposal. The manager ignores stale-generation connection events, so a late old-session callback cannot schedule a new connection over a healthy session.
2. **Reconnect is bounded** — each device has an exponential backoff (5–60 seconds, ±20% jitter), a schedule version, a user reconnect whitelist and a process-wide reconnect gate. Cancellation and lifecycle shutdown invalidate pending schedules; a delayed task cannot return from `finally` or overwrite a newer schedule.
3. **Polling is single-flight and prioritized** — each device owns one scheduler with separate temperature, XY and auxiliary lanes. Temperature has priority; delayed temperature samples stretch XY cadence. A real temperature lane token prevents an old Stop→Start loop from overlapping a new temperature read. A process-wide semaphore caps synchronous Hsl/native calls, and a six-task detached-call breaker stops retry storms.
4. **Temperature values are evidence-backed** — actual and target registers have independent address/type/scale/range definitions. Empty or malformed payloads, non-finite/out-of-range values and excessive steps are rejected. Valid samples carry raw value, connection generation, monotonic sequence and quality. UI only shows a numeric value when the current generation is fresh; the last valid value and age are shown separately.
5. **Malformed I/O cannot shrink state** — X/Y/M payload lengths are checked before applying arrays or comparing point changes. The first valid I/O frame establishes a baseline and produces no synthetic operation events. Null array assignments are normalized in `PlcStatus`.
6. **UI/serial pressure is isolated** — TC60 Open/Write/Read/Sleep work runs on a background state pump with latest-state coalescing and retry. Device/detail scanline effects are static; chart animations remain disabled. Diagnostic writes are queued, rotated and aggregated for repeated slow/failing PLC calls; connection phases and temperature rejections are persisted to the same diagnostic stream.

Verification for this closure: `dotnet build` Debug and Release both pass with zero warnings/errors; xUnit passes 40/40 in both configurations; `git diff --check` is clean; package vulnerability/deprecation audits report none. A new self-contained single-file package is in `publish/MitsubishiMonitor-1.2.0-communication-stability-20260830/` and does not contain the developer `config.json`. Real FX3U, wireless bridge and USB tower-light acceptance remains a separate read-only field gate.

## 2026-08-30 Mixed-Power Device Policy v1.2.1

Field clarification: the four PLC-backed machines are independently and unpredictably powered. Any combination from all-off to one, several or all machines running is normal. Treating every configured PLC as continuously required online caused expected power-off states to look like communication failures and kept the reconnect/tower-light policy unnecessarily active.

Changes:

1. Added a persisted per-device `DeviceMonitoringMode`: `AutoStandby` (default), `RequiredOnline` and `Disabled`. Legacy configurations without the field are normalized to four `AutoStandby` entries.
2. `AutoStandby` uses low-frequency 30–60 second discovery and never turns a powered-off machine into `CommunicationFault` or an offline-banner entry. `RequiredOnline` retains the faster 5–60 second recovery and failure semantics. `Disabled` cancels pending reconnect generations and stops the service.
3. Added explicit `Standby` and `Disabled` UI states. Powered-off machines cannot display a historical temperature as current; their card remains `--.-°C` while retaining the separately labelled last valid value.
4. Tower-light input is now mode-aware. Offline auto-standby and disabled machines are excluded; an online auto-standby machine participates in freshness/alarm checks; required-online machines always participate.
5. Startup and the global button sequentially probe enabled machines. Expected auto-standby misses are reported as standby, not failed connections. The card's stop action is now a persisted disable; enabling a disabled card restores the safe default auto-standby mode.
6. Settings can atomically save all four modes alongside storage settings and apply them to the current manager. Runtime-apply failure is reported separately from successful durable configuration save.

Verification: Debug and Release builds pass with zero warnings/errors; xUnit passes 45/45 in both configurations, including one-on/three-off, all-off, required-offline, online-stale, alarm, policy-delay, legacy-config migration and invalid-mode cases. No live PLC, bridge or tower light was used; the mixed-power field matrix remains read-only acceptance work.

## 2026-08-30 Post-Review Reconnect Cadence Fix

Adversarial review found that the v1.2.1 reconnect backoff was applied twice after a failed attempt: the scheduler gate waited `delay(N+1)` before the next task could be scheduled, and that task then slept the same `delay(N+1)` again before calling `ConnectAsync`. The steady-state spacing between actual connect attempts was therefore about 120 seconds instead of the documented 30–60 seconds, so a powered-on auto-standby machine could wait roughly two minutes to be discovered in the worst case.

Fix: `ApplyReconnectOutcome` now opens the scheduler gate immediately after a failure. Backoff is applied exactly once, inside the next scheduled task's delay. First-retry semantics are unchanged (5 seconds for required-online, 30 seconds for auto-standby after a drop is detected), and steady-state intervals now equal the `DeviceMonitoringPolicy` delay plus at most one 5-second monitor tick. A regression test asserts that the gate opens immediately after failure, so the double application cannot be reintroduced silently.

Verification: Debug and Release builds pass with zero warnings; xUnit passes 46/46 in both configurations; `git diff --check` is clean.

## 2026-09-03 Startup and Legacy Config Fix v1.2.3

The 1.2.1 reconnect package crashed on `MainWindow` show: `Run.Text` defaults to TwoWay, and `Device.MonitoringModeDisplay` is a get-only display string. WPF raised `InvalidOperationException` and the process terminated.

Fix: every `<Run Text="{Binding ...}"/>` that lacked an explicit mode now uses `Mode=OneWay`, including `MonitoringModeDisplay`, collection `Count` properties, and other display-only values. Legacy configurations that contain the four PLC IPs but predate `DeviceThresholds` and `DeviceMonitoringModes` now migrate those missing fields to the established 90°C and `AutoStandby` defaults instead of disabling all PLC connections.

Verification: the release must pass the full automated suite plus a real process-level startup smoke test before packaging. The replacement package is `publish/MitsubishiMonitor-1.2.3-startup-config-fix-20260903/` (FileVersion `1.2.3.0`); developer `config.json` remains excluded from publish output.

## 2026-09-03 Click-Open Freeze Completion v1.2.4

The device card previously executed its modal detail command during `MouseLeftButtonDown`. That entered `ShowDialog()` before the matching mouse-up/input route had completed, so even a healthy detail window could leave the card in a half-finished input state and appear frozen. The card now records press state on preview-down and defers the command from mouse-up through the dispatcher. Embedded buttons remain excluded from the card command.

Window-open diagnostics now bracket device detail, settings and log-query creation. A dedicated `--ui-smoke` mode uses demo services and a per-process temporary database, actually shows and lays out `MainWindow`, `DeviceDetailWindow`, `SettingsDialog` and `LogQueryWindow`, then exits with code 0 only after all four have a live WPF presentation source. Demo/smoke command lines are detected before `AppConfig` loads, so a clean release directory no longer gains a production `config.json` as a side effect of validation.

Verification: Debug and Release builds pass with zero warnings/errors; xUnit v3 passes 52/52 in both configurations; the published single-file process passed all four real window loads and exited 0. The release directory remained free of `config.json`, `logs` and `Data` before and after the smoke run. No PLC, bridge or tower-light output was used. The package is `publish/MitsubishiMonitor-1.2.4-click-freeze-fix-20260903/` (FileVersion `1.2.4.0`).

## 2026-09-03 Generation-Bound Connection State Machine v1.2.5

The connection phase was previously a freely assigned enum beside `_isConnected`, `PlcStatus.IsConnected`, and a separately published snapshot. A valid temperature could finish its locked status update, lose a race to disconnect, and then publish an old-generation `OnlineFresh` phase outside the lock. Several enum values also belonged to manager-level reconnect policy or were never entered.

Fix: `PlcConnectionTransitionPolicy` is now the explicit transition table. Session generation, immutable connection snapshot, and the legacy `PlcStatus.IsConnected` projection commit under `_sessionSync`; snapshot identity suppresses superseded notifications. Temperature data and `OnlineFresh` commit together. Real and demo services use the same seven executable phases, while reconnect backoff remains owned by `DeviceManagerService`. `TemperatureSampled` is part of `IPlcService`, removing the manager's concrete-type subscription branch.

Verification: Debug and Release builds pass with zero warnings/errors; xUnit v3 passes 73/73 in both configurations and three repeated Release runs. Matrix tests exhaust every same-generation and next-generation phase pair. Deterministic re-entrancy tests disconnect from both temperature and online-property callbacks and prove that no old-generation usable snapshot is republished. The final single-file package is `publish/MitsubishiMonitor-1.2.5-state-machine-20260903/` (FileVersion `1.2.5.0`, SHA-256 `C048DD1F8432996C51884D64C8963B99A3454B166BBF29BF5BEC64CF74B014F8`). Its isolated UI smoke exited 0; the detail modal command completed in 425 ms and all four window chains in 1224 ms without creating production configuration or attempting PLC/tower-light I/O. This is software validation only; no PLC, bridge, or tower-light hardware was used.
