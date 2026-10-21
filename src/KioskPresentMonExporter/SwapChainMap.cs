using System.Globalization;

namespace KioskPresentMonExporter;

// The `swapchain` label (presentmon-metrics#1). A kiosk driving several screens presents
// through one swapchain per screen, and merging them multiplies rates by the screen count,
// mixes quantiles and hides one frozen screen behind the healthy ones. The raw address
// changes at every app restart, which would mint new series each time, so each swapchain
// gets a small stable ordinal instead: "0", "1", ... in first-seen order for the tracked
// process, starting again when the process changes. Past MaxSwapChains the rest share
// "other", which bounds cardinality. Also remembers when each label last presented, for
// the per-swapchain last-present gauge and the active-swapchain count.
public sealed class SwapChainMap
{
    public const string Other = "other";

    private readonly int _max;
    private readonly Dictionary<ulong, string> _labels = new();
    private readonly Dictionary<string, DateTime> _lastSeenUtc = new();
    private int _numbered;
    private uint _pid;

    public SwapChainMap(int maxSwapChains)
    {
        _max = Math.Max(1, maxSwapChains);
    }

    // Start numbering again when the tracked process changes (an app restart, or the poll
    // loop rotating to another name-match). The labels themselves ("0", "1", ...) carry on.
    public void Track(uint pid)
    {
        if (pid == _pid) return;
        _pid = pid;
        _labels.Clear();
        _numbered = 0;
    }

    // The label for a swapchain address; 0 (a PresentMon that can't report it) is "0" too.
    public string Label(ulong address)
    {
        if (_labels.TryGetValue(address, out var label)) return label;
        label = _numbered < _max ? (_numbered++).ToString(CultureInfo.InvariantCulture) : Other;
        _labels[address] = label;
        return label;
    }

    public void Seen(string label, DateTime utc) => _lastSeenUtc[label] = utc;

    // How many labels presented within `window` of `utc`.
    public int Active(DateTime utc, TimeSpan window) =>
        _lastSeenUtc.Values.Count(t => utc - t <= window);
}
