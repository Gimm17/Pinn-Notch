using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Controllers;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed class MultiDriveSearchProvider : ISpotlightProvider, IDisposable
{
    internal readonly struct IndexedEntry
    {
        public readonly string Name;
        public readonly string FullPath;
        public readonly bool IsDirectory;

        public IndexedEntry(string name, string fullPath, bool isDirectory)
        {
            Name = name;
            FullPath = fullPath;
            IsDirectory = isDirectory;
        }
    }

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "WpSystem",
        "WindowsApps", "WUDownloadCache", "node_modules", ".git", ".svn", ".vs", ".vscode",
        ".idea", "__pycache__", ".pnpm-store", "obj", "bin", ".nuget", "android-sdk", "WSL",
        "AppData", "Temp", "Cache", "Caches", "npm-cache", "npm-global", "vendor",
        "3uToolsV3", "3uTools9"
    };

    private static readonly HashSet<string> ExcludedCDriveRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "MSOCache"
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".sys", ".drv", ".ocx", ".pdb", ".obj", ".mui", ".cat",
        ".tmp", ".temp", ".cache", ".etl", ".manifest"
    };

    private const string CacheFileHeader = "PINNMDI1";
    private readonly string _cacheFilePath;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _initTask;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _snapshotLock = new();

    private volatile IndexedEntry[] _snapshot = Array.Empty<IndexedEntry>();
    private bool _disposed;

    public bool IsAvailable => true;
    public bool IsInstant => true;

    internal Func<string, bool>? IsExcluded { get; set; }

    public MultiDriveSearchProvider()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "V-Notch", "multidrive_index.dat"))
    {
    }

    internal MultiDriveSearchProvider(string cacheFilePath)
    {
        _cacheFilePath = cacheFilePath;
        _initTask = Task.Run(InitializeAsync);
    }

    internal Task WarmupAsync() => _initTask;

    private async Task InitializeAsync()
    {
        try
        {
            // 1. Fast cache load for sub-15ms readiness
            LoadCache();

            // 2. Start full background crawl with low priority to catch all new/changed files
            await CrawlAllDrivesAsync(_cts.Token).ConfigureAwait(false);

            // 3. Setup FileSystemWatchers for real-time file updates
            SetupWatchers();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MULTIDRIVE-INDEX", ex, "Initialization failed");
        }
    }

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        query = query.Trim().Trim('"').Trim();
        if (query.Length < 2 || limit <= 0)
            return Array.Empty<SpotlightSearchItem>();

        if (!SpotlightFileVisibility.ShouldSearch(query))
            return Array.Empty<SpotlightSearchItem>();

        var snapshot = _snapshot;
        if (snapshot.Length == 0)
            return Array.Empty<SpotlightSearchItem>();

        limit = Math.Clamp(limit, 1, 50);

        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Array.Empty<SpotlightSearchItem>();

        var matches = new List<SpotlightSearchItem>(Math.Min(limit * 4, 200));

        await Task.Run(() =>
        {
            int maxScanMatches = 300;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var entry = snapshot[i];
                bool allMatch = true;

                for (int t = 0; t < terms.Length; t++)
                {
                    string term = terms[t];
                    if (entry.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                        entry.FullPath.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (!allMatch) continue;

                if (IsExcluded?.Invoke(entry.FullPath) == true) continue;

                var item = new SpotlightSearchItem(
                    $"multidrive:{entry.FullPath}",
                    entry.IsDirectory ? SpotlightResultKind.Folder : SpotlightResultKind.File,
                    entry.Name,
                    Path.GetDirectoryName(entry.FullPath) ?? string.Empty,
                    entry.FullPath,
                    entry.FullPath
                );

                if (!SpotlightFileVisibility.ShouldInclude(item, query)) continue;

                double score = SpotlightRanker.Score(item, query);
                if (score > 0)
                {
                    matches.Add(item with { Score = score });
                    if (matches.Count >= maxScanMatches) break;
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        if (matches.Count == 0)
            return Array.Empty<SpotlightSearchItem>();

        return matches
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private async Task CrawlAllDrivesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<IndexedEntry>(100_000);
        var queue = new Queue<string>();

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .ToArray();
        }
        catch
        {
            drives = Array.Empty<DriveInfo>();
        }

        foreach (var drive in drives)
        {
            string rootPath = drive.RootDirectory.FullName;
            bool isSystemDrive = string.Equals(drive.Name.TrimEnd('\\'), "C:", StringComparison.OrdinalIgnoreCase);

            if (isSystemDrive)
            {
                // On C:, index C:\Users and custom user directories, skip OS folders
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(rootPath, "*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    }))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (!ExcludedCDriveRoots.Contains(dirName))
                        {
                            queue.Enqueue(dir);
                            entries.Add(new IndexedEntry(dirName, dir, true));
                        }
                    }
                }
                catch
                {
                }
            }
            else
            {
                // On all other drives (D:, E:, etc.), start directly from root
                queue.Enqueue(rootPath);
            }
        }

        int processedSinceThrottle = 0;
        int lastPublishCount = 0;

        while (queue.Count > 0 && entries.Count < 500_000)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string current = queue.Dequeue();

            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(current, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    string subName = Path.GetFileName(subDir);
                    if (string.IsNullOrEmpty(subName)) continue;

                    if (ExcludedDirs.Contains(subName) || subName.StartsWith('$') || subName.StartsWith('.'))
                        continue;

                    queue.Enqueue(subDir);
                    entries.Add(new IndexedEntry(subName, subDir, true));
                }

                foreach (string file in Directory.EnumerateFiles(current, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    string ext = Path.GetExtension(file);
                    if (ExcludedExtensions.Contains(ext)) continue;

                    string fileName = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(fileName)) continue;

                    entries.Add(new IndexedEntry(fileName, file, false));
                }
            }
            catch
            {
                // Skip inaccessible or vanished directories safely
            }

            processedSinceThrottle++;
            if (processedSinceThrottle >= 500)
            {
                processedSinceThrottle = 0;
                await Task.Yield();

                // Incrementally publish new items so user can search immediately
                if (entries.Count - lastPublishCount >= 5000)
                {
                    lastPublishCount = entries.Count;
                    lock (_snapshotLock)
                    {
                        _snapshot = entries.ToArray();
                    }
                }
            }
        }

        // Final publish and save to cache
        lock (_snapshotLock)
        {
            _snapshot = entries.ToArray();
        }

        SaveCache(_snapshot);
        RuntimeLog.Info("MULTIDRIVE-INDEX", $"Indexed {_snapshot.Length} items across all ready drives");
    }

    private void SetupWatchers()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .ToArray();

            foreach (var drive in drives)
            {
                string watchPath;
                if (string.Equals(drive.Name.TrimEnd('\\'), "C:", StringComparison.OrdinalIgnoreCase))
                {
                    string userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!Directory.Exists(userDir)) continue;
                    watchPath = userDir;
                }
                else
                {
                    watchPath = drive.RootDirectory.FullName;
                }

                try
                {
                    var watcher = new FileSystemWatcher(watchPath)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };

                    watcher.Created += OnFileSystemChanged;
                    watcher.Deleted += OnFileSystemDeleted;
                    watcher.Renamed += OnFileSystemRenamed;

                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("MULTIDRIVE-WATCHER", $"Could not watch {watchPath}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MULTIDRIVE-WATCHER", $"Watcher setup failed: {ex.Message}");
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            string name = Path.GetFileName(e.FullPath);
            if (string.IsNullOrEmpty(name) || ExcludedDirs.Contains(name)) return;
            bool isDir = Directory.Exists(e.FullPath);

            if (!isDir)
            {
                string ext = Path.GetExtension(e.FullPath);
                if (ExcludedExtensions.Contains(ext)) return;
            }

            lock (_snapshotLock)
            {
                var current = _snapshot;
                var updated = new IndexedEntry[current.Length + 1];
                current.CopyTo(updated, 0);
                updated[current.Length] = new IndexedEntry(name, e.FullPath, isDir);
                _snapshot = updated;
            }
        }
        catch
        {
        }
    }

    private void OnFileSystemDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            lock (_snapshotLock)
            {
                var current = _snapshot;
                _snapshot = current.Where(entry => !string.Equals(entry.FullPath, e.FullPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
        }
        catch
        {
        }
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            string newName = Path.GetFileName(e.FullPath);
            if (string.IsNullOrEmpty(newName)) return;
            bool isDir = Directory.Exists(e.FullPath);

            lock (_snapshotLock)
            {
                var current = _snapshot;
                var list = new List<IndexedEntry>(current.Length);
                for (int i = 0; i < current.Length; i++)
                {
                    if (string.Equals(current[i].FullPath, e.OldFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new IndexedEntry(newName, e.FullPath, isDir));
                    }
                    else
                    {
                        list.Add(current[i]);
                    }
                }
                _snapshot = list.ToArray();
            }
        }
        catch
        {
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return;

            using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var reader = new BinaryReader(stream);

            string header = reader.ReadString();
            if (header != CacheFileHeader) return;

            int count = reader.ReadInt32();
            if (count <= 0 || count > 1_000_000) return;

            var entries = new IndexedEntry[count];
            for (int i = 0; i < count; i++)
            {
                bool isDir = reader.ReadBoolean();
                string fullPath = reader.ReadString();
                string name = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(name)) name = fullPath;
                entries[i] = new IndexedEntry(name, fullPath, isDir);
            }

            lock (_snapshotLock)
            {
                _snapshot = entries;
            }

            RuntimeLog.Info("MULTIDRIVE-INDEX", $"Loaded {_snapshot.Length} items from cache file");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MULTIDRIVE-INDEX", $"Cache load failed: {ex.Message}");
        }
    }

    private void SaveCache(IndexedEntry[] entries)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmpPath = _cacheFilePath + ".tmp";
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(CacheFileHeader);
                writer.Write(entries.Length);
                for (int i = 0; i < entries.Length; i++)
                {
                    writer.Write(entries[i].IsDirectory);
                    writer.Write(entries[i].FullPath);
                }
            }

            File.Move(tmpPath, _cacheFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MULTIDRIVE-INDEX", $"Cache save failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        for (int i = 0; i < _watchers.Count; i++)
        {
            try
            {
                _watchers[i].EnableRaisingEvents = false;
                _watchers[i].Dispose();
            }
            catch
            {
            }
        }
        _watchers.Clear();
    }
}
