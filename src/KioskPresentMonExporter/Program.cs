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

    // Idle grace: presentmon_up stays 1 for this long after the LAST frame.
    // Windows apps present only on damage, so static content legitimately
    // presents nothing for stretches — without the grace, `up` flaps on every
    // pause in screen activity and every state derived from it bounces too.
    // A vanished process is still a hard 0 (no grace). 0 = old per-poll
    // semantics.
    public int IdleGraceSeconds { get; set; } = 120;

    // Which metric families to EMIT. `up` and frames_dropped_total are always on.
    // What actually ships to a backend is a separate concern (scrape/keep-list);
    // this controls what the exporter exposes at all. All default on.
    public MetricsOptions Metrics { get; set; } = new();
}

public sealed class MetricsOptions
{
    public bool FrameTimeMs { get; set; } = true;       // CPU frame time histogram
    public bool DisplayedTimeMs { get; set; } = true;   // on-screen interval histogram (ms)
    public bool DisplayedFpsHist { get; set; } = true;  // instantaneous fps histogram (heatmap)
    // Optional bucket overrides; null/empty => built-in tuned defaults.
    public double[]? FrameTimeBucketsMs { get; set; }
    public double[]? FpsBuckets { get; set; }
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
    // Histograms are OPTIONAL (per Exporter:Metrics config) — null when disabled.
    // Their _count fields double as frame counts (frame_time_ms_count = presented,
    // displayed_*_count = displayed); `dropped` is the only count needing its own metric.
    private readonly Histogram? _frameTime;
    private readonly Histogram? _displayedTime;
    private readonly Histogram? _displayedFpsHist;
    private readonly Counter _dropped;
    private readonly Gauge _up;
    private readonly Gauge _lastPresent;

    // Default buckets in ms. DENSE around common refresh rates (8.3=120Hz, 16.7=60Hz)
    // so the mass doesn't collapse into one wide bucket (bad histogram_quantile interp),
    // plus finer resolution through the stutter region. Override via Exporter:Metrics:FrameTimeBucketsMs.
    private static readonly double[] DefaultFrameTimeBuckets =
        { 4, 6, 8, 8.3, 10, 12, 14, 15, 16, 16.7, 17, 18, 20, 22, 25, 28, 33.3, 40, 50, 66.7, 100, 200, 500 };

    // Default buckets in fps. Dense around 60 (+ 30/24 stutter), covers 120/144 Hz.
    // Override via Exporter:Metrics:FpsBuckets.
    private static readonly double[] DefaultFpsBuckets =
        { 10, 20, 24, 28, 30, 40, 48, 50, 55, 58, 60, 72, 90, 110, 120, 144, 240 };

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

        var frameBuckets = (_opt.Metrics.FrameTimeBucketsMs is { Length: > 0 } fb) ? fb : DefaultFrameTimeBuckets;
        var fpsBuckets   = (_opt.Metrics.FpsBuckets is { Length: > 0 } pb) ? pb : DefaultFpsBuckets;

        _up = f.CreateGauge("presentmon_up",
            "1 if the target process is tracked and has presented within IdleGraceSeconds (static content presents nothing); 0 when frames stop past the grace or the process is gone (no grace).", labels);
        _dropped = f.CreateCounter("presentmon_frames_dropped_total",
            "Frames dropped (presented but never displayed).", labels);
        // Unix time of the last drained present. Initialized to SERVICE START,
        // not 0: on startup nothing has been seen yet, and time()-0 would read
        // as an eternity of stillness — a restart on static content must not
        // false-fire per-host freeze alerts (time() - this > threshold).
        _lastPresent = f.CreateGauge("presentmon_last_present_timestamp_seconds",
            "Unix time the tracked process last presented a frame (initialized to service start). Alert on time() - this exceeding a per-host stillness budget.", labels);
        if (_opt.Metrics.FrameTimeMs)
            _frameTime = f.CreateHistogram("presentmon_frame_time_ms",
                "CPU frame time per frame (ms).",
                new HistogramConfiguration { Buckets = frameBuckets, LabelNames = labels });
        if (_opt.Metrics.DisplayedTimeMs)
            _displayedTime = f.CreateHistogram("presentmon_displayed_time_ms",
                "On-screen interval per displayed frame (ms).",
                new HistogramConfiguration { Buckets = frameBuckets, LabelNames = labels });
        if (_opt.Metrics.DisplayedFpsHist)
            _displayedFpsHist = f.CreateHistogram("presentmon_displayed_fps_hist",
                "Distribution of instantaneous displayed fps (1000/displayed_time) per frame; render as a heatmap.",
                new HistogramConfiguration { Buckets = fpsBuckets, LabelNames = labels });
        _log.LogInformation("emitting: frame_time_ms={Ft} displayed_time_ms={Dt} displayed_fps_hist={Fh}",
            _opt.Metrics.FrameTimeMs, _opt.Metrics.DisplayedTimeMs, _opt.Metrics.DisplayedFpsHist);

