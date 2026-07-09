# presentmon-metrics

A Prometheus exporter for Windows frame-pacing telemetry, built on the **Intel
PresentMon 2.x Service**. It measures how smoothly frames actually reach the
screen — frame time, FPS, and dropped frames — for *any* fullscreen or
black-box Direct3D/OpenGL/Vulkan app, without instrumenting the app itself.

Point it at a process; it drains PresentMon's per-frame stream and exposes
Prometheus histograms + counters on a localhost `/metrics` endpoint.

> **Requires a real/local display.** RDP / virtual-display sessions don't emit
> the DXGI present events PresentMon captures — the exporter will read `up 0`.

## How it works

```
PresentMon Service (LocalSystem, ETW frame capture)
        │  PresentMonAPI2.dll  (frame query: per-frame stream)
        ▼
presentmon-metrics (Windows service)  ──►  localhost:9110/metrics  ──►  Prometheus ──► Grafana
```

The exporter drains the SDK's **frame query** and feeds every frame into
Prometheus **histograms + counters**. Because those are cumulative, per-minute
scraping loses nothing and Grafana reconstructs any quantile over any window at
query time — the sub-second/per-minute mismatch disappears.

Runs as a **Windows service**; the metrics endpoint binds **localhost**. Finds
the target process by name (or exact PID) and re-tracks automatically when it
restarts.

> **Fleet deployment:** the private `xzibit-pty-ltd/kiosk-presentmon` component
> installs and wires this on the kiosk fleet (downloads the release, installs the
> PresentMon Service, registers the Windows service, scrapes via kiosk-alloy).

## Exported metrics

| Metric | Type | Meaning |
|---|---|---|
| `presentmon_up` | gauge | 1 = target tracked & producing frames, 0 = missing/idle |
| `presentmon_displayed_time_ms` | histogram | on-screen interval per displayed frame (ms) — the smoothness signal |
| `presentmon_frame_time_ms` | histogram | CPU frame time per frame (ms) — diagnostic (CPU jitter vs display cadence) |
| `presentmon_displayed_fps_hist` | histogram | instantaneous displayed fps per frame, fps-bucketed — render as a heatmap |
| `presentmon_displayed_fps` | gauge | glanceable instantaneous fps (jittery; `rate()` of a histogram `_count` is authoritative) |
| `presentmon_frames_dropped_total` | counter | frames dropped (presented, never displayed) |

All carry an `app` label. `instance`/host come from the Prometheus scrape config.

Frame **counts** come free from the histogram `_count` fields — no separate
counters: presented = `presentmon_frame_time_ms_count`, displayed =
`presentmon_displayed_time_ms_count`. Only `dropped` needs its own counter.

### Grafana queries (any window, chosen at query time)

```promql
# Frame-time p99 over the last 5m — the stutter SLO
histogram_quantile(0.99, sum by (le, instance) (rate(presentmon_displayed_time_ms_bucket[5m])))

# Displayed FPS (avg = rate of the displayed-frame count) — and 1% low, the stutter signal
rate(presentmon_displayed_time_ms_count[1m])
1000 / histogram_quantile(0.99, sum by (le,instance) (rate(presentmon_displayed_time_ms_bucket[2m])))

# FPS distribution as a heatmap panel (format: heatmap, calculate: false)
sum by (le) (rate(presentmon_displayed_fps_hist_bucket[$__rate_interval]))

# Dropped frames per minute
increase(presentmon_frames_dropped_total[1m])

# Drop ratio (%) — denominator = presented = frame_time_ms_count
100 * rate(presentmon_frames_dropped_total[5m]) / rate(presentmon_frame_time_ms_count[5m])
```

## Demo in two minutes (no Windows box)

```bash
cd demo && docker compose up
# → http://localhost:3000/d/kiosk-presentmon   (anonymous admin)
```

Brings up Prometheus + Grafana with the dashboard provisioned, fed by a bundled
sample-data generator (`demo/frame_sim.py`) that emits the exporter's exact
metrics with realistic, moving frame data — so every panel populates
immediately. To drive it from a real exporter instead, edit
`demo/prometheus/prometheus.yml` (remove the `frame-sim` target, point at your
kiosk) and drop the `frame-sim` service.

## Repository layout

| Path | What |
|---|---|
| `src/KioskPresentMonExporter/` | the exporter (.NET 8 Windows service) |
| `src/KioskPresentMonExporter/PresentMonInterop.cs` | P/Invoke to PresentMonAPI2.dll |
| `src/KioskPresentMonExporter/PresentMonSession.cs` | frame-query drain + GPU dynamic query |
| `demo/` | self-contained Prometheus + Grafana + sample-data generator |
| `grafana/` | dashboard JSON for the Git-Sync dashboards repo |
| `docs/PLAN.md` | architecture, state, and the verify-on-Windows checklist |

## Build & deploy (on a Windows box)

```powershell
# Self-contained single-file exe — no .NET runtime needed on the target
dotnet publish src/KioskPresentMonExporter -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 1. Install the Intel PresentMon Service (ships PresentMonAPI2.dll), >= v2.3.1:
#      winget install --id Intel.PresentMon -e
# 2. Make PresentMonAPI2.dll resolvable — run with the service dir on PATH, e.g.
#      $env:PATH = "C:\Program Files\Intel\PresentMonSharedService;$env:PATH"
# 3. Register as an auto-start LocalSystem service:
sc.exe create presentmon-metrics binPath= "C:\path\presentmon-metrics.exe" start= auto obj= LocalSystem
sc.exe start presentmon-metrics

# 4. Point Prometheus at http://localhost:9110/metrics
```

Config via `appsettings.json` (or `Exporter__*` env vars): `TargetProcessName`
(no `.exe`) or `TargetProcessId`, `ListenPort`, `PollIntervalMs`.

## Status

**Validated on real hardware** — builds clean (.NET 8), the PresentMon interop
works, and live GPU frame capture flows through Prometheus → Grafana (verified
~60 fps, p99 frame time, fps heatmap). See [docs/PLAN.md](docs/PLAN.md).
