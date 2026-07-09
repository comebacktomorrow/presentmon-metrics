using System.Diagnostics;
using Microsoft.Extensions.Options;
using Prometheus;

namespace KioskPresentMonExporter;

public sealed class ExporterOptions
{
    // Process to measure, WITHOUT the .exe suffix (e.g. "SignagePlayer", "notepad").
    public string TargetProcessName { get; set; } = "";
    public int ListenPort { get; set; } = 9110;
    public double GpuWindowMs { get; set; } = 1000;   // averaging window for GPU gauges
    public uint FrameBatchSize { get; set; } = 512;   // frames drained per pmConsumeFrames call
    public int PollIntervalMs { get; set; } = 1000;   // how often we drain the frame stream
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

    private static readonly string[] Labels = { "app" };

    // Buckets in ms, tuned around 60 Hz (16.7) and 30 Hz (33.3) — the frame-pacing
    // range that matters for signage smoothness.
    private static readonly double[] FrameTimeBuckets =
        { 6, 8, 10, 12, 14, 16.7, 20, 25, 33.3, 50, 66.7, 100, 250, 500 };

    // Cumulative histograms: Grafana computes p99 over ANY window via
    //   histogram_quantile(0.99, rate(presentmon_frame_time_ms_bucket[5m]))
    private static readonly Histogram FrameTime = Metrics.CreateHistogram(
        "presentmon_frame_time_ms", "CPU frame time per frame (ms).",
        new HistogramConfiguration { Buckets = FrameTimeBuckets, LabelNames = Labels });
    private static readonly Histogram DisplayedTime = Metrics.CreateHistogram(
        "presentmon_displayed_time_ms", "On-screen interval per displayed frame (ms).",
        new HistogramConfiguration { Buckets = FrameTimeBuckets, LabelNames = Labels });

    // Cumulative counters: FPS = rate(...presented/displayed...), drops = increase(...dropped...)
    private static readonly Counter FramesPresented = Metrics.CreateCounter(
        "presentmon_frames_presented_total", "Frames presented (all frames in the stream).", Labels);
    private static readonly Counter FramesDisplayed = Metrics.CreateCounter(
        "presentmon_frames_displayed_total", "Frames that reached the screen (not dropped).", Labels);
    private static readonly Counter FramesDropped = Metrics.CreateCounter(
        "presentmon_frames_dropped_total", "Frames dropped (presented but never displayed).", Labels);

    private static readonly Gauge Up = Metrics.CreateGauge(
        "presentmon_up", "1 if the target process is tracked and producing frames, else 0.", Labels);
    private static readonly Gauge GpuPower = Metrics.CreateGauge(
        "presentmon_gpu_power_watts", "Windowed avg GPU power (W).", Labels);
    private static readonly Gauge GpuTemp = Metrics.CreateGauge(
        "presentmon_gpu_temperature_celsius", "Windowed avg GPU temperature (C).", Labels);
    private static readonly Gauge GpuUtil = Metrics.CreateGauge(
        "presentmon_gpu_utilization_percent", "Windowed avg GPU utilization (%).", Labels);

    public PollerService(IOptions<ExporterOptions> opt, ILogger<PollerService> log)
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

        using var pm = new PresentMonSession(_opt.GpuWindowMs, _opt.FrameBatchSize);
        _log.LogInformation("Exposing /metrics on :{Port}, tracking '{App}' (GPU telemetry: {Gpu})",
            _opt.ListenPort, app, pm.GpuEnabled ? "on" : "off");

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

                    int drained = pm.DrainFrames(pid.Value, f =>
                    {
                        if (IsSane(f.FrameTimeMs))
                            FrameTime.WithLabels(app).Observe(f.FrameTimeMs);
                        FramesPresented.WithLabels(app).Inc();
                        if (f.Dropped)
                        {
                            FramesDropped.WithLabels(app).Inc();
                        }
                        else
                        {
                            FramesDisplayed.WithLabels(app).Inc();
                            if (IsSane(f.DisplayedTimeMs))
                                DisplayedTime.WithLabels(app).Observe(f.DisplayedTimeMs);
                        }
                    });

                    Up.WithLabels(app).Set(drained > 0 ? 1 : 0);

                    var gpu = pm.PollGpu(pid.Value);
                    if (gpu is not null)
                    {
                        SetIfSane(GpuPower, app, gpu["gpu_power_w"]);
                        SetIfSane(GpuTemp, app, gpu["gpu_temperature_c"]);
                        SetIfSane(GpuUtil, app, gpu["gpu_utilization_percent"]);
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

    private static bool IsSane(double v) => double.IsFinite(v) && v >= 0;

    private static void SetIfSane(Gauge g, string app, double v)
    {
        if (IsSane(v)) g.WithLabels(app).Set(v);
    }

    private static uint? FindPid(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            return procs.Length > 0 ? (uint)procs[0].Id : null;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }
}
