using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class SystemTrayAndDockTests
{
    private readonly ITestOutputHelper _output;

    public SystemTrayAndDockTests(ITestOutputHelper output)
    {
        _output = output;
    }
    [Fact]
    public void DockPillHasZeroPaddingAndSingleOuterRimToPreventDoubleRoundedCorners()
    {
        var xaml = XDocument.Load(DockXamlPath());
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var dockPill = xaml.Descendants(ui + "Border")
            .Single(e => (string?)e.Attribute(x + "Name") == "DockPill");

        Assert.Equal("0", (string?)dockPill.Attribute("Padding"));
        Assert.Equal("0", (string?)dockPill.Attribute("BorderThickness"));
        Assert.Equal("22", (string?)dockPill.Attribute("CornerRadius"));

        // Verify there is only ONE rounded Border with non-zero BorderThickness inside DockPill's glass stack.
        var glassClipHost = dockPill.Descendants(ui + "Grid")
            .Single(e => (string?)e.Attribute(x + "Name") == "DockGlassClipHost");

        var rimBorders = glassClipHost.Descendants(ui + "Border")
            .Where(b =>
            {
                string? bt = (string?)b.Attribute("BorderThickness");
                return !string.IsNullOrEmpty(bt) && bt != "0";
            })
            .ToList();

        Assert.Single(rimBorders);
        Assert.Equal("DockGlassRimBorder", (string?)rimBorders[0].Attribute(x + "Name"));
        Assert.Equal("0", (string?)rimBorders[0].Attribute("Margin"));

        // Verify the System Tray chevron button is wired in DockWindow.
        var trayButton = dockPill.Descendants(ui + "Border")
            .SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "SystemTrayButton");
        Assert.NotNull(trayButton);
    }

    [Fact]
    public void DockUsesClearGlassCanvasHostWithoutDarkSmokedTint()
    {
        var xaml = XDocument.Load(DockXamlPath());
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var backdropHost = xaml.Descendants(ui + "Border")
            .Single(e => (string?)e.Attribute(x + "Name") == "DockGlassBackdropHost");

        var canvas = Assert.Single(backdropHost.Elements(ui + "Canvas"));
        var backdropImage = Assert.Single(canvas.Elements(ui + "Image"));
        Assert.Equal("DockGlassBackdropImage", (string?)backdropImage.Attribute(x + "Name"));

        var tintOverlay = xaml.Descendants(ui + "Border")
            .Single(e => (string?)e.Attribute(x + "Name") == "DockGlassTintOverlay");

        var stops = tintOverlay.Descendants(ui + "GradientStop")
            .Select(s => ((string?)s.Attribute("Color")) ?? "")
            .ToList();

        Assert.DoesNotContain(stops, c => c.Equals("#5A222636", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stops, c => c.Equals("#620D0F18", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stops, c => c.EndsWith("FFFFFF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseOrderListDecodes64BitRegistryOrderEntries()
    {
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(111111111111UL).CopyTo(bytes, 0);
        BitConverter.GetBytes(222222222222UL).CopyTo(bytes, 8);

        var order = SystemTrayService.ParseOrderList(bytes);

        Assert.Equal(2, order.Count);
        Assert.Equal(0, order["111111111111"]);
        Assert.Equal(1, order["222222222222"]);
    }

    [Theory]
    [InlineData("#32768", 0u, true)]
    [InlineData("HwndWrapper[App;;123]", 0x80000000u, true)] // WS_POPUP without WS_CAPTION
    [InlineData("CabinetWClass", 0x80C00000u, false)]      // WS_POPUP + WS_CAPTION -> normal window
    [InlineData("Static", 0x00000000u, false)]
    public void IsTrayMenuWindowMatchesNativeMenusAndCaptionlessPopupsOnly(string cls, uint style, bool expected)
    {
        Assert.Equal(expected, SystemTrayService.IsTrayMenuWindow(cls, style));
    }

    [Fact]
    public void IsSameAppFamilyMatchesAppDirectoryAndRejectsSystemRoots()
    {
        Assert.True(SystemTrayService.IsSameAppFamily(
            @"C:\Apps\BitChord\helper.exe",
            @"C:\Apps\BitChord\BitChord.exe"));

        Assert.False(SystemTrayService.IsSameAppFamily(
            @"C:\Apps\Other\other.exe",
            @"C:\Program Files\SharedApp.exe"));

        Assert.False(SystemTrayService.IsSameAppFamily(
            @"C:\Windows\System32\cmd.exe",
            @"C:\Windows\explorer.exe"));
    }

    [Fact]
    public void ResolveKnownFolderPathExpandsWindowsFolderGuid()
    {
        string? resolved = SystemTrayService.ResolveKnownFolderPath(@"{F38BF404-1D43-42F2-9305-67DE0B28FC23}\explorer.exe");
        Assert.False(string.IsNullOrEmpty(resolved));
        Assert.EndsWith(@"\explorer.exe", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.False(resolved!.StartsWith('{'));
    }

    [Fact]
    public void TaskbarTrayInteractionRefCountIsBalancedAndGuardsUnderflow()
    {
        while (TaskbarIntegrationService.IsTrayInteractionActive)
            TaskbarIntegrationService.EndTrayInteraction();

        Assert.False(TaskbarIntegrationService.IsTrayInteractionActive);
        TaskbarIntegrationService.EndTrayInteraction();
        Assert.False(TaskbarIntegrationService.IsTrayInteractionActive);

        TaskbarIntegrationService.BeginTrayInteraction();
        TaskbarIntegrationService.BeginTrayInteraction();
        Assert.True(TaskbarIntegrationService.IsTrayInteractionActive);

        TaskbarIntegrationService.EndTrayInteraction();
        Assert.True(TaskbarIntegrationService.IsTrayInteractionActive);

        TaskbarIntegrationService.EndTrayInteraction();
        Assert.False(TaskbarIntegrationService.IsTrayInteractionActive);
    }

    [Fact]
    public void EnumerateLiveTrayIconsDiscoversActiveWindowsTrayApps() => SharedStaTestRunner.Run(() =>
    {
        var items = SystemTrayService.EnumerateLiveTrayIcons();
        Assert.NotNull(items);
        _output.WriteLine($"Discovered {items.Count} live tray icons:");
        foreach (var item in items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Key));
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(item.ExecutablePath));
            _output.WriteLine($"  - {item.DisplayName} (promo={item.IsPromoted}, own={item.IsOwnProcess}, icon={item.Icon != null}, key={item.Key})");
        }
    });

    private static string DockXamlPath([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../Windows/DockWindow.xaml"));
}
