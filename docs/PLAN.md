# Kiosk PresentMon Exporter — Architecture & Plan

Frame-pacing telemetry for the Windows kiosk fleet. The signage players are
fullscreen black boxes, so app-level instrumentation is off the table and DWM
composition timing is bypassed by fullscreen/flip — leaving the OS present layer
(ETW, via the PresentMon Service) as the correct measurement point.

## Why this shape (decisions locked)

- **Service, not the CLI.** The PresentMon 2.x Service captures ETW centrally and
  exposes the SDK streaming API. Fullscreen rules out DWM; the CLI/CSV path would
  mean parsing a per-frame firehose and shelling out to a process.
- **Dynamic query, not frame query.** The SDK computes `PERCENTILE_99`/`AVG` over
  a sliding window server-side. One poll per scrape → gauges. No client-side
  aggregation, no histograms.
- **C# / .NET 8**, matching `kiosk-ohm`. P/Invoke to `PresentMonAPI2.dll`,
  `prometheus-net` for exposition, `Microsoft.Extensions.Hosting.WindowsServices`
  for service hosting. Published self-contained so kiosks need no runtime.
- **Two Windows services** (PresentMon Service as LocalSystem + this exporter),
  both outside the Assigned-Access session, so true kiosk mode is a non-issue.
- **Process-by-name discovery**, self-healing on player restart (re-`StartTracking`
  when the pid changes).

## Status

| Piece | State |
|---|---|
| SDK API surface pinned against published PresentMonAPI.h | ✅ (signatures, structs, enum values) |
| P/Invoke layer (`PresentMonInterop.cs`) | ✅ written — ⬜ unverified on Windows |
| Dynamic-query lifecycle (`PresentMonSession.cs`) | ✅ written — ⬜ unverified |
| Exporter host + gauges + poll loop (`Program.cs`) | ✅ written |
| Compile + smoke test on a kiosk | ⬜ **next** |
| Grafana dashboard | 🟡 skeleton in `grafana/` |
| Prometheus scrape config + fleet rollout | ⬜ pending |
| Release (`--prerelease` → promote) | ⬜ pending |

## Verify-on-Windows checklist (must do before release)

1. **Compile.** `dotnet publish` on Windows; resolve any P/Invoke marshalling
   warnings. macOS can't build `net8.0-windows`.
2. **Blob / swap-chain layout.** Confirm `pmPollDynamicQuery` fills the blob as
   `[swapchain][element@dataOffset]` and that reading `BitConverter.ToDouble` at
   each `element.dataOffset` for swap-chain 0 yields sane numbers. Cross-check
   against the SDK's DynamicQuerySample. This is the #1 thing to validate.
3. **GPU `deviceId`.** Frame metrics use `deviceId 0`. GPU power/temp/util may
   need the real adapter id from the introspection API (`pmGetIntrospectionRoot`).
   If those three read 0/NaN, resolve and set the id; frame metrics are unaffected.
4. **Window vs poll interval.** Default `WindowSizeMs=1000`, `PollIntervalMs=1000`.
   Confirm p99 is stable and not starved at 60 Hz (≈60 samples/window).
5. **PM_STATUS codes.** We only branch on SUCCESS; log the raw code on failure to
   distinguish "service not running" from "process not tracked yet".
6. **Loader vs direct DLL.** Decide `PresentMonAPI2.dll` (service-registered) vs
   `PresentMonAPI2Loader.dll` (shim). Direct DLL assumed; switch the const in
   `PresentMonInterop.cs` if the loader is cleaner for packaging.

## Open questions

- **Which frame-time metric is the headline?** `CPU_FRAME_TIME` (app pacing) vs
  `DISPLAYED_TIME` (true on-screen cadence). Both are exported; pick the primary
  panel after seeing real data. For "does the screen look smooth," `DISPLAYED_TIME`
  p99 is likely the better SLO.
- **Dropped-frames semantics.** Exported as a windowed gauge, not a monotonic
  counter (the dynamic query is windowed). Good enough for alerting on drop
  spikes; revisit if you want a true cumulative counter (would need a frame query).
- **Scrape interval.** Gauges reflect the last `WindowSizeMs`. A 15–30s Prometheus
  scrape under-samples a 1s window (you see one window in fifteen). Either widen
  `WindowSizeMs` toward the scrape interval, or accept sampled snapshots. Decide
  at rollout.

## Alerting ideas (Grafana / dashboards repo)

- `presentmon_up == 0 for 5m` → player not presenting (crash / frozen output).
- `presentmon_displayed_time_ms_p99 > 33` (i.e. worse than ~30fps) sustained → stutter.
- `rate`/delta on `presentmon_dropped_frames` spiking → dropped-frame events.
