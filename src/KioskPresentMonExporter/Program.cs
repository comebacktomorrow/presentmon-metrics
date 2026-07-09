using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;
using Prometheus;

namespace KioskPresentMonExporter;

public sealed class ExporterOptions
{
    // Process to measure, WITHOUT the .exe suffix (e.g. "SignagePlayer").
    public string TargetProcessName { get; set; } = "";
    public int ListenPort { get; set; } = 9110;
    public double WindowSizeMs { get; set; } = 1000;   // stat window for the dynamic query
    public double MetricOffsetMs { get; set; } = 0;
    public int PollIntervalMs { get; set; } = 1000;    // how often we refresh the gauges
}

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "KioskPresentMonExporter");
        builder.Services.Configure<ExporterOptions>(builder.Configuration.GetSection("Exporter"));
        builder.Services.AddHostedService<PollerService>();
        builder.Build().Run();
    }
}

internal sealed class PollerService : BackgroundService
{
    private readonly ExporterOptions _opt;
    private readonly ILogger<PollerService> _log;

    // Gauges. instance/host labelling comes from Prometheus scrape config; we
    // add "app" so a dashboard can distinguish players if the target ever changes.
    private static readonly string[] Labels = { "app" };
    private static readonly Gauge Up = Metrics.CreateGauge(
        "presentmon_up", "1 if the target process is being tracked and presenting, else 0.", Labels);
    private static readonly Gauge FrameTimeP99 = Metrics.CreateGauge(
        "presentmon_cpu_frame_time_ms_p99", "99th-pct CPU frame time over the window (ms). Stutter signal.", Labels);
    private static readonly Gauge FrameTimeAvg = Metrics.CreateGauge(
        "presentmon_cpu_frame_time_ms_avg", "Average CPU frame time over the window (ms).", Labels);
    private static readonly Gauge DisplayedTimeP99 = Metrics.CreateGauge(
        "presentmon_displayed_time_ms_p99", "99th-pct on-screen frame interval over the window (ms).", Labels);
    private static readonly Gauge DisplayedTimeAvg = Metrics.CreateGauge(
        "presentmon_displayed_time_ms_avg", "Average on-screen frame interval over the window (ms).", Labels);
    private static readonly Gauge DisplayedFps = Metrics.CreateGauge(
        "presentmon_displayed_fps", "Average displayed FPS over the window.", Labels);
    private static readonly Gauge PresentedFps = Metrics.CreateGauge(
        "presentmon_presented_fps", "Average presented FPS over the window.", Labels);
    private static readonly Gauge DroppedFrames = Metrics.CreateGauge(
        "presentmon_dropped_frames", "Dropped frames (windowed average).", Labels);
    private static readonly Gauge GpuPower = Metrics.CreateGauge(
        "presentmon_gpu_power_watts", "Average GPU power over the window (W).", Labels);
    private static readonly Gauge GpuTemp = Metrics.CreateGauge(
        "presentmon_gpu_temperature_celsius", "Average GPU temperature over the window (C).", Labels);
    private static readonly Gauge GpuUtil = Metrics.CreateGauge(
        "presentmon_gpu_utilization_percent", "Average GPU utilization over the window (%).", Labels);

    public PollerService(Microsoft.Extensions.Options.IOptions<ExporterOptions> opt, ILogger<PollerService> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.TargetProcessName))
            throw new InvalidOperationException("Exporter:TargetProcessName is required.");

        var app = _opt.TargetProcessName;
        using var server = new KestrelMetricServer(port: _opt.ListenPort);
        server.Start();
        _log.LogInformation("Exposing /metrics on :{Port}, tracking '{App}'", _opt.ListenPort, app);

        using var pm = new PresentMonSession(_opt.WindowSizeMs, _opt.MetricOffsetMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pid = FindPid(app);
                if (pid is null)
                {
                    Up.WithLabels(app).Set(0);
                }
                else
                {
                    pm.EnsureTracking(pid.Value);
                    var m = pm.Poll(pid.Value);
                    if (m is null)
                    {
                        Up.WithLabels(app).Set(0);   // tracked but not presenting yet
                    }
                    else
                    {
                        Up.WithLabels(app).Set(1);
                        FrameTimeP99.WithLabels(app).Set(m["cpu_frame_time_ms_p99"]);
                        FrameTimeAvg.WithLabels(app).Set(m["cpu_frame_time_ms_avg"]);
                        DisplayedTimeP99.WithLabels(app).Set(m["displayed_time_ms_p99"]);
                        DisplayedTimeAvg.WithLabels(app).Set(m["displayed_time_ms_avg"]);
                        DisplayedFps.WithLabels(app).Set(m["displayed_fps_avg"]);
                        PresentedFps.WithLabels(app).Set(m["presented_fps_avg"]);
                        DroppedFrames.WithLabels(app).Set(m["dropped_frames"]);
                        GpuPower.WithLabels(app).Set(m["gpu_power_w"]);
                        GpuTemp.WithLabels(app).Set(m["gpu_temperature_c"]);
                        GpuUtil.WithLabels(app).Set(m["gpu_utilization_percent"]);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Poll cycle failed");
                Up.WithLabels(app).Set(0);
            }

            await Task.Delay(_opt.PollIntervalMs, stoppingToken);
        }
    }

    private static uint? FindPid(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            // Kiosk mode runs a single foreground instance; take the first alive.
            return procs.Length > 0 ? (uint)procs[0].Id : null;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }
}
