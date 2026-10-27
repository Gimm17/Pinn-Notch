using System;
using System.Collections.Generic;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class TaskbarIntegrationTests
{
    [Fact]
    public void ParseColorToABGR_ConvertsCorrectly()
    {
        // Fully transparent black (Alpha = 0)
        int clearColor = TaskbarIntegrationService.ParseColorToABGR("#000000", 0.0);
        Assert.Equal(0, clearColor);

        // Solid White (#FFFFFF, Alpha = 1.0) -> 0xFFFFFFFF
        int solidWhite = TaskbarIntegrationService.ParseColorToABGR("#FFFFFF", 1.0);
        Assert.Equal(unchecked((int)0xFFFFFFFF), solidWhite);

        // Pure Red (#FF0000, Alpha = 1.0) -> In ABGR: A=0xFF, B=0x00, G=0x00, R=0xFF => 0xFF0000FF
        int solidRed = TaskbarIntegrationService.ParseColorToABGR("#FF0000", 1.0);
        Assert.Equal(unchecked((int)0xFF0000FF), solidRed);

        // Pure Blue (#0000FF, Alpha = 1.0) -> In ABGR: A=0xFF, B=0xFF, G=0x00, R=0x00 => 0xFFFF0000
        int solidBlue = TaskbarIntegrationService.ParseColorToABGR("#0000FF", 1.0);
        Assert.Equal(unchecked((int)0xFFFF0000), solidBlue);

        // 50% opacity
        int halfBlue = TaskbarIntegrationService.ParseColorToABGR("#0000FF", 0.5);
        byte alpha = (byte)((halfBlue >> 24) & 0xFF);
        Assert.InRange(alpha, 126, 129);
    }

    [Fact]
    public void NotchSettings_TaskbarProperties_CloneAndValueEqualsWork()
    {
        var settings = new NotchSettings
        {
            EnableTransparentTaskbar = true,
            TaskbarStyle = "Acrylic",
            TaskbarColor = "#336699",
            TaskbarOpacity = 0.65,
            TaskbarDynamicMaximized = false,
            EnableFreezeTaskbar = true,
            TaskbarFreezeMode = "ClickThrough"
        };

        var clone = settings.Clone();

        Assert.True(clone.EnableTransparentTaskbar);
        Assert.Equal("Acrylic", clone.TaskbarStyle);
        Assert.Equal("#336699", clone.TaskbarColor);
        Assert.Equal(0.65, clone.TaskbarOpacity);
        Assert.False(clone.TaskbarDynamicMaximized);
        Assert.True(clone.EnableFreezeTaskbar);
        Assert.Equal("ClickThrough", clone.TaskbarFreezeMode);

        Assert.True(settings.ValueEquals(clone));

        clone.TaskbarStyle = "Clear";
        Assert.False(settings.ValueEquals(clone));
    }

    [Fact]
    public void TaskbarIntegrationService_LifecycleAndApplyDoesNotThrow()
    {
        using var service = new TaskbarIntegrationService();

        // Null settings safety
        service.ApplySettings(null!);

        // Default disabled settings
        var settings = new NotchSettings
        {
            EnableTransparentTaskbar = false,
            EnableFreezeTaskbar = false
        };
        service.ApplySettings(settings);

        // All taskbar windows query works safely
        var taskbars = TaskbarIntegrationService.GetAllTaskbarWindows();
        Assert.NotNull(taskbars);

        // Restoration works safely
        service.RestoreTaskbars();
    }
}
