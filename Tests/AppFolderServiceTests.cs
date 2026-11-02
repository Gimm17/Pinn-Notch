using System;
using System.IO;
using System.Linq;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class AppFolderServiceTests
{
    [Fact]
    public void DefaultFolders_ArePopulatedWithCommonApps()
    {
        var service = AppFolderService.Instance;
        Assert.NotNull(service.Folders);
        Assert.True(service.Folders.Count >= 3, "Expected at least 3 default app folders");

        var browserFolder = service.Folders.FirstOrDefault(f => f.Name.Contains("Browser", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(browserFolder);
        Assert.NotEmpty(browserFolder.Apps);
    }

    [Fact]
    public void AddFolder_And_RenameFolder_WorksAndPersists()
    {
        var service = AppFolderService.Instance;
        string testFolder = "TestFolder_" + Guid.NewGuid().ToString("N")[..6];

        service.AddFolder(testFolder);
        var found = service.Folders.FirstOrDefault(f => f.Name == testFolder);
        Assert.NotNull(found);

        string renamed = testFolder + "_Renamed";
        service.RenameFolder(found.Id, renamed);

        var foundRenamed = service.Folders.FirstOrDefault(f => f.Id == found.Id);
        Assert.NotNull(foundRenamed);
        Assert.Equal(renamed, foundRenamed.Name);

        // Cleanup
        service.DeleteFolder(found.Id);
        Assert.Null(service.Folders.FirstOrDefault(f => f.Id == found.Id));
    }

    [Fact]
    public void AddAppToFolder_And_RemoveApp_WorksCorrectly()
    {
        var service = AppFolderService.Instance;
        string testFolder = "AppTestFolder_" + Guid.NewGuid().ToString("N")[..6];
        service.AddFolder(testFolder);
        var folder = service.Folders.First(f => f.Name == testFolder);

        service.AddAppToFolder(folder.Id, "Test App", "test.exe");
        var updated = service.Folders.First(f => f.Id == folder.Id);
        var app = updated.Apps.FirstOrDefault(a => a.Name == "Test App");
        Assert.NotNull(app);
        Assert.Equal("test.exe", app.TargetPath);

        service.RemoveAppFromFolder(folder.Id, app.Id);
        var afterRemove = service.Folders.First(f => f.Id == folder.Id);
        Assert.DoesNotContain(afterRemove.Apps, a => a.Id == app.Id);

        // Cleanup
        service.DeleteFolder(folder.Id);
    }
}
