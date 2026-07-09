using static KioskPresentMonExporter.PresentMonInterop;

namespace KioskPresentMonExporter;

// Thin wrapper over the SDK dynamic-query flow:
//   pmOpenSession -> pmStartTrackingProcess -> pmRegisterDynamicQuery
//   -> pmPollDynamicQuery (once per scrape) -> pmCloseSession
//
// The dynamic query does the aggregation for us: we ask the SDK for the
// PERCENTILE_99 / AVG of each metric over a sliding window, so the exporter
// never touches per-frame data. Poll -> read doubles -> set gauges.
internal sealed class PresentMonSession : IDisposable
{
    // Each entry becomes one PM_QUERY_ELEMENT and one exported gauge key.
    private static readonly (string Key, PM_METRIC Metric, PM_STAT Stat)[] Wanted =
    {
        // The trio you actually asked for:
        ("cpu_frame_time_ms_p99",   PM_METRIC.PM_METRIC_CPU_FRAME_TIME, PM_STAT.PM_STAT_PERCENTILE_99),
        ("cpu_frame_time_ms_avg",   PM_METRIC.PM_METRIC_CPU_FRAME_TIME, PM_STAT.PM_STAT_AVG),
        ("displayed_time_ms_p99",   PM_METRIC.PM_METRIC_DISPLAYED_TIME, PM_STAT.PM_STAT_PERCENTILE_99),
        ("displayed_time_ms_avg",   PM_METRIC.PM_METRIC_DISPLAYED_TIME, PM_STAT.PM_STAT_AVG),
        ("displayed_fps_avg",       PM_METRIC.PM_METRIC_DISPLAYED_FPS,  PM_STAT.PM_STAT_AVG),
        ("presented_fps_avg",       PM_METRIC.PM_METRIC_PRESENTED_FPS,  PM_STAT.PM_STAT_AVG),
        ("dropped_frames",          PM_METRIC.PM_METRIC_DROPPED_FRAMES, PM_STAT.PM_STAT_AVG),
        // GPU telemetry — bonus, same poll. NOTE: GPU metrics may require a real
        // adapter deviceId (see below); if they read as 0/NaN, resolve the id via
        // the introspection API. Frame metrics above use deviceId 0 and are solid.
        ("gpu_power_w",             PM_METRIC.PM_METRIC_GPU_POWER,       PM_STAT.PM_STAT_AVG),
        ("gpu_temperature_c",       PM_METRIC.PM_METRIC_GPU_TEMPERATURE, PM_STAT.PM_STAT_AVG),
        ("gpu_utilization_percent", PM_METRIC.PM_METRIC_GPU_UTILIZATION, PM_STAT.PM_STAT_AVG),
    };

    private const uint MaxSwapChains = 8;

    private readonly double _windowMs;
    private readonly double _offsetMs;

    private IntPtr _session;
    private IntPtr _query;
    private PM_QUERY_ELEMENT[] _elements = Array.Empty<PM_QUERY_ELEMENT>();
    private int _blobStride;
    private uint _trackedPid;

    public PresentMonSession(double windowMs, double offsetMs)
    {
        _windowMs = windowMs;
        _offsetMs = offsetMs;

        var st = pmOpenSession(out _session);
        if (st != PM_STATUS.PM_STATUS_SUCCESS)
            throw new InvalidOperationException($"pmOpenSession failed: {st}");

        RegisterQuery();
    }

    private void RegisterQuery()
    {
        _elements = new PM_QUERY_ELEMENT[Wanted.Length];
        for (int i = 0; i < Wanted.Length; i++)
        {
            _elements[i] = new PM_QUERY_ELEMENT
            {
                metric = Wanted[i].Metric,
                stat = Wanted[i].Stat,
                deviceId = 0,   // 0 = default/universal device for frame metrics
                arrayIndex = 0,
            };
        }

        var st = pmRegisterDynamicQuery(_session, out _query, _elements,
            (ulong)_elements.Length, _windowMs, _offsetMs);
        if (st != PM_STATUS.PM_STATUS_SUCCESS)
            throw new InvalidOperationException($"pmRegisterDynamicQuery failed: {st}");

        // After registration, each element's dataOffset/dataSize is populated.
        // The per-swap-chain stride is the far edge of the last element.
        _blobStride = 0;
        foreach (var e in _elements)
            _blobStride = Math.Max(_blobStride, (int)(e.dataOffset + e.dataSize));
    }

    // Point the query at a pid. Safe to call repeatedly; only re-tracks on change
    // (self-heals when the signage app restarts under a new pid).
    public void EnsureTracking(uint pid)
    {
        if (pid == _trackedPid) return;
        if (_trackedPid != 0) pmStopTrackingProcess(_session, _trackedPid);
        var st = pmStartTrackingProcess(_session, pid);
        if (st != PM_STATUS.PM_STATUS_SUCCESS)
            throw new InvalidOperationException($"pmStartTrackingProcess({pid}) failed: {st}");
        _trackedPid = pid;
    }

    // Poll the current window and return metric-key -> value. Returns null if the
    // process produced no swap-chain data this window (app not presenting yet).
    public IReadOnlyDictionary<string, double>? Poll(uint pid)
    {
        var blob = new byte[_blobStride * MaxSwapChains];
        uint swapChains = MaxSwapChains;

        var st = pmPollDynamicQuery(_query, pid, blob, ref swapChains);
        if (st != PM_STATUS.PM_STATUS_SUCCESS || swapChains == 0)
            return null;

        // A fullscreen signage app has one swap chain; read the first block.
        var result = new Dictionary<string, double>(Wanted.Length);
        for (int i = 0; i < Wanted.Length; i++)
        {
            int off = (int)_elements[i].dataOffset;
            result[Wanted[i].Key] = BitConverter.ToDouble(blob, off);
        }
        return result;
    }

    public void Dispose()
    {
        if (_trackedPid != 0) pmStopTrackingProcess(_session, _trackedPid);
        if (_session != IntPtr.Zero) pmCloseSession(_session);
        _session = IntPtr.Zero;
    }
}
