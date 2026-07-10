# Implementation Notes

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
