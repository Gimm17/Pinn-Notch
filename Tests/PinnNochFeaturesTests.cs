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
}
