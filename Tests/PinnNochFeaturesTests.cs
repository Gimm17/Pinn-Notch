using System;
using System.IO;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class PinnNochFeaturesTests
{
    [Fact]
    public void AppPaths_UsesPinnNochDirectory()
    {
        Assert.Equal("Pinn-Noch", AppPaths.DirectoryName);
        Assert.Equal("V-Notch", AppPaths.LegacyDirectoryName);
        Assert.EndsWith("Pinn-Noch", AppPaths.RoamingAppDataRoot);
    }

    [Fact]
    public void SingleInstanceGuard_UsesPinnNochMutex()
    {
        string mutexName = SingleInstanceGuard.GetCurrentUserMutexName();
        Assert.StartsWith(@"Local\PinnNoch_SingleInstance_", mutexName);
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
}
