using System;
using System.Windows;
using System.Windows.Interop;
using VNotch.Controllers;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsHotkeyTests
{
    [Fact]
    public void SettingsHotkey_ParsesValidGestures()
    {
        // Win+Alt+Space
        Assert.True(SettingsHotkeyController.TryParse("Win+Alt+Space", out uint mods, out uint key));
        Assert.NotEqual(0u, mods);
        Assert.NotEqual(0u, key);

        // Win+Alt+S
        Assert.True(SettingsHotkeyController.TryParse("Win+Alt+S", out mods, out key));
        Assert.NotEqual(0u, mods);
        Assert.NotEqual(0u, key);

        // Ctrl+Alt+S
        Assert.True(SettingsHotkeyController.TryParse("Ctrl+Alt+S", out mods, out key));
        Assert.NotEqual(0u, mods);
        Assert.NotEqual(0u, key);

        // Invalid
        Assert.False(SettingsHotkeyController.TryParse("InvalidHotkey", out _, out _));
        Assert.False(SettingsHotkeyController.TryParse("", out _, out _));
    }

    [Fact]
    public void NotchSettings_SettingsHotkey_DefaultAndCloneWork()
    {
        var settings = new NotchSettings();
        Assert.Equal("Win+Alt+Space", settings.SettingsHotkey);

        settings.SettingsHotkey = "Ctrl+Alt+S";
        var clone = settings.Clone();
        Assert.Equal("Ctrl+Alt+S", clone.SettingsHotkey);
        Assert.True(settings.ValueEquals(clone));

        clone.SettingsHotkey = "Win+Alt+S";
        Assert.False(settings.ValueEquals(clone));
    }

    [Fact]
    public void SettingsHotkeyController_LifecycleAndRegistration() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window();
        new WindowInteropHelper(window).EnsureHandle();
        int triggered = 0;
        using var controller = new SettingsHotkeyController(window, () => triggered++);

        try
        {
            // Empty / None unregisters without error
            controller.Apply("");
            Assert.Null(controller.RegistrationError);
            Assert.False(controller.IsRegistered);

            controller.Apply("None");
            Assert.Null(controller.RegistrationError);
            Assert.False(controller.IsRegistered);

            // Invalid format reports error
            controller.Apply("Invalid-Key-Combo");
            Assert.NotNull(controller.RegistrationError);

            // Clearing restores clean state
            controller.Apply("");
            Assert.Null(controller.RegistrationError);
        }
        finally
        {
            window.Close();
        }
    });
}
