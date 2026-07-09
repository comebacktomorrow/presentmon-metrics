using static KioskPresentMonExporter.PresentMonInterop;

namespace KioskPresentMonExporter;

// One raw per-frame record pulled from the frame query.
public readonly record struct FrameRecord(double FrameTimeMs, double DisplayedTimeMs, bool Dropped);

// Bridges the PresentMon SDK to Prometheus. Two queries on one session:
//
//   * FRAME query  — the per-frame stream (CPU_FRAME_TIME, DISPLAYED_TIME,
//     DROPPED_FRAMES with PM_STAT_NONE). We drain every frame and feed them
//     into Prometheus histograms + counters. Cumulative, so per-minute scraping
//     loses nothing and Grafana can compute any quantile over any window.
//
//   * DYNAMIC query — slow-moving GPU telemetry (power/temp/util) as windowed
//     averages → gauges. Optional: disabled automatically on a GPU-less box.
internal sealed class PresentMonSession : IDisposable
{
    // Frame-query elements (order = read order). PM_STAT_NONE = raw per-frame value.
    private static readonly PM_METRIC[] FrameMetrics =
    {
        PM_METRIC.PM_METRIC_CPU_FRAME_TIME,   // 0
        PM_METRIC.PM_METRIC_DISPLAYED_TIME,   // 1
        PM_METRIC.PM_METRIC_DROPPED_FRAMES,   // 2
    };

    private static readonly (string Key, PM_METRIC Metric)[] GpuMetrics =
    {
        ("gpu_power_w",             PM_METRIC.PM_METRIC_GPU_POWER),
        ("gpu_temperature_c",       PM_METRIC.PM_METRIC_GPU_TEMPERATURE),
        ("gpu_utilization_percent", PM_METRIC.PM_METRIC_GPU_UTILIZATION),
    };

    private readonly double _gpuWindowMs;
    private readonly uint _batchSize;

    private IntPtr _session;
    private uint _trackedPid;

    // Frame query state
    private IntPtr _frameQuery;
    private uint _frameStride;              // bytes per frame record
    private long[] _frameOffsets = Array.Empty<long>();
    private byte[] _frameBuf = Array.Empty<byte>();

    // GPU dynamic query state (optional)
    private bool _gpuEnabled;
    private IntPtr _gpuQuery;
    private PM_QUERY_ELEMENT[] _gpuElements = Array.Empty<PM_QUERY_ELEMENT>();
    private int _gpuStride;

    public bool GpuEnabled => _gpuEnabled;

    public PresentMonSession(double gpuWindowMs, uint batchSize)
    {
        _gpuWindowMs = gpuWindowMs;
        _batchSize = Math.Max(1, batchSize);

        var st = pmOpenSession(out _session);
        if (st != PM_STATUS.PM_STATUS_SUCCESS)
            throw new InvalidOperationException($"pmOpenSession failed: {st}");

        RegisterFrameQuery();
        TryRegisterGpuQuery();   // best-effort; leaves _gpuEnabled=false on failure
    }

    private void RegisterFrameQuery()
    {
        var elements = new PM_QUERY_ELEMENT[FrameMetrics.Length];
        for (int i = 0; i < FrameMetrics.Length; i++)
            elements[i] = new PM_QUERY_ELEMENT
            {
                metric = FrameMetrics[i],
                stat = PM_STAT.PM_STAT_NONE,
                deviceId = 0,
                arrayIndex = 0,
            };

        var st = pmRegisterFrameQuery(_session, out _frameQuery, elements,
            (ulong)elements.Length, out _frameStride);
        if (st != PM_STATUS.PM_STATUS_SUCCESS)
            throw new InvalidOperationException($"pmRegisterFrameQuery failed: {st}");
        if (_frameStride == 0)
            throw new InvalidOperationException("pmRegisterFrameQuery returned blobSize 0");

        _frameOffsets = elements.Select(e => (long)e.dataOffset).ToArray();
        _frameBuf = new byte[_frameStride * _batchSize];
    }

