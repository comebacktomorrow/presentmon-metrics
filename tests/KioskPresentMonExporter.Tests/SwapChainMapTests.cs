using KioskPresentMonExporter;
using Xunit;

// The `swapchain` label (presentmon-metrics#1): stable ordinals per tracked process, capped.
public class SwapChainMapTests
{
    [Fact]
    public void Numbers_swapchains_in_first_seen_order()
    {
        var m = new SwapChainMap(4);
        m.Track(100);
        Assert.Equal("0", m.Label(0xA000));
        Assert.Equal("1", m.Label(0xB000));
        Assert.Equal("0", m.Label(0xA000));   // same swapchain, same label
        Assert.Equal("2", m.Label(0xC000));
        Assert.Equal("1", m.Label(0xB000));
    }

    [Fact]
    public void Past_the_cap_the_rest_share_other()
    {
        var m = new SwapChainMap(2);
        m.Track(100);
        Assert.Equal("0", m.Label(1));
        Assert.Equal("1", m.Label(2));
        Assert.Equal(SwapChainMap.Other, m.Label(3));
        Assert.Equal(SwapChainMap.Other, m.Label(4));
        Assert.Equal("1", m.Label(2));         // the numbered ones keep their labels
    }

    [Fact]
    public void A_new_process_starts_numbering_again()
    {
        // The address changes at every app restart; the labels must not.
        var m = new SwapChainMap(4);
        m.Track(100);
        m.Label(0xA000); m.Label(0xB000);
        m.Track(200);
        Assert.Equal("0", m.Label(0xF000));
        Assert.Equal("1", m.Label(0xE000));
    }

    [Fact]
    public void Tracking_the_same_process_again_keeps_the_numbers()
    {
        var m = new SwapChainMap(4);
        m.Track(100);
        m.Label(0xA000); m.Label(0xB000);
        m.Track(100);
        Assert.Equal("1", m.Label(0xB000));
        Assert.Equal("2", m.Label(0xC000));
    }

    [Fact]
    public void No_address_reported_is_swapchain_0()
    {
        // A PresentMon without SWAP_CHAIN_ADDRESS reads 0 for every frame: one series, as before.
        var m = new SwapChainMap(4);
        m.Track(100);
        Assert.Equal("0", m.Label(0));
        Assert.Equal("0", m.Label(0));
    }

    [Fact]
    public void The_cap_is_at_least_one()
    {
        var m = new SwapChainMap(0);
        m.Track(1);
        Assert.Equal("0", m.Label(7));
        Assert.Equal(SwapChainMap.Other, m.Label(8));
    }

    [Fact]
    public void Active_counts_swapchains_that_presented_within_the_window()
    {
        var m = new SwapChainMap(4);
        var t0 = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        m.Seen("0", t0);
        m.Seen("1", t0.AddSeconds(20));
        m.Seen("2", t0.AddSeconds(25));
        var window = TimeSpan.FromSeconds(30);
        Assert.Equal(3, m.Active(t0.AddSeconds(30), window));
        Assert.Equal(2, m.Active(t0.AddSeconds(31), window));   // "0" went quiet: one frozen screen shows
        Assert.Equal(0, m.Active(t0.AddMinutes(5), window));
    }
}
