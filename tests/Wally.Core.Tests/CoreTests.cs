using Wally.Core;
using Xunit;

namespace Wally.Core.Tests;

/// <summary>
/// Unit tests for Wally core logic (no Windows APIs touched).
/// 1:1 port of the verified Python suite.
/// </summary>
public class CoreTests
{
    // --- fixtures --------------------------------------------------------

    private static Monitor MakeMonitor(
        string edid, bool primary = false, int w = 1920, int h = 1080, int x = 0, int y = 0)
        => new(
            DeviceId: $@"\\?\DISPLAY#{edid}#path",
            EdidKey: edid,
            Width: w,
            Height: h,
            X: x,
            Y: y,
            IsPrimary: primary,
            FriendlyName: edid);

    private static MonitorSetup LaptopOnly()
        => new(new[] { MakeMonitor("LAPTOP_PANEL", primary: true) });

    private static MonitorSetup LaptopPlus27()
        => new(new[]
        {
            MakeMonitor("LAPTOP_PANEL", primary: true),
            MakeMonitor("DELL_27_U2720Q", w: 3840, h: 2160, x: 1920),
        });

    // --- fingerprint -----------------------------------------------------

    [Fact]
    public void Fingerprint_IsOrderIndependent()
    {
        var a = new MonitorSetup(new[] { MakeMonitor("A"), MakeMonitor("B") });
        var b = new MonitorSetup(new[] { MakeMonitor("B"), MakeMonitor("A") });
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Fingerprint_DistinguishesSetups()
    {
        Assert.NotEqual(LaptopOnly().Fingerprint, LaptopPlus27().Fingerprint);
    }

    [Fact]
    public void Fingerprint_IsStableAcrossCalls()
    {
        var setup = LaptopPlus27();
        Assert.Equal(setup.Fingerprint, setup.Fingerprint);
    }

    // --- rule resolution -------------------------------------------------

    [Fact]
    public void NewSetup_ReportsAllUnassigned()
    {
        var result = RulesEngine.ResolveSetup(new WallyConfig(), LaptopPlus27());
        Assert.True(result.IsNewSetup);
        Assert.Equal(2, result.Unassigned.Count);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void ExplicitPerMonitorRule_Resolves()
    {
        var setup = LaptopPlus27();
        var rule = new SetupRule(
            setup.Fingerprint,
            new[]
            {
                new MonitorRule("LAPTOP_PANEL", "C:/w/panel.jpg", WallpaperFit.Fill),
                new MonitorRule("DELL_27_U2720Q", "C:/w/dell.jpg", WallpaperFit.Span),
            });
        var config = new WallyConfig().WithSetupRule(rule);
        var result = RulesEngine.ResolveSetup(config, setup);

        Assert.False(result.IsNewSetup);
        Assert.Empty(result.Unassigned);
        var paths = result.Actions.Select(a => a.ImagePath).ToHashSet();
        Assert.Equal(new HashSet<string> { "C:/w/panel.jpg", "C:/w/dell.jpg" }, paths);
    }

    [Fact]
    public void DefaultImage_CoversUnlistedMonitor()
    {
        var setup = LaptopPlus27();
        var rule = new SetupRule(
            setup.Fingerprint,
            new[] { new MonitorRule("LAPTOP_PANEL", "C:/w/panel.jpg") },
            DefaultImage: "C:/w/default.jpg");
        var config = new WallyConfig().WithSetupRule(rule);
        var result = RulesEngine.ResolveSetup(config, setup);

        Assert.Empty(result.Unassigned);
        var dell = result.Actions.Single(a => a.DeviceId.Contains("DELL"));
        Assert.Equal("C:/w/default.jpg", dell.ImagePath);
    }

    // --- rotation --------------------------------------------------------

    [Fact]
    public void Rotation_PrefersUnused()
    {
        var config = new WallyConfig() with { RotationPool = new[] { "a.jpg", "b.jpg", "c.jpg" } };
        Assert.Equal("b.jpg", RulesEngine.NextRotationImage(config, new[] { "a.jpg" }));
    }

    [Fact]
    public void Rotation_WrapsWhenAllUsed()
    {
        var config = new WallyConfig() with { RotationPool = new[] { "a.jpg", "b.jpg" } };
        Assert.Equal("a.jpg", RulesEngine.NextRotationImage(config, new[] { "a.jpg", "b.jpg" }));
    }

    [Fact]
    public void Rotation_EmptyPoolReturnsNull()
    {
        Assert.Null(RulesEngine.NextRotationImage(new WallyConfig(), Array.Empty<string>()));
    }

    // --- virtual desktop assignment --------------------------------------

    [Fact]
    public void DesktopAssignment_Lookup()
    {
        var config = new WallyConfig().WithDesktopAssignment(
            new DesktopAssignment("guid-1", "C:/w/one.jpg"));
        Assert.Equal("C:/w/one.jpg", RulesEngine.ResolveDesktop(config, "guid-1")!.ImagePath);
        Assert.Null(RulesEngine.ResolveDesktop(config, "guid-2"));
    }

    // --- immutability ----------------------------------------------------

    [Fact]
    public void WithSetupRule_DoesNotMutateOriginal()
    {
        var original = new WallyConfig();
        var updated = original.WithSetupRule(new SetupRule("fp1"));
        Assert.Empty(original.SetupRules);
        Assert.Single(updated.SetupRules);
    }

    [Fact]
    public void WithMonitor_ReplacesNotDuplicates()
    {
        var rule = new SetupRule("fp");
        var r1 = rule.WithMonitor(new MonitorRule("A", "one.jpg"));
        var r2 = r1.WithMonitor(new MonitorRule("A", "two.jpg"));
        Assert.Single(r2.PerMonitor);
        Assert.Equal("two.jpg", r2.PerMonitor[0].ImagePath);
    }

    // --- persistence round-trip ------------------------------------------

    [Fact]
    public void Config_JsonRoundTrip()
    {
        var config = new WallyConfig()
            { NewDesktopBehavior = "auto_assign", RotationPool = new[] { "x.jpg", "y.jpg" } }
            .WithSetupRule(new SetupRule(
                "fp",
                new[]
                {
                    new MonitorRule("A", "a.jpg", WallpaperFit.Fit),
                    new MonitorRule("B", "b.jpg", WallpaperFit.Fill, RotationDegrees: 90),
                },
                DefaultImage: "d.jpg"))
            .WithDesktopAssignment(new DesktopAssignment("g1", "g.jpg"));

        var path = Path.Combine(Path.GetTempPath(), $"wally-test-{Guid.NewGuid():N}.json");
        try
        {
            ConfigStore.Save(config, path);
            var loaded = ConfigStore.Load(path);
            Assert.Equal(config, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFileReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wally-nope-{Guid.NewGuid():N}.json");
        Assert.Equal(new WallyConfig(), ConfigStore.Load(path));
    }

    [Fact]
    public void Load_CorruptFileThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wally-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Throws<FormatException>(() => ConfigStore.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