    private void TryRegisterGpuQuery()
    {
        try
        {
            _gpuElements = new PM_QUERY_ELEMENT[GpuMetrics.Length];
            for (int i = 0; i < GpuMetrics.Length; i++)
                _gpuElements[i] = new PM_QUERY_ELEMENT
                {
                    metric = GpuMetrics[i].Metric,
                    stat = PM_STAT.PM_STAT_AVG,
                    deviceId = 0,   // may need a real adapter id; see PLAN.md
                    arrayIndex = 0,
                };

            var st = pmRegisterDynamicQuery(_session, out _gpuQuery, _gpuElements,
                (ulong)_gpuElements.Length, _gpuWindowMs, 0);
            if (st != PM_STATUS.PM_STATUS_SUCCESS)
                return;   // GPU telemetry unavailable (e.g. no GPU) — frame path unaffected

            _gpuStride = 0;
            foreach (var e in _gpuElements)
                _gpuStride = Math.Max(_gpuStride, (int)(e.dataOffset + e.dataSize));
            _gpuEnabled = _gpuStride > 0;
        }
        catch
        {
            _gpuEnabled = false;
        }
    }

    // Point both queries at a pid; re-tracks on change (self-heals on app restart).
    public void EnsureTracking(uint pid)
    {
        if (pid == _trackedPid) return;
        if (_trackedPid != 0) pmStopTrackingProcess(_session, _trackedPid);
        var st = pmStartTrackingProcess(_session, pid);
        if (st != PM_STATUS.PM_STATUS_SUCCESS)
            throw new InvalidOperationException($"pmStartTrackingProcess({pid}) failed: {st}");
        _trackedPid = pid;
    }

    // Drain all queued frames for pid, invoking onFrame for each. Returns frame count.
    public int DrainFrames(uint pid, Action<FrameRecord> onFrame)
    {
        int total = 0;
        while (true)
        {
            uint n = _batchSize;
            var st = pmConsumeFrames(_frameQuery, pid, _frameBuf, ref n);
            if (st != PM_STATUS.PM_STATUS_SUCCESS || n == 0)
                break;

            for (uint i = 0; i < n; i++)
            {
                long b = (long)i * _frameStride;
                double ft = BitConverter.ToDouble(_frameBuf, (int)(b + _frameOffsets[0]));
                double dt = BitConverter.ToDouble(_frameBuf, (int)(b + _frameOffsets[1]));
                double dr = BitConverter.ToDouble(_frameBuf, (int)(b + _frameOffsets[2]));
                onFrame(new FrameRecord(ft, dt, dr >= 0.5));
                total++;
            }

            if (n < _batchSize) break;   // buffer wasn't full → stream drained
        }
        return total;
    }

    // Latest windowed GPU averages, or null if GPU telemetry is disabled/empty.
    public IReadOnlyDictionary<string, double>? PollGpu(uint pid)
    {
        if (!_gpuEnabled) return null;

        var blob = new byte[_gpuStride];
        uint swapChains = 1;
        var st = pmPollDynamicQuery(_gpuQuery, pid, blob, ref swapChains);
        if (st != PM_STATUS.PM_STATUS_SUCCESS || swapChains == 0)
            return null;

        var result = new Dictionary<string, double>(GpuMetrics.Length);
        for (int i = 0; i < GpuMetrics.Length; i++)
            result[GpuMetrics[i].Key] = BitConverter.ToDouble(blob, (int)_gpuElements[i].dataOffset);
        return result;
    }

    public void Dispose()
    {
        if (_frameQuery != IntPtr.Zero) pmFreeFrameQuery(_frameQuery);
        if (_trackedPid != 0) pmStopTrackingProcess(_session, _trackedPid);
        if (_session != IntPtr.Zero) pmCloseSession(_session);
        _session = IntPtr.Zero;
    }
}
