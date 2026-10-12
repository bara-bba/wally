using Wally.Core;
using Wally.Platform;
using Monitor = Wally.Core.Monitor;

namespace Wally.App;

/// <summary>
/// A test-only monitor provider that reports a simulated multi-display
/// arrangement, so the Monitors view can be exercised with 2+ screens on a
/// machine that only has one. Selected with the hidden <c>--simulate</c>
/// command-line flag; the real COM backend is never involved in enumeration
/// when this is active.
/// </summary>
public sealed class FakeMonitorProvider : IMonitorProvider
{
    private readonly MonitorSetup _setup;

    public FakeMonitorProvider(MonitorSetup setup) => _setup = setup;

    public MonitorSetup CurrentSetup() => _setup;

    /// <summary>A plausible laptop + external arrangement: 1920×1080 laptop at
    /// origin, a 3840×2160 4K panel to its right, and a rotated 1080×1920
    /// portrait monitor further right.</summary>
    public static FakeMonitorProvider TwoPlusDisplays()
    {
        var monitors = new[]
        {
            new Monitor(
                DeviceId: "SIM-LAPTOP",
                EdidKey: "SIM_LAPTOP_1080",
                Width: 1920, Height: 1080, X: 0, Y: 520,
                IsPrimary: true, FriendlyName: "Laptop (simulated)"),
            new Monitor(
                DeviceId: "SIM-4K",
                EdidKey: "SIM_DELL_4K",
                Width: 3840, Height: 2160, X: 1920, Y: 0,
                IsPrimary: false, FriendlyName: "4K monitor (simulated)"),
            new Monitor(
                DeviceId: "SIM-PORTRAIT",
                EdidKey: "SIM_LG_PORTRAIT",
                Width: 1080, Height: 1920, X: 5760, Y: 120,
                IsPrimary: false, FriendlyName: "Portrait (simulated)"),
        };

        return new FakeMonitorProvider(new MonitorSetup(monitors));
    }
}
