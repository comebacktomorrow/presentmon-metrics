using System.Runtime.InteropServices;

namespace KioskPresentMonExporter;

// P/Invoke bindings for the PresentMon 2.x SDK (PresentMonAPI2).
//
// The PresentMon Service (v2.3.1+) deploys PresentMonAPI2.dll alongside the
// service. Point the exporter at it either by copying the DLL next to this
// exe or by installing the service normally (it registers the DLL).
//
// IMPORTANT: PM_METRIC / PM_STAT / PM_STATUS are plain C enums with implicit
// sequential values. The members below are listed in the SAME ORDER as
// PresentMonAPI.h so the compiler assigns identical integer values. If you
// bump the SDK version, re-diff this ordering against the shipped header.
internal static class PresentMonInterop
{
    // If you prefer the loader shim, change this to "PresentMonAPI2Loader.dll".
    private const string Dll = "PresentMonAPI2.dll";

    public enum PM_STATUS
    {
        PM_STATUS_SUCCESS = 0,
        PM_STATUS_FAILURE,
        PM_STATUS_BAD_ARGUMENT,
        // ... remaining status codes exist in the header; we only branch on SUCCESS.
    }

    // Order-sensitive: values must match PresentMonAPI.h exactly.
    public enum PM_METRIC
    {
        PM_METRIC_APPLICATION = 0,
        PM_METRIC_SWAP_CHAIN_ADDRESS,
        PM_METRIC_GPU_VENDOR,
        PM_METRIC_GPU_NAME,
        PM_METRIC_CPU_VENDOR,
        PM_METRIC_CPU_NAME,
        PM_METRIC_CPU_START_TIME,
        PM_METRIC_CPU_START_QPC,
        PM_METRIC_CPU_FRAME_TIME,      // 8
        PM_METRIC_CPU_BUSY,
        PM_METRIC_CPU_WAIT,
        PM_METRIC_DISPLAYED_FPS,       // 11
        PM_METRIC_PRESENTED_FPS,       // 12
        PM_METRIC_GPU_TIME,
        PM_METRIC_GPU_BUSY,
        PM_METRIC_GPU_WAIT,
        PM_METRIC_DROPPED_FRAMES,      // 16
        PM_METRIC_DISPLAYED_TIME,      // 17
        PM_METRIC_SYNC_INTERVAL,
        PM_METRIC_PRESENT_FLAGS,
        PM_METRIC_PRESENT_MODE,
        PM_METRIC_PRESENT_RUNTIME,
        PM_METRIC_ALLOWS_TEARING,
        PM_METRIC_GPU_LATENCY,
        PM_METRIC_DISPLAY_LATENCY,
        PM_METRIC_CLICK_TO_PHOTON_LATENCY,
        PM_METRIC_GPU_SUSTAINED_POWER_LIMIT,
        PM_METRIC_GPU_POWER,           // 27
        PM_METRIC_GPU_VOLTAGE,
        PM_METRIC_GPU_FREQUENCY,
        PM_METRIC_GPU_TEMPERATURE,     // 30
        PM_METRIC_GPU_FAN_SPEED,
        PM_METRIC_GPU_UTILIZATION,     // 32
    }

    public enum PM_STAT
    {
        PM_STAT_NONE = 0,
        PM_STAT_AVG,
        PM_STAT_PERCENTILE_99,
        PM_STAT_PERCENTILE_95,
        PM_STAT_PERCENTILE_90,
        PM_STAT_PERCENTILE_01,
        PM_STAT_PERCENTILE_05,
        PM_STAT_PERCENTILE_10,
        PM_STAT_MAX,
        PM_STAT_MIN,
        PM_STAT_MID_POINT,
        PM_STAT_MID_LERP,
        PM_STAT_NEWEST_POINT,
        PM_STAT_OLDEST_POINT,
        PM_STAT_COUNT,
        PM_STAT_NON_ZERO_AVG,
    }

    // Layout matches struct PM_QUERY_ELEMENT. dataOffset/dataSize are filled in
    // by pmRegisterDynamicQuery and tell you where each result double lives in
    // the polled blob.
    [StructLayout(LayoutKind.Sequential)]
    public struct PM_QUERY_ELEMENT
    {
        public PM_METRIC metric;
        public PM_STAT stat;
        public uint deviceId;
        public uint arrayIndex;
        public ulong dataOffset;
        public ulong dataSize;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmOpenSession(out IntPtr pHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmCloseSession(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmStartTrackingProcess(IntPtr handle, uint processId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmStopTrackingProcess(IntPtr handle, uint processId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmRegisterDynamicQuery(
        IntPtr sessionHandle,
        out IntPtr pHandle,
        [In, Out] PM_QUERY_ELEMENT[] pElements,
        ulong numElements,
        double windowSizeMs,
        double metricOffsetMs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmPollDynamicQuery(
        IntPtr handle,
        uint processId,
        [In, Out] byte[] pBlob,
        ref uint numSwapChains);

    // --- Frame query (per-frame stream) ---
    // pBlobSize returns the byte stride of one frame's record; each query
    // element's dataOffset is that value's offset within a frame record.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmRegisterFrameQuery(
        IntPtr sessionHandle,
        out IntPtr pHandle,
        [In, Out] PM_QUERY_ELEMENT[] pElements,
        ulong numElements,
        out uint pBlobSize);

    // pNumFramesToRead: in = buffer capacity (frames), out = frames actually written.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmConsumeFrames(
        IntPtr handle,
        uint processId,
        [In, Out] byte[] pBlobs,
        ref uint pNumFramesToRead);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PM_STATUS pmFreeFrameQuery(IntPtr handle);
}
