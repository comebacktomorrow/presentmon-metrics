using static KioskPresentMonExporter.PresentMonInterop;

namespace KioskPresentMonExporter;

// One raw per-frame record pulled from the frame query. SwapChain is the swapchain's address
// (0 when this PresentMon can't report it): a kiosk driving several screens presents through
// one swapchain per screen (presentmon-metrics#1).
public readonly record struct FrameRecord(double FrameTimeMs, double DisplayedTimeMs, bool Dropped, ulong SwapChain);

// Bridges the PresentMon SDK frame query to the exporter. We drain the per-frame
// stream (CPU_FRAME_TIME, DISPLAYED_TIME, DROPPED_FRAMES, SWAP_CHAIN_ADDRESS with PM_STAT_NONE) and
// feed each frame into Prometheus histograms + counters. Cumulative, so per-minute
// scraping loses nothing and Grafana can compute any quantile over any window.
internal sealed class PresentMonSession : IDisposable
{
    // Frame-query elements (order = read order). PM_STAT_NONE = raw per-frame value.
    private static readonly PM_METRIC[] FrameMetrics =
    {
        PM_METRIC.PM_METRIC_CPU_FRAME_TIME,   // 0
        PM_METRIC.PM_METRIC_DISPLAYED_TIME,   // 1
        PM_METRIC.PM_METRIC_DROPPED_FRAMES,   // 2
        PM_METRIC.PM_METRIC_SWAP_CHAIN_ADDRESS, // 3 (uint64, not double)
    };

    private readonly uint _batchSize;

    private IntPtr _session;
    private uint _trackedPid;

    private IntPtr _frameQuery;
    private uint _frameStride;              // bytes per frame record
    private long[] _frameOffsets = Array.Empty<long>();
    private byte[] _frameBuf = Array.Empty<byte>();
    private int _metricCount;

    // False when this PresentMon refused the swapchain element: every frame then reads
    // SwapChain = 0, i.e. one series per app, as before presentmon-metrics#1.
    public bool HasSwapChain => _metricCount > 3;

    public PresentMonSession(uint batchSize)
    {
        _batchSize = Math.Max(1, batchSize);

        var st = pmOpenSession(out _session);
        if (st != PM_STATUS.PM_STATUS_SUCCESS)
            throw new InvalidOperationException($"pmOpenSession failed: {st}");

        if (!TryRegisterFrameQuery(FrameMetrics.Length, out var withSwapChain)
            && !TryRegisterFrameQuery(3, out _))
            throw new InvalidOperationException($"pmRegisterFrameQuery failed: {withSwapChain}");
    }

    // Register the frame query over the first `count` FrameMetrics; false (and the status) if
    // PresentMon refused it.
    private bool TryRegisterFrameQuery(int count, out PM_STATUS status)
    {
        var elements = new PM_QUERY_ELEMENT[count];
        for (int i = 0; i < count; i++)
            elements[i] = new PM_QUERY_ELEMENT
            {
                metric = FrameMetrics[i],
                stat = PM_STAT.PM_STAT_NONE,
                deviceId = 0,
                arrayIndex = 0,
            };

        status = pmRegisterFrameQuery(_session, out _frameQuery, elements,
            (ulong)elements.Length, out _frameStride);
        if (status != PM_STATUS.PM_STATUS_SUCCESS) return false;
        if (_frameStride == 0)
            throw new InvalidOperationException("pmRegisterFrameQuery returned blobSize 0");

        _metricCount = count;
        _frameOffsets = elements.Select(e => (long)e.dataOffset).ToArray();
        _frameBuf = new byte[_frameStride * _batchSize];
        return true;
    }

    // Point the query at a pid; re-tracks on change (self-heals on app restart).
    // Returns true if it (re)started tracking this call, false if already tracking pid.
    public bool EnsureTracking(uint pid)
    {
        if (pid == _trackedPid) return false;
        if (_trackedPid != 0) pmStopTrackingProcess(_session, _trackedPid);
        var st = pmStartTrackingProcess(_session, pid);
        if (st != PM_STATUS.PM_STATUS_SUCCESS)
            throw new InvalidOperationException($"pmStartTrackingProcess({pid}) failed: {st}");
        _trackedPid = pid;
        return true;
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
                ulong sc = _metricCount > 3 ? BitConverter.ToUInt64(_frameBuf, (int)(b + _frameOffsets[3])) : 0;
                onFrame(new FrameRecord(ft, dt, dr >= 0.5, sc));
                total++;
            }

            if (n < _batchSize) break;   // buffer wasn't full → stream drained
        }
        return total;
    }

    public void Dispose()
    {
        if (_frameQuery != IntPtr.Zero) pmFreeFrameQuery(_frameQuery);
        if (_trackedPid != 0) pmStopTrackingProcess(_session, _trackedPid);
        if (_session != IntPtr.Zero) pmCloseSession(_session);
        _session = IntPtr.Zero;
    }
}
