using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class MemoryOptimizationTests
{
    [Fact]
    public void ShouldExcludePathFiltersCacheHiddenAppDataAndTempPathsWhileKeepingUserFiles()
    {
        // Excluded system, cache, VCS, AppData, dot-dirs, and binary extensions
        Assert.True(MultiDriveSearchProvider.ShouldExcludePath(@"C:\Users\Lyvn\AppData\Local\Google\Chrome\User Data\Default\Cache\data_1", false));
        Assert.True(MultiDriveSearchProvider.ShouldExcludePath(@"C:\Users\Lyvn\.gemini\antigravity\scratch\tmp.txt", false));
        Assert.True(MultiDriveSearchProvider.ShouldExcludePath(@"D:\Projects\app\node_modules\lodash\index.js", false));
        Assert.True(MultiDriveSearchProvider.ShouldExcludePath(@"D:\Projects\app\.git\objects\ab\cd1234", false));
        Assert.True(MultiDriveSearchProvider.ShouldExcludePath(@"E:\$Recycle.Bin\S-1-5-21\file.zip", false));
        Assert.True(MultiDriveSearchProvider.ShouldExcludePath(@"D:\Games\SomeApp\engine.dll", false));
        Assert.True(MultiDriveSearchProvider.ShouldExcludePath(@"E:\Downloads\partial.tmp", false));
        Assert.True(MultiDriveSearchProvider.ShouldExcludePath(@"C:\Windows\System32\notepad.exe", false));

        // Allowed user documents and nested project files across C:, D:, E:
        Assert.False(MultiDriveSearchProvider.ShouldExcludePath(@"C:\Users\Lyvn\Documents\Laporan_2026.pdf", false));
        Assert.False(MultiDriveSearchProvider.ShouldExcludePath(@"D:\Multimedia\Video\Edit_Final.mp4", false));
        Assert.False(MultiDriveSearchProvider.ShouldExcludePath(@"E:\Coding\Pinn-Notch\README.md", false));
        Assert.False(MultiDriveSearchProvider.ShouldExcludePath(@"D:\Archive\2026\Skripsi", true));
    }

    [Fact]
    public async Task CompactArenaIndexesSearchesReloadsMigratesLegacyCacheAndHandlesLiveWatcherMutations()
    {
        string suiteBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestScratch"));
        string tempRoot = Path.Combine(suiteBase, "pinn-mdi-test-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(tempRoot, "DriveD");
        string cacheFile = Path.Combine(tempRoot, "multidrive_index.dat");

        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "Work", "Reports"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "Work", "node_modules", "pkg"));
            Directory.CreateDirectory(Path.Combine(dataRoot, ".gemini", "cache"));

            await File.WriteAllTextAsync(Path.Combine(dataRoot, "Work", "Reports", "Q4 Budget Summary.xlsx"), "data");
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "Work", "Reports", "Design Notes.md"), "data");
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "Work", "Reports", "ignored.tmp"), "tmp");
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "Work", "node_modules", "pkg", "index.js"), "js");
            await File.WriteAllTextAsync(Path.Combine(dataRoot, ".gemini", "cache", "secret.txt"), "hidden");

            using (var provider = new MultiDriveSearchProvider(cacheFile, new[] { dataRoot }, enableWatchers: false))
            {
                await provider.WarmupAsync();
                Assert.True(provider.IndexedCount >= 4); // Work, Reports, Q4 Budget Summary.xlsx, Design Notes.md

                var hits = await provider.SearchAsync("budget reports", 10, CancellationToken.None);
                Assert.Contains(hits, h => h.Title == "Q4 Budget Summary.xlsx");

                var excludedHits = await provider.SearchAsync("ignored", 10, CancellationToken.None);
                Assert.DoesNotContain(excludedHits, h => h.Title == "ignored.tmp");

                var nodeHits = await provider.SearchAsync("index.js", 10, CancellationToken.None);
                Assert.Empty(nodeHits);

                var hiddenHits = await provider.SearchAsync("secret.txt", 10, CancellationToken.None);
                Assert.Empty(hiddenHits);
            }

            Assert.True(File.Exists(cacheFile));

            // Reload PINNMDI2 compact cache cleanly
            using (var reloaded = new MultiDriveSearchProvider(cacheFile, new[] { dataRoot }, enableWatchers: false))
            {
                await reloaded.WarmupAsync();
                Assert.True(reloaded.WasCacheLoadedFresh);
                var hits = await reloaded.SearchAsync("Design Notes", 10, CancellationToken.None);
                Assert.Contains(hits, h => h.Title == "Design Notes.md");
            }

            // Write a legacy PINNMDI1 cache with excluded paths and verify migration purges them into PINNMDI2
            string legacyCache = Path.Combine(tempRoot, "legacy.dat");
            using (var stream = new FileStream(legacyCache, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write("PINNMDI1");
                writer.Write(3);
                writer.Write(false);
                writer.Write(Path.Combine(dataRoot, "Work", "Reports", "Q4 Budget Summary.xlsx"));
                writer.Write(false);
                writer.Write(Path.Combine(dataRoot, "Work", "node_modules", "pkg", "index.js"));
                writer.Write(false);
                writer.Write(@"C:\Users\Lyvn\AppData\Local\Temp\junk.dat");
            }

            using (var migrated = new MultiDriveSearchProvider(legacyCache, Array.Empty<string>(), enableWatchers: false))
            {
                await migrated.WarmupAsync();
                Assert.True(migrated.WasCacheLoadedFresh);
                var budget = await migrated.SearchAsync("Q4 Budget", 10, CancellationToken.None);
                Assert.Contains(budget, h => h.Title == "Q4 Budget Summary.xlsx");

                var junk = await migrated.SearchAsync("junk", 10, CancellationToken.None);
                Assert.Empty(junk);
            }

            using (var verifyHeaderStream = new FileStream(legacyCache, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var verifyReader = new BinaryReader(verifyHeaderStream))
            {
                Assert.Equal("PINNMDI2", verifyReader.ReadString());
            }

            // Verify live FileSystemWatcher Created -> Renamed -> Deleted mutations
            using (var liveProvider = new MultiDriveSearchProvider(cacheFile, new[] { dataRoot }, enableWatchers: true))
            {
                await liveProvider.WarmupAsync();
                string createdPath = Path.Combine(dataRoot, "Work", "Reports", "LiveCreatedAlpha.txt");
                string renamedPath = Path.Combine(dataRoot, "Work", "Reports", "LiveRenamedBeta.txt");

                await File.WriteAllTextAsync(createdPath, "hello");
                await WaitForSearchStateAsync(liveProvider, "LiveCreatedAlpha", expectFound: true);

                File.Move(createdPath, renamedPath);
                await WaitForSearchStateAsync(liveProvider, "LiveRenamedBeta", expectFound: true);
                await WaitForSearchStateAsync(liveProvider, "LiveCreatedAlpha", expectFound: false);

                File.Delete(renamedPath);
                await WaitForSearchStateAsync(liveProvider, "LiveRenamedBeta", expectFound: false);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
                if (Directory.Exists(suiteBase) && !Directory.EnumerateFileSystemEntries(suiteBase).Any())
                    Directory.Delete(suiteBase, recursive: false);
            }
            catch
            {
            }
        }
    }

    private static async Task WaitForSearchStateAsync(
        MultiDriveSearchProvider provider,
        string query,
        bool expectFound)
    {
        for (int i = 0; i < 40; i++)
        {
            var results = await provider.SearchAsync(query, 10, CancellationToken.None);
            bool found = results.Count > 0;
            if (found == expectFound)
                return;
            await Task.Delay(50);
        }

        var finalResults = await provider.SearchAsync(query, 10, CancellationToken.None);
        Assert.Equal(expectFound, finalResults.Count > 0);
    }
}
