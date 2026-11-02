using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class AppResourceMonitorServiceTests
{
    [Fact]
    public void Instance_IsNotNull()
    {
        var service = AppResourceMonitorService.Instance;
        Assert.NotNull(service);
    }

    [Fact]
    public void SampleCurrentSnapshot_PopulatesValidMetrics()
    {
        var service = AppResourceMonitorService.Instance;
        var snapshot = service.SampleCurrentSnapshot();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.AppCpuPercent >= 0.0 && snapshot.AppCpuPercent <= 100.0);
        Assert.True(snapshot.AppRamBytes > 0);
        Assert.True(snapshot.MainProcRamBytes > 0);
        Assert.True(snapshot.SystemRamTotalBytes > 0);
        Assert.True(snapshot.SystemRamPercent >= 0.0 && snapshot.SystemRamPercent <= 100.0);
        Assert.True(snapshot.ThreadCount > 0);
    }

    [Fact]
    public void FormatBytes_ProducesReadableOutput()
    {
        ulong oneMb = 1024 * 1024;
        Assert.Equal("1.0 MB", AppResourceMonitorService.FormatBytes(oneMb));

        ulong fiftyMb = 50 * 1024 * 1024;
        Assert.Equal("50.0 MB", AppResourceMonitorService.FormatBytes(fiftyMb));

        ulong twoGb = 2UL * 1024 * 1024 * 1024;
        Assert.Equal("2.0 GB", AppResourceMonitorService.FormatBytes(twoGb));
    }

    [Fact]
    public void StartAndStop_DoesNotThrow()
    {
        var service = AppResourceMonitorService.Instance;
        service.Start();
        service.Stop();
    }

    [Fact]
    public void AppMemoryOptimizer_OptimizeMemory_DoesNotThrow()
    {
        AppMemoryOptimizer.OptimizeMemory(aggressive: true);
        var snapshot = AppResourceMonitorService.Instance.SampleCurrentSnapshot();
        Assert.NotNull(snapshot);
        Assert.True(snapshot.MainProcRamBytes > 0);
    }

    [Fact]
    public void SpotlightCatalog_ContainsAppMonitorEntry()
    {
        var entry = SpotlightSystemCatalog.Find("vnotch-action:appMonitor");
        Assert.NotNull(entry);
        Assert.Equal(SpotlightResultKind.SystemAction, entry.Kind);
        Assert.False(entry.RequiresConfirmation);

        var item = entry.CreateItem();
        Assert.True(SpotlightLauncher.IsValidTarget(item));
        Assert.Contains("Monitor", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pinn-Notch", item.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("cpu")]
    [InlineData("ram")]
    [InlineData("gpu")]
    [InlineData("resource")]
    [InlineData("performa")]
    [InlineData("performance")]
    public async Task SpotlightSearch_FindsAppMonitorByKeywords(string keyword)
    {
        var provider = new SystemCommandProvider();
        var results = await provider.SearchAsync(keyword, 10, CancellationToken.None);

        Assert.NotEmpty(results);
        var appMonitorItem = results.FirstOrDefault(r => r.Target == "vnotch-action:appMonitor");
        Assert.NotNull(appMonitorItem);
        Assert.True(appMonitorItem.Score > 0);
    }
}
