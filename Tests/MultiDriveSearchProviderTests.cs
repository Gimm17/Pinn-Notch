using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class MultiDriveSearchProviderTests : IDisposable
{
    private readonly string _cacheFile;

    public MultiDriveSearchProviderTests()
    {
        _cacheFile = Path.Combine(Path.GetTempPath(), $"pinn-cache-{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_cacheFile))
            {
                File.Delete(_cacheFile);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task SearchAsync_FindsFilesAndFoldersInNestedDirectories()
    {
        string targetFile = @"D:\CODINGAN\GameDev\DragonBall_Story.docx";
        string alproFile = @"E:\Kuliah\Tugas\Quiz_Alpro_2026.pdf";
        string alproFolder = @"E:\Kuliah\Tugas_Alpro";

        using var provider = new MultiDriveSearchProvider(_cacheFile);

        var snapshotField = typeof(MultiDriveSearchProvider)
            .GetField("_snapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var entries = new[]
        {
            new MultiDriveSearchProvider.IndexedEntry("DragonBall_Story.docx", targetFile, false),
            new MultiDriveSearchProvider.IndexedEntry("Quiz_Alpro_2026.pdf", alproFile, false),
            new MultiDriveSearchProvider.IndexedEntry("Tugas_Alpro", alproFolder, true),
        };
        snapshotField?.SetValue(provider, entries);

        // Test 1: Search file
        var results = await provider.SearchAsync("Dragon", 10, CancellationToken.None);
        Assert.NotEmpty(results);
        Assert.Equal("DragonBall_Story.docx", results[0].Title);
        Assert.Equal(SpotlightResultKind.File, results[0].Kind);
        Assert.Equal(targetFile, results[0].Target);

        // Test 2: Search folder
        var folderResults = await provider.SearchAsync("Alpro", 10, CancellationToken.None);
        Assert.Contains(folderResults, r => r.Kind == SpotlightResultKind.Folder && r.Title == "Tugas_Alpro");
        Assert.Contains(folderResults, r => r.Kind == SpotlightResultKind.File && r.Title == "Quiz_Alpro_2026.pdf");
    }

    [Fact]
    public async Task SearchAsync_HandlesMultiWordTokens()
    {
        using var provider = new MultiDriveSearchProvider(_cacheFile);

        var snapshotField = typeof(MultiDriveSearchProvider)
            .GetField("_snapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        string p1 = @"D:\Videos\Anime\Super_Dragon_Ball_Z.mp4";
        string p2 = @"D:\Videos\Anime\Dragon_Quest.mp4";
        string p3 = @"D:\Videos\Sports\Ball_Game.mp4";

        var entries = new[]
        {
            new MultiDriveSearchProvider.IndexedEntry("Super_Dragon_Ball_Z.mp4", p1, false),
            new MultiDriveSearchProvider.IndexedEntry("Dragon_Quest.mp4", p2, false),
            new MultiDriveSearchProvider.IndexedEntry("Ball_Game.mp4", p3, false),
        };
        snapshotField?.SetValue(provider, entries);

        // Searching "dragon ball" should match p1, but not p2 or p3
        var results = await provider.SearchAsync("dragon ball", 10, CancellationToken.None);
        Assert.Single(results);
        Assert.Equal("Super_Dragon_Ball_Z.mp4", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyWhenQueryIsTooShortOrEmpty()
    {
        using var provider = new MultiDriveSearchProvider(_cacheFile);
        var resultsShort = await provider.SearchAsync("a", 10, CancellationToken.None);
        Assert.Empty(resultsShort);

        var resultsEmpty = await provider.SearchAsync("", 10, CancellationToken.None);
        Assert.Empty(resultsEmpty);
    }
}