        // Publish every enabled series at zero up front so "no frames yet" / "no drops"
        // reads as 0, not absent — otherwise increase()/rate() return No Data and
        // panels/alerts break until the first event.
        _up.WithLabels(_app).Set(0);
        _lastPresent.WithLabels(_app).Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _dropped.WithLabels(_app).Inc(0);
        _frameTime?.WithLabels(_app);
        _displayedTime?.WithLabels(_app);
        _displayedFpsHist?.WithLabels(_app);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var server = new KestrelMetricServer(port: _opt.ListenPort, registry: _registry);
        server.Start();
        _log.LogInformation("Exposing /metrics on :{Port}, tracking '{App}'", _opt.ListenPort, _app);

        using var pm = new PresentMonSession(_opt.FrameBatchSize);

        uint currentPid = 0;   // the PID we're currently tracking among name matches
        int unproductive = 0;  // consecutive zero-frame cycles on currentPid
        var lastFrameUtc = DateTime.MinValue;   // when the tracked pid last presented

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var candidates = CandidatePids();
                if (candidates.Length == 0)
                {
                    // process GONE is a hard down — the grace is for content
                    // that stops changing, not for an app that stopped existing
                    currentPid = 0;
                    lastFrameUtc = DateTime.MinValue;
                    _up.WithLabels(_app).Set(0);
                }
                else
                {
                    // If our tracked PID vanished (or first run), start on the first match.
                    if (Array.IndexOf(candidates, currentPid) < 0) { currentPid = candidates[0]; unproductive = 0; }
                    bool retracked = pm.EnsureTracking(currentPid);

                    int drained = pm.DrainFrames(currentPid, frame =>
                    {
                        if (IsSane(frame.FrameTimeMs))
                            _frameTime?.WithLabels(_app).Observe(frame.FrameTimeMs);
                        if (frame.Dropped)
                        {
                            _dropped.WithLabels(_app).Inc();
                        }
                        else if (IsSane(frame.DisplayedTimeMs) && frame.DisplayedTimeMs > 0)
                        {
                            _displayedTime?.WithLabels(_app).Observe(frame.DisplayedTimeMs);
                            _displayedFpsHist?.WithLabels(_app).Observe(1000.0 / frame.DisplayedTimeMs);
                        }
                    });

                    if (drained > 0)
                    {
                        lastFrameUtc = DateTime.UtcNow;
                        _lastPresent.WithLabels(_app).Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    }
                    bool withinGrace = _opt.IdleGraceSeconds > 0
                        && lastFrameUtc != DateTime.MinValue
                        && (DateTime.UtcNow - lastFrameUtc).TotalSeconds <= _opt.IdleGraceSeconds;
                    _up.WithLabels(_app).Set(drained > 0 || withinGrace ? 1 : 0);

                    // Converge on the PRESENTING pid: if this match isn't producing frames,
                    // give it a couple cycles then rotate to the next name-match. Auto-finds
                    // the presenter for multi-process apps (e.g. Chrome's GPU process); a no-op
                    // when the name resolves to one process (Unity / native players).
                    if (drained > 0) unproductive = 0;
                    else if (candidates.Length > 1 && !retracked && ++unproductive >= 2)
                    {
                        int idx = Array.IndexOf(candidates, currentPid);
                        currentPid = candidates[(idx + 1) % candidates.Length];
                        unproductive = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Poll cycle failed");
                _up.WithLabels(_app).Set(0);
            }

            await Task.Delay(_opt.PollIntervalMs, stoppingToken);
        }
    }

    private static bool IsSane(double v) => double.IsFinite(v) && v >= 0;

    // All PIDs matching the target (exact PID if set, else every process by name).
    // The poll loop converges on whichever of these is actually presenting.
    private uint[] CandidatePids()
    {
        if (_opt.TargetProcessId > 0)
        {
            try
            {
                using var p = Process.GetProcessById(_opt.TargetProcessId);
                return new[] { (uint)p.Id };
            }
            catch (ArgumentException)
            {
                return Array.Empty<uint>();   // pid no longer alive
            }
        }

        var procs = Process.GetProcessesByName(_opt.TargetProcessName);
        try
        {
            return procs.Select(p => (uint)p.Id).ToArray();
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }
}
