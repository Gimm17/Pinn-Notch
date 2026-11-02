using System;
using System.IO;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class PinnNotchFeaturesTests
{
    [Fact]
    public void AppPaths_UsesPinnNotchDirectory()
    {
        Assert.Equal("Pinn-Notch", AppPaths.DirectoryName);
        Assert.Equal("V-Notch", AppPaths.LegacyDirectoryName);
        Assert.EndsWith("Pinn-Notch", AppPaths.RoamingAppDataRoot);
    }

    [Fact]
    public void SingleInstanceGuard_UsesPinnNotchMutex()
    {
        string mutexName = SingleInstanceGuard.GetCurrentUserMutexName();
        Assert.StartsWith(@"Local\PinnNotch_SingleInstance_", mutexName);
    }

    [Fact]
    public void NotchSettings_PinnDefaultsAreSet()
    {
        var settings = new NotchSettings();
        Assert.True(settings.EnablePinnMascot);
        Assert.Equal("Right", settings.PinnMascotPosition);
        Assert.True(settings.PinnMascotReactToMusic);
        Assert.Equal("SakuraPastel", settings.PinnTheme);
        Assert.True(settings.EnableGhostMode);
        Assert.Equal(0.45, settings.GhostModeOpacity);
        Assert.True(settings.EnableUsbNotifications);
        Assert.True(settings.EnableBudsBattery);
    }

    [Theory]
    [InlineData("SakuraPastel")]
    [InlineData("LavenderDream")]
    [InlineData("MatchaLatte")]
    [InlineData("CatppuccinMocha")]
    [InlineData("ClassicBlack")]
    public void UiPalette_AllPinnThemesCanBeApplied(string themeId)
    {
        UiPalette.ApplyTheme(themeId);
        Assert.Equal(themeId, UiPalette.CurrentTheme.Id);
        Assert.NotNull(UiPalette.PrimaryBrush);
        Assert.NotNull(UiPalette.SecondaryBrush);
        Assert.NotNull(UiPalette.AccentBrush);
    }

    [Fact]
    public void BluetoothDeviceInfo_StoresBatteryPercentage()
    {
        var info = new BluetoothDeviceInfo
        {
            Id = "test-buds",
            Name = "AirPods Pro",
            DeviceType = BluetoothDeviceType.Headphones,
            BatteryPercentage = 85
        };

        Assert.Equal(85, info.BatteryPercentage);
        Assert.Equal("AirPods Pro", info.Name);
    }

    [Fact]
    public void DragOutFile_GeneratesFileDropAndUnicodeText_WithValidDiskPaths() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Directory.CreateTempSubdirectory("pinn-drag-file-").FullName;
        try
        {
            string testDocPath = Path.Combine(root, "contract.pdf");
            File.WriteAllText(testDocPath, "%PDF-1.4 dummy content");

            await using var controller = new Controllers.ClipboardHistoryController(
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                new Services.Clipboard.ClipboardHistoryStore(Path.Combine(root, "history")));

            var dropData = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { testDocPath });
            await controller.ImportDropAsync(dropData, "All");

            var entries = controller.Store.Snapshot();
            var fileEntry = Assert.Single(entries, e => e.Kind == ClipboardKind.File);
            Assert.Equal("contract.pdf", fileEntry.Title);

            var paths = new System.Collections.Generic.List<string>();
            var (data, personal) = await controller.CreateSelectionDataAsync([fileEntry], paths);

            Assert.False(personal);
            Assert.NotEmpty(paths);
            Assert.True(data.GetDataPresent(System.Windows.DataFormats.FileDrop));
            Assert.True(data.GetDataPresent(System.Windows.DataFormats.UnicodeText));

            string[] droppedFiles = (string[])data.GetData(System.Windows.DataFormats.FileDrop)!;
            string exportedPath = Assert.Single(droppedFiles);
            Assert.True(File.Exists(exportedPath));
            Assert.EndsWith("contract.pdf", exportedPath, StringComparison.OrdinalIgnoreCase);

            string textPath = (string)data.GetData(System.Windows.DataFormats.UnicodeText)!;
            Assert.Equal(exportedPath, textPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    });

    [Fact]
    public void DragOutImage_GeneratesFileDropAndBitmap_ForWordAndChatPaste() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Directory.CreateTempSubdirectory("pinn-drag-img-").FullName;
        try
        {
            string imagePath = Path.Combine(root, "screenshot.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8)));
            using (var stream = File.Create(imagePath)) encoder.Save(stream);

            await using var controller = new Controllers.ClipboardHistoryController(
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                new Services.Clipboard.ClipboardHistoryStore(Path.Combine(root, "history")));

            var dropData = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { imagePath });
            await controller.ImportDropAsync(dropData, "All");

            var entries = controller.Store.Snapshot();
            var imgEntry = Assert.Single(entries, e => e.Kind == ClipboardKind.Image);

            var paths = new System.Collections.Generic.List<string>();
            var (data, _) = await controller.CreateSelectionDataAsync([imgEntry], paths);

            Assert.True(data.GetDataPresent(System.Windows.DataFormats.FileDrop));
            Assert.True(data.GetDataPresent(System.Windows.DataFormats.Bitmap));
            Assert.NotNull(data.GetData(System.Windows.DataFormats.Bitmap));

            string[] droppedFiles = (string[])data.GetData(System.Windows.DataFormats.FileDrop)!;
            string exportedPath = Assert.Single(droppedFiles);
            Assert.True(File.Exists(exportedPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    });

    [Fact]
    public void NotchSettings_DockDefaultsAreConfigured()
    {
        var settings = new NotchSettings();
        Assert.Equal("NotchAndDock", settings.LayoutMode);
        Assert.True(settings.EnableDockMagnification);
        Assert.True(settings.EnableDockRunningIndicators);
    }

    [Fact]
    public void NotchSettings_AutoHideDefaultsAndClampingAreConfigured()
    {
        var settings = new NotchSettings();
        Assert.Equal("Smart", settings.DockAutoHideMode);
        Assert.Equal(4, settings.DockRevealHeightPx);
        Assert.Equal(0, settings.DockRevealExtraWidthPx);
        Assert.Equal(120, settings.DockRevealDelayMs);
        Assert.Equal(400, settings.DockHideDelayMs);

        Assert.Equal("Smart", settings.NotchAutoHideMode);
        Assert.Equal(3, settings.NotchRevealHeightPx);
        Assert.Equal(0, settings.NotchRevealExtraWidthPx);
        Assert.Equal(100, settings.NotchRevealDelayMs);
        Assert.Equal(500, settings.NotchHideDelayMs);

        Assert.True(settings.NotchHiddenPeekEnabled);
        Assert.Equal(2, settings.NotchHiddenPeekHeightPx);
        Assert.True(settings.NotchNotificationRevealEnabled);
        Assert.Equal(2500, settings.NotchNotificationRevealDurationMs);

        // Clamping test
        settings.DockRevealHeightPx = 999;
        Assert.Equal(40, settings.DockRevealHeightPx);
        settings.DockRevealHeightPx = -10;
        Assert.Equal(1, settings.DockRevealHeightPx);

        settings.NotchHiddenPeekHeightPx = 999;
        Assert.Equal(12, settings.NotchHiddenPeekHeightPx);
        settings.NotchHiddenPeekHeightPx = 0;
        Assert.Equal(1, settings.NotchHiddenPeekHeightPx);
    }

    [Fact]
    public void TriggerZoneCalculator_CalculatesPreciseNotchZone()
    {
        var zone = TriggerZoneCalculator.CalculateNotchTriggerZone(0, 1920, 200, 3, 0);
        Assert.Equal(860, zone.Left);
        Assert.Equal(0, zone.Top);
        Assert.Equal(200, zone.Width);
        Assert.Equal(3, zone.Height);

        Assert.True(TriggerZoneCalculator.IsPointInZone(zone, new System.Windows.Point(900, 1)));
        Assert.False(TriggerZoneCalculator.IsPointInZone(zone, new System.Windows.Point(800, 1))); // left of notch
        Assert.False(TriggerZoneCalculator.IsPointInZone(zone, new System.Windows.Point(900, 5))); // below 3px trigger strip
    }

    [Fact]
    public void TriggerZoneCalculator_CalculatesPreciseDockZone()
    {
        var zone = TriggerZoneCalculator.CalculateDockTriggerZone(670, 580, 1080, 4, 0);
        Assert.Equal(670, zone.Left);
        Assert.Equal(1076, zone.Top);
        Assert.Equal(580, zone.Width);
        Assert.Equal(4, zone.Height);

        Assert.True(TriggerZoneCalculator.IsPointInZone(zone, new System.Windows.Point(700, 1078)));
        Assert.False(TriggerZoneCalculator.IsPointInZone(zone, new System.Windows.Point(600, 1078))); // left of dock
        Assert.False(TriggerZoneCalculator.IsPointInZone(zone, new System.Windows.Point(700, 1070))); // above 4px trigger strip
    }

    [Fact]
    public void SpringValue_UpdatesTowardsTargetSmoothly()
    {
        var spring = new Controllers.SpringValue(1.0)
        {
            Target = 1.30
        };

        bool moving = spring.Update(0.016, 28.0);
        Assert.True(moving);
        Assert.True(spring.Current > 1.0 && spring.Current < 1.30);

        // Simulate convergence
        for (int i = 0; i < 30; i++)
        {
            moving = spring.Update(0.016, 28.0);
        }

        Assert.False(moving);
        Assert.Equal(1.30, spring.Current);
    }
}
