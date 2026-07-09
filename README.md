# Kiosk PresentMon Exporter

Frame-pacing telemetry for the Windows kiosk fleet → Prometheus → Grafana.

The signage players render fullscreen and we don't own their source, so we
measure at the OS present layer with the **Intel PresentMon 2.x Service** and
bridge its SDK to a Prometheus `/metrics` endpoint. Complements
`kiosk-ohm` (CPU/GPU temp, fan) and `windows_exporter` (host metrics) with the
one dimension they can't see: **how smoothly frames actually reach the screen.**

Exposes frame-time p99, dropped frames, and FPS (plus GPU power/temp/util for
free from the same query).

## How it works

```
PresentMon Service (LocalSystem, ETW capture + vendor telemetry)
        │  PresentMonAPI2.dll  (dynamic query: p99/avg over a sliding window)
        ▼
kiosk-presentmon-exporter (Windows service)  ──►  :9110/metrics  ──►  Prometheus ──► Grafana
```

The exporter uses the SDK's **dynamic query**, which computes the statistics
(`PERCENTILE_99`, `AVG`, …) over a configurable window inside the service. So we
never see per-frame data — each scrape is one poll that returns already-aggregated
numbers we drop straight into gauges. No client-side histograms, no CSV parsing.

Both pieces run as **Windows services**, outside the Assigned-Access kiosk
session, so true kiosk mode doesn't restrict them. The exporter finds the
signage process by name and re-tracks automatically when it restarts.

## Exported metrics

| Metric | Type | Meaning |
|---|---|---|
| `presentmon_up` | gauge | 1 = target tracked & presenting, 0 = missing/idle |
| `presentmon_cpu_frame_time_ms_p99` | gauge | 99th-pct app frame time (stutter) |
| `presentmon_cpu_frame_time_ms_avg` | gauge | avg app frame time |
| `presentmon_displayed_time_ms_p99` | gauge | 99th-pct on-screen frame interval |
| `presentmon_displayed_fps` | gauge | avg displayed FPS |
| `presentmon_presented_fps` | gauge | avg presented FPS |
| `presentmon_dropped_frames` | gauge | dropped frames (windowed) |
| `presentmon_gpu_power_watts` / `_temperature_celsius` / `_utilization_percent` | gauge | GPU telemetry |

All carry an `app` label. `instance`/host come from the Prometheus scrape config.

## Repository layout

| Path | What |
|---|---|
| `src/KioskPresentMonExporter/` | the exporter (.NET 8 Windows service) |
| `src/KioskPresentMonExporter/PresentMonInterop.cs` | P/Invoke to PresentMonAPI2.dll |
| `src/KioskPresentMonExporter/PresentMonSession.cs` | dynamic-query lifecycle |
| `grafana/` | dashboard JSON for the Git-Sync dashboards repo |
| `docs/PLAN.md` | architecture, state, and the verify-on-Windows checklist |

## Build & deploy (on a Windows box)

```powershell
# Self-contained single-file exe — kiosks need no .NET runtime installed
dotnet publish src/KioskPresentMonExporter -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 1. Install the PresentMon Service (ships PresentMonAPI2.dll). >= v2.3.1.
# 2. Copy PresentMonAPI2.dll next to the exe (or rely on the service's registration).
# 3. Register the exporter as an auto-start service:
sc.exe create KioskPresentMonExporter binPath= "C:\kiosk\kiosk-presentmon-exporter.exe" start= auto
sc.exe start KioskPresentMonExporter

# 4. Point Prometheus at http://<kiosk>:9110/metrics
```

Set `Exporter:TargetProcessName` in `appsettings.json` to the signage player's
process name (no `.exe`).

## Status

⚠️ **Scaffold — not yet built or run on hardware.** Written on macOS against the
published PresentMonAPI.h; the P/Invoke layer needs a compile + smoke test on a
Windows kiosk before release. See [docs/PLAN.md](docs/PLAN.md) for the exact
checklist (blob/swap-chain layout and GPU `deviceId` are the two things to verify).

Release via the fleet flow: `--prerelease` then promote; never pin.
