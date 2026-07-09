# Kiosk PresentMon Exporter — Architecture & Plan

Frame-pacing telemetry for the Windows kiosk fleet. The signage players are
fullscreen black boxes, so app-level instrumentation is off the table and DWM
composition timing is bypassed by fullscreen/flip — leaving the OS present layer
(ETW, via the PresentMon Service) as the correct measurement point.

## Why this shape (decisions locked)

- **Service, not the CLI.** The PresentMon 2.x Service captures ETW centrally and
  exposes the SDK streaming API. Fullscreen rules out DWM; the CLI/CSV path would
  mean parsing a per-frame firehose and shelling out to a process.
- **Frame query, not dynamic query.** We drain the per-frame stream and feed
  every frame into cumulative Prometheus histograms + counters. This defeats the
  sub-second-data / per-minute-scrape mismatch: nothing is lost between scrapes,
  and Grafana computes any quantile over any window at query time. (GPU telemetry
  still rides a small dynamic query → gauges; it's slow-moving so a snapshot is
  fine, and it auto-disables on a GPU-less box.)
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
| SDK API surface pinned against published PresentMonAPI.h | ✅ (dynamic + frame query, structs, enum values) |
| P/Invoke layer (`PresentMonInterop.cs`) | ✅ written — ⬜ unverified on Windows |
| Frame-query drain + GPU dynamic query (`PresentMonSession.cs`) | ✅ written — ⬜ unverified |
| Exporter host: histograms + counters + poll loop (`Program.cs`) | ✅ written |
| Compile + self-contained publish on a Windows dev box | ✅ builds clean (net8 8.0.422); fixed 2 bugs |
| Interop/runtime verified: pmOpenSession/RegisterFrameQuery/GPU query, /metrics 200 | ✅ on dev box |
| Grafana pipeline (Prometheus scrape → dashboard → PromQL) | ✅ validated via `demo/` + sample-data generator |
| Real frame capture (histogram/counter values) | ⬜ **blocked**: dev box is Parsec/virtual-display → no capturable presents. Needs a real-display target (kiosk VM, creds pending) |
| Grafana dashboard | 🟡 skeleton in `grafana/` |
| Prometheus scrape config + fleet rollout | ⬜ pending |
| Release (`--prerelease` → promote) | ⬜ pending |

## Verify-on-Windows checklist (must do before release)

1. **Compile.** `dotnet publish` on Windows; resolve any P/Invoke marshalling
   warnings. macOS can't build `net8.0-windows`.
2. **Frame blob layout.** Confirm `pmConsumeFrames` writes frames back-to-back at
   `_frameStride` bytes each, with each element at `frameIndex*stride + dataOffset`,
   and that `BitConverter.ToDouble` there yields sane frame times. Cross-check
   against the SDK's FrameQuerySample. This is the #1 thing to validate. Also
   confirm `DROPPED_FRAMES` is 0/1 per frame (we threshold at ≥0.5).
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

## Findings from dev-box bring-up (2026-07-09)

- **PresentMon can't capture presents in a remote/virtual-display session.** On
  the dev box (accessed via Parsec + a Virtual Display Adapter), the official
  PresentMon console captured **zero frames system-wide** even with a WebGL app
  actively GPU-rendering — the "present to screen" goes out the remote-capture
  path, not a DXGI flip ETW sees. **Implication for the fleet: kiosks must present
  to a real/local display.** Confirm the production kiosks aren't rendered through
  any remote/virtual-display layer, or this captures nothing.
- **GPU telemetry needs a real adapter deviceId.** The box has an NVIDIA A2000
  yet GPU metrics registered as unavailable with `deviceId 0` — confirming the
  introspection-API follow-up (resolve the adapter id) rather than hardcoding 0.

## Resolved

- **Timescale mismatch (sub-second frames vs per-minute scrape).** Solved by
  cumulative histograms + counters (frame query). No sampling gap; Grafana picks
  the window at query time via `histogram_quantile(rate(..._bucket[5m]))`.
- **Dropped frames.** Now a true monotonic counter (`..._dropped_total`) → exact
  `increase()`/`rate()`, no under-counting between scrapes.

## Open questions

- **Which frame-time metric is the headline?** `presentmon_frame_time_ms`
  (CPU/app pacing) vs `presentmon_displayed_time_ms` (true on-screen cadence).
  Both histograms are exported; for "does the screen look smooth,"
  `displayed_time` p99 is the better SLO. Confirm on real signage data.
- **Histogram bucket tuning.** Current `le` set targets 60/30 Hz. If players run
  at other refreshes, re-tune so a bucket edge sits just past the target frame
  time (otherwise p99 snaps to a coarse bucket).
- **Drain cadence vs service buffer.** We drain every `PollIntervalMs` (1s),
  `FrameBatchSize` frames per `pmConsumeFrames` call, looping until empty. Confirm
  the service's internal frame buffer doesn't overflow at 1s under 60fps
  (≈60 frames/s ≪ 512 batch, drained fully each cycle — should be safe).

## Alerting ideas (Grafana / dashboards repo)

- `presentmon_up == 0 for 5m` → player not presenting (crash / frozen output).
- `presentmon_displayed_time_ms_p99 > 33` (i.e. worse than ~30fps) sustained → stutter.
- `rate`/delta on `presentmon_dropped_frames` spiking → dropped-frame events.
