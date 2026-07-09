using System.Diagnostics;
using Microsoft.Extensions.Options;
using Prometheus;

namespace KioskPresentMonExporter;

public sealed class ExporterOptions
{
    // Process to measure, WITHOUT the .exe suffix (e.g. "SignagePlayer", "notepad").
    public string TargetProcessName { get; set; } = "";
    // Optional: target an exact PID instead of resolving by name (0 = use name).
    // Handy when multiple instances share a name (e.g. one dwm per session).
    public int TargetProcessId { get; set; } = 0;
    public int ListenPort { get; set; } = 9110;
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
    private readonly string _app;

    // Own registry so ONLY presentmon_* is exposed — no dotnet_/process_/kestrel_
    // default-collector noise (which is pure overhead across a fleet).
    private readonly CollectorRegistry _registry;
    private readonly Histogram _frameTime;
    private readonly Histogram _displayedTime;
    private readonly Histogram _displayedFpsHist;
    private readonly Counter _presented;
    private readonly Counter _displayed;
    private readonly Counter _dropped;
    private readonly Gauge _fps;
    private readonly Gauge _up;

    // Buckets in ms, tuned around 60 Hz (16.7) and 30 Hz (33.3).
    private static readonly double[] FrameTimeBuckets =
        { 6, 8, 10, 12, 14, 16.7, 20, 25, 33.3, 50, 66.7, 100, 250, 500 };

    // Buckets in fps (1000/frametime), for the intuitive fps heatmap. Covers the
    // signage-relevant range; a locked-60 player clusters at 60 and smears toward
    // 30/24 during stutter.
    private static readonly double[] FpsBuckets =
        { 5, 10, 15, 20, 24, 30, 40, 48, 50, 60, 72, 90, 120, 144, 240 };

    public PollerService(IOptions<ExporterOptions> opt, ILogger<PollerService> log)
    {
        _opt = opt.Value;
        _log = log;

        if (string.IsNullOrWhiteSpace(_opt.TargetProcessName) && _opt.TargetProcessId <= 0)
            throw new InvalidOperationException("Exporter:TargetProcessName or TargetProcessId is required.");
        _app = !string.IsNullOrWhiteSpace(_opt.TargetProcessName)
            ? _opt.TargetProcessName
            : $"pid{_opt.TargetProcessId}";

        _registry = Metrics.NewCustomRegistry();
        var f = Metrics.WithCustomRegistry(_registry);
        var labels = new[] { "app" };

        _up = f.CreateGauge("presentmon_up",
            "1 if the target process is tracked and producing frames, else 0.", labels);
        _fps = f.CreateGauge("presentmon_displayed_fps",
            "Displayed frames in the last poll interval, as frames/sec (glanceable; rate() of the counter is authoritative).", labels);
        _frameTime = f.CreateHistogram("presentmon_frame_time_ms",
            "CPU frame time per frame (ms).",
            new HistogramConfiguration { Buckets = FrameTimeBuckets, LabelNames = labels });
        _displayedTime = f.CreateHistogram("presentmon_displayed_time_ms",
            "On-screen interval per displayed frame (ms).",
            new HistogramConfiguration { Buckets = FrameTimeBuckets, LabelNames = labels });
        _displayedFpsHist = f.CreateHistogram("presentmon_displayed_fps_hist",
            "Distribution of instantaneous displayed fps (1000/displayed_time) per frame; render as a heatmap.",
            new HistogramConfiguration { Buckets = FpsBuckets, LabelNames = labels });
        _presented = f.CreateCounter("presentmon_frames_presented_total",
            "Frames presented (all frames in the stream).", labels);
        _displayed = f.CreateCounter("presentmon_frames_displayed_total",
            "Frames that reached the screen (not dropped).", labels);
        _dropped = f.CreateCounter("presentmon_frames_dropped_total",
            "Frames dropped (presented but never displayed).", labels);

        // Publish every series at zero up front so "no frames yet" / "no drops"
        // reads as 0, not absent — otherwise increase()/rate() return No Data and
        // panels/alerts break until the first event.
        _up.WithLabels(_app).Set(0);
        _fps.WithLabels(_app).Set(0);
        _presented.WithLabels(_app).Inc(0);
        _displayed.WithLabels(_app).Inc(0);
        _dropped.WithLabels(_app).Inc(0);
        _frameTime.WithLabels(_app);
        _displayedTime.WithLabels(_app);
        _displayedFpsHist.WithLabels(_app);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var server = new KestrelMetricServer(port: _opt.ListenPort, registry: _registry);
        server.Start();
        _log.LogInformation("Exposing /metrics on :{Port}, tracking '{App}'", _opt.ListenPort, _app);

        using var pm = new PresentMonSession(_opt.FrameBatchSize);

        // Track actual wall-time between drains so the fps gauge reflects real
        // elapsed, not the nominal interval (which over/under-counts when a cycle
        // runs long or drains a startup backlog). rate() stays authoritative.
        long lastTs = Stopwatch.GetTimestamp();
        bool firstCycle = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pid = ResolvePid();
                if (pid is null)
                {
                    _up.WithLabels(_app).Set(0);
                    _fps.WithLabels(_app).Set(0);
                }
                else
                {
                    bool retracked = pm.EnsureTracking(pid.Value);

                    int displayedThisCycle = 0;
                    int drained = pm.DrainFrames(pid.Value, frame =>
                    {
                        if (IsSane(frame.FrameTimeMs))
                            _frameTime.WithLabels(_app).Observe(frame.FrameTimeMs);
                        _presented.WithLabels(_app).Inc();
                        if (frame.Dropped)
                        {
                            _dropped.WithLabels(_app).Inc();
                        }
                        else
                        {
                            _displayed.WithLabels(_app).Inc();
                            displayedThisCycle++;
                            if (IsSane(frame.DisplayedTimeMs) && frame.DisplayedTimeMs > 0)
                            {
                                _displayedTime.WithLabels(_app).Observe(frame.DisplayedTimeMs);
                                _displayedFpsHist.WithLabels(_app).Observe(1000.0 / frame.DisplayedTimeMs);
                            }
                        }
                    });

                    var elapsedSec = Stopwatch.GetElapsedTime(lastTs).TotalSeconds;
                    lastTs = Stopwatch.GetTimestamp();

                    _up.WithLabels(_app).Set(drained > 0 ? 1 : 0);
                    // Skip the fps sample on the first cycle or just after (re)tracking:
                    // that drain covers an arbitrary backlog window, not one interval.
                    if (!firstCycle && !retracked && elapsedSec > 0)
                        _fps.WithLabels(_app).Set(displayedThisCycle / elapsedSec);
                    firstCycle = false;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Poll cycle failed");
                _up.WithLabels(_app).Set(0);
                _fps.WithLabels(_app).Set(0);
            }

            await Task.Delay(_opt.PollIntervalMs, stoppingToken);
        }
    }

    private static bool IsSane(double v) => double.IsFinite(v) && v >= 0;

    private uint? ResolvePid()
    {
        if (_opt.TargetProcessId > 0)
        {
            try
            {
                using var p = Process.GetProcessById(_opt.TargetProcessId);
                return (uint)p.Id;
            }
            catch (ArgumentException)
            {
                return null;   // pid no longer alive
            }
        }

        var procs = Process.GetProcessesByName(_opt.TargetProcessName);
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
