using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed class MultiDriveSearchProvider : ISpotlightProvider, IDisposable
{
    internal const int MaxIndexedEntries = 500_000;
    private const int DirSlabSize = 4_096;
    private const int MaxDirSlabs = 128; // 524,288 unique directories
    private const int NameSlabSize = 32_768;
    private const int MaxNameSlabs = 2_048; // 64 MB UTF-8 name pool in 32 KB slabs
    private const int EntrySlabSize = 4_096;
    private const int MaxEntrySlabs = 256; // Up to 1,048,576 append slots in 48 KB slabs

    private const byte FlagIsDirectory = 0x01;
    private const byte FlagDeleted = 0x02;

    private const string LegacyCacheHeader = "PINNMDI1";
    private const string CompactCacheHeader = "PINNMDI2";
    private static readonly TimeSpan CacheFreshnessWindow = TimeSpan.FromHours(12);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct EntryRecord
    {
        public int DirId;
        public ushort NameSlab;
        public ushort NameOffset;
        public ushort NameByteLen;
        public byte Flags;
    }

    internal sealed class CompactArena
    {
        public readonly object SyncRoot = new();
        private readonly Dictionary<string, int> _dirMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly string[][] _dirSlabs = new string[MaxDirSlabs][];
        private readonly byte[][] _nameSlabs = new byte[MaxNameSlabs][];
        private readonly EntryRecord[][] _entrySlabs = new EntryRecord[MaxEntrySlabs][];

        private int _dirCount;
        private int _currentNameSlab;
        private int _currentNameOffset;
        internal int _entryCount;
        internal int _activeCount;

        public int EntryCount => Volatile.Read(ref _entryCount);
        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int DirectoryCount
        {
            get { lock (SyncRoot) return _dirCount; }
        }

        public string GetDir(int dirId)
        {
            int slab = dirId >> 12;
            int idx = dirId & (DirSlabSize - 1);
            return _dirSlabs[slab][idx];
        }

        public ReadOnlySpan<byte> GetNameBytes(in EntryRecord rec) =>
            _nameSlabs[rec.NameSlab].AsSpan(rec.NameOffset, rec.NameByteLen);

        public string GetNameString(in EntryRecord rec) =>
            Encoding.UTF8.GetString(GetNameBytes(in rec));

        public ref readonly EntryRecord GetEntry(int index)
        {
            int slab = index >> 12;
            int idx = index & (EntrySlabSize - 1);
            return ref _entrySlabs[slab][idx];
        }

        public int EnsureDirectoryLocked(string dirPath)
        {
            if (_dirMap.TryGetValue(dirPath, out int existing))
                return existing;

            int id = _dirCount;
            int slab = id >> 12;
            if (slab >= MaxDirSlabs)
                return -1;

            int idx = id & (DirSlabSize - 1);
            _dirSlabs[slab] ??= new string[DirSlabSize];
            _dirSlabs[slab][idx] = dirPath;
            _dirMap[dirPath] = id;
            _dirCount = id + 1;
            return id;
        }

        public bool AppendCharNameLocked(string dirPath, string name, bool isDirectory)
        {
            int dirId = EnsureDirectoryLocked(dirPath);
            if (dirId < 0) return false;
            int byteCount = Encoding.UTF8.GetByteCount(name);
            if (byteCount <= 0 || byteCount > NameSlabSize)
                return false;

            return AppendUtf8InternalLocked(dirId, name, ReadOnlySpan<byte>.Empty, byteCount, isDirectory);
        }

        public bool AppendUtf8NameLocked(int dirId, ReadOnlySpan<byte> utf8Name, bool isDirectory)
        {
            if (dirId < 0 || dirId >= _dirCount || utf8Name.IsEmpty || utf8Name.Length > NameSlabSize)
                return false;

            return AppendUtf8InternalLocked(dirId, null, utf8Name, utf8Name.Length, isDirectory);
        }

        private bool AppendUtf8InternalLocked(
            int dirId,
            string? charName,
            ReadOnlySpan<byte> utf8Name,
            int byteCount,
            bool isDirectory)
        {
            if (_activeCount >= MaxIndexedEntries)
                return false;

            int entryIdx = _entryCount;
            int eSlab = entryIdx >> 12;
            if (eSlab >= MaxEntrySlabs)
                return false;

            int nSlab = _currentNameSlab;
            int nOffset = _currentNameOffset;
            if (nOffset + byteCount > NameSlabSize)
            {
                nSlab++;
                nOffset = 0;
            }
            if (nSlab >= MaxNameSlabs)
                return false;

            byte[] nameBuffer = _nameSlabs[nSlab] ??= new byte[NameSlabSize];
            if (charName != null)
                Encoding.UTF8.GetBytes(charName, nameBuffer.AsSpan(nOffset, byteCount));
            else
                utf8Name.CopyTo(nameBuffer.AsSpan(nOffset, byteCount));

            _currentNameSlab = nSlab;
            _currentNameOffset = nOffset + byteCount;

            int eIdx = entryIdx & (EntrySlabSize - 1);
            EntryRecord[] entrySlab = _entrySlabs[eSlab] ??= new EntryRecord[EntrySlabSize];
            entrySlab[eIdx] = new EntryRecord
            {
                DirId = dirId,
                NameSlab = (ushort)nSlab,
                NameOffset = (ushort)nOffset,
                NameByteLen = (ushort)byteCount,
                Flags = isDirectory ? FlagIsDirectory : (byte)0
            };

            Volatile.Write(ref _activeCount, _activeCount + 1);
            Volatile.Write(ref _entryCount, entryIdx + 1);
            return true;
        }

        public bool ContainsExactLocked(string dirPath, string name)
        {
            if (!_dirMap.TryGetValue(dirPath, out int dirId))
                return false;

            Span<byte> utf8 = stackalloc byte[512];
            int need = Encoding.UTF8.GetByteCount(name);
            byte[]? rented = need > utf8.Length ? new byte[need] : null;
            Span<byte> target = rented ?? utf8;
            Encoding.UTF8.GetBytes(name, target);
            ReadOnlySpan<byte> targetSlice = target.Slice(0, need);

            int count = _entryCount;
            for (int i = 0; i < count; i++)
            {
                ref readonly EntryRecord rec = ref _entrySlabs[i >> 12][i & (EntrySlabSize - 1)];
                if (rec.DirId != dirId || (rec.Flags & FlagDeleted) != 0)
                    continue;
                if (rec.NameByteLen == need && GetNameBytes(in rec).SequenceEqual(targetSlice))
                    return true;
            }
            return false;
        }

        public bool MarkPathDeletedLocked(string fullPath, string dirPath, string name)
        {
            bool changed = false;

            if (_dirMap.TryGetValue(dirPath, out int dirId))
            {
                Span<byte> utf8 = stackalloc byte[512];
                int need = Encoding.UTF8.GetByteCount(name);
                byte[]? rented = need > utf8.Length ? new byte[need] : null;
                Span<byte> target = rented ?? utf8;
                Encoding.UTF8.GetBytes(name, target);
                ReadOnlySpan<byte> targetSlice = target.Slice(0, need);

                int count = _entryCount;
                for (int i = 0; i < count; i++)
                {
                    ref EntryRecord rec = ref _entrySlabs[i >> 12][i & (EntrySlabSize - 1)];
                    if (rec.DirId != dirId || (rec.Flags & FlagDeleted) != 0)
                        continue;

                    if (Utf8EqualsOrdinalIgnoreCase(GetNameBytes(in rec), targetSlice))
                    {
                        rec.Flags |= FlagDeleted;
                        if (_activeCount > 0)
                            Volatile.Write(ref _activeCount, _activeCount - 1);
                        changed = true;
                    }
                }
            }

            // If a directory itself was deleted/renamed, tombstones all descendants under that directory prefix.
            string dirPrefix = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (_dirMap.ContainsKey(dirPrefix) || HasSubdirStartingWithLocked(dirPrefix))
            {
                HashSet<int>? matchingDirIds = null;
                for (int d = 0; d < _dirCount; d++)
                {
                    string candidate = _dirSlabs[d >> 12][d & (DirSlabSize - 1)];
                    if (IsSameOrChildPath(candidate, dirPrefix))
                    {
                        matchingDirIds ??= new HashSet<int>();
                        matchingDirIds.Add(d);
                    }
                }

                if (matchingDirIds != null)
                {
                    int count = _entryCount;
                    for (int i = 0; i < count; i++)
                    {
                        ref EntryRecord rec = ref _entrySlabs[i >> 12][i & (EntrySlabSize - 1)];
                        if ((rec.Flags & FlagDeleted) == 0 && matchingDirIds.Contains(rec.DirId))
                        {
                            rec.Flags |= FlagDeleted;
                            if (_activeCount > 0)
                                Volatile.Write(ref _activeCount, _activeCount - 1);
                            changed = true;
                        }
                    }
                }
            }

            return changed;
        }

        private bool HasSubdirStartingWithLocked(string dirPrefix)
        {
            for (int d = 0; d < _dirCount; d++)
            {
                string candidate = _dirSlabs[d >> 12][d & (DirSlabSize - 1)];
                if (IsSameOrChildPath(candidate, dirPrefix))
                    return true;
            }
            return false;
        }

        private static bool IsSameOrChildPath(string candidate, string prefix)
        {
            if (candidate.Length == prefix.Length)
                return string.Equals(candidate, prefix, StringComparison.OrdinalIgnoreCase);
            if (candidate.Length > prefix.Length &&
                candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                char sep = candidate[prefix.Length];
                return sep == Path.DirectorySeparatorChar || sep == Path.AltDirectorySeparatorChar;
            }
            return false;
        }

        private static bool Utf8EqualsOrdinalIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            bool hasNonAscii = false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] >= 0x80) { hasNonAscii = true; break; }
            }
            if (!hasNonAscii)
            {
                for (int i = 0; i < b.Length; i++)
                {
                    if (b[i] >= 0x80) { hasNonAscii = true; break; }
                }
            }

            if (!hasNonAscii)
            {
                if (a.Length != b.Length)
                    return false;
                for (int i = 0; i < a.Length; i++)
                {
                    byte ca = a[i];
                    byte cb = b[i];
                    if ((uint)(ca - 'A') <= ('Z' - 'A')) ca = (byte)(ca + 32);
                    if ((uint)(cb - 'A') <= ('Z' - 'A')) cb = (byte)(cb + 32);
                    if (ca != cb)
                        return false;
                }
                return true;
            }

            return string.Equals(Encoding.UTF8.GetString(a), Encoding.UTF8.GetString(b), StringComparison.OrdinalIgnoreCase);
        }

        public void WriteToCache(BinaryWriter writer, string drivesSignature)
        {
            lock (SyncRoot)
            {
                writer.Write(CompactCacheHeader);
                writer.Write(DateTime.UtcNow.Ticks);
                writer.Write(drivesSignature);
                writer.Write(_dirCount);
                for (int d = 0; d < _dirCount; d++)
                    writer.Write(GetDir(d));

                writer.Write(_activeCount);
                int count = _entryCount;
                for (int i = 0; i < count; i++)
                {
                    ref readonly EntryRecord rec = ref GetEntry(i);
                    if ((rec.Flags & FlagDeleted) != 0) continue;
                    writer.Write(rec.Flags);
                    writer.Write(rec.DirId);
                    writer.Write(rec.NameByteLen);
                    writer.Write(_nameSlabs[rec.NameSlab], rec.NameOffset, rec.NameByteLen);
                }
            }
        }
    }

    private readonly string _cacheFilePath;
    private readonly IReadOnlyList<string>? _customRoots;
    private readonly bool _enableWatchers;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _initTask;
    private readonly List<FileSystemWatcher> _watchers = new();

    private volatile CompactArena _arena = new();
    private volatile bool _cacheLoadedFresh;
    private bool _disposed;

    public bool IsAvailable => true;
    public bool IsInstant => true;

    internal int IndexedCount => _arena.ActiveCount;
    internal bool WasCacheLoadedFresh => _cacheLoadedFresh;
    internal Func<string, bool>? IsExcluded { get; set; }

    public MultiDriveSearchProvider()
        : this(Path.Combine(AppPaths.RoamingAppDataRoot, "multidrive_index.dat"))
    {
    }

    internal MultiDriveSearchProvider(
        string cacheFilePath,
        IReadOnlyList<string>? customRoots = null,
        bool enableWatchers = true)
    {
        _cacheFilePath = cacheFilePath;
        _customRoots = customRoots;
        _enableWatchers = enableWatchers;
        _initTask = Task.Run(InitializeAsync);
    }

    internal Task WarmupAsync() => _initTask;

    internal void SeedEntriesForTest(IEnumerable<(string FullPath, bool IsDirectory)> entries)
    {
        var arena = new CompactArena();
        lock (arena.SyncRoot)
        {
            foreach (var (fullPath, isDirectory) in entries)
            {
                string? dir = Path.GetDirectoryName(fullPath);
                string name = Path.GetFileName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(name))
                    arena.AppendCharNameLocked(dir, name, isDirectory);
            }
        }
        _arena = arena;
    }

    internal static bool ShouldExcludePath(ReadOnlySpan<char> fullPath, bool isDirectory)
    {
        if (fullPath.IsEmpty) return true;

        if (!isDirectory)
        {
            ReadOnlySpan<char> ext = Path.GetExtension(fullPath);
            if (!ext.IsEmpty && IsExcludedExtension(ext))
                return true;
        }

        int pos = 0;
        bool isCDrive = false;

        if (fullPath.Length >= 2 && fullPath[1] == ':')
        {
            isCDrive = fullPath[0] == 'C' || fullPath[0] == 'c';
            pos = 2;
        }

        while (pos < fullPath.Length && (fullPath[pos] == '\\' || fullPath[pos] == '/'))
            pos++;

        bool firstSegmentAfterRoot = true;
        while (pos < fullPath.Length)
        {
            int nextSepRel = fullPath.Slice(pos).IndexOfAny('\\', '/');
            int segLen = nextSepRel < 0 ? fullPath.Length - pos : nextSepRel;
            bool isLastSegment = nextSepRel < 0 || pos + nextSepRel >= fullPath.Length - 1;

            if (segLen > 0)
            {
                ReadOnlySpan<char> seg = fullPath.Slice(pos, segLen);
                if (firstSegmentAfterRoot)
                {
                    firstSegmentAfterRoot = false;
                    if (isCDrive && IsExcludedCDriveRoot(seg))
                        return true;
                }

                bool checkAsDir = isDirectory || !isLastSegment;
                if (checkAsDir)
                {
                    if (seg[0] == '$' || seg[0] == '.' || IsExcludedDirSegment(seg))
                        return true;
                }
                else
                {
                    if (seg[0] == '$')
                        return true;
                }
            }

            if (nextSepRel < 0) break;
            pos += segLen + 1;
            while (pos < fullPath.Length && (fullPath[pos] == '\\' || fullPath[pos] == '/'))
                pos++;
        }

        return false;
    }

    private static bool IsExcludedExtension(ReadOnlySpan<char> ext) => ext.Length switch
    {
        4 => ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".sys", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".drv", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".ocx", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".obj", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".mui", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".cat", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
          || ext.Equals(".etl", StringComparison.OrdinalIgnoreCase),
        5 => ext.Equals(".temp", StringComparison.OrdinalIgnoreCase),
        6 => ext.Equals(".cache", StringComparison.OrdinalIgnoreCase),
        9 => ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsExcludedCDriveRoot(ReadOnlySpan<char> seg) => seg.Length switch
    {
        7 => seg.Equals("Windows", StringComparison.OrdinalIgnoreCase),
        8 => seg.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("MSOCache", StringComparison.OrdinalIgnoreCase),
        10 => seg.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase),
        11 => seg.Equals("ProgramData", StringComparison.OrdinalIgnoreCase),
        12 => seg.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase),
        13 => seg.Equals("Program Files", StringComparison.OrdinalIgnoreCase),
        19 => seg.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase),
        25 => seg.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsExcludedDirSegment(ReadOnlySpan<char> seg) => seg.Length switch
    {
        3 => seg.Equals("obj", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("bin", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("WSL", StringComparison.OrdinalIgnoreCase),
        4 => seg.Equals(".git", StringComparison.OrdinalIgnoreCase)
          || seg.Equals(".svn", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("Temp", StringComparison.OrdinalIgnoreCase),
        5 => seg.Equals(".idea", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("Cache", StringComparison.OrdinalIgnoreCase),
        6 => seg.Equals("Caches", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("vendor", StringComparison.OrdinalIgnoreCase),
        7 => seg.Equals("AppData", StringComparison.OrdinalIgnoreCase)
          || seg.Equals(".vscode", StringComparison.OrdinalIgnoreCase)
          || seg.Equals(".nuget", StringComparison.OrdinalIgnoreCase),
        8 => seg.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("WpSystem", StringComparison.OrdinalIgnoreCase),
        9 => seg.Equals("3uToolsV3", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("3uTools9", StringComparison.OrdinalIgnoreCase)
          || seg.Equals("npm-cache", StringComparison.OrdinalIgnoreCase),
        10 => seg.Equals("npm-global", StringComparison.OrdinalIgnoreCase)
           || seg.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase),
        11 => seg.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
           || seg.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)
           || seg.Equals(".pnpm-store", StringComparison.OrdinalIgnoreCase)
           || seg.Equals("android-sdk", StringComparison.OrdinalIgnoreCase),
        12 => seg.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
           || seg.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase),
        15 => seg.Equals("WUDownloadCache", StringComparison.OrdinalIgnoreCase),
        25 => seg.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private async Task InitializeAsync()
    {
        try
        {
            if (LoadCache(out bool isFresh))
            {
                _cacheLoadedFresh = isFresh;
                if (isFresh)
                {
                    RuntimeLog.Info("MULTIDRIVE-INDEX", $"Loaded {_arena.ActiveCount} items from compact cache (skipped disk crawl)");
                    SetupWatchers();
                    return;
                }
            }

            await CrawlAllDrivesAsync(_cts.Token).ConfigureAwait(false);
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

        var arena = _arena;
        int entryCount = arena.EntryCount;
        if (entryCount == 0)
            return Array.Empty<SpotlightSearchItem>();

        limit = Math.Clamp(limit, 1, 50);

        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0 || terms.Length > 31) return Array.Empty<SpotlightSearchItem>();

        int termCount = terms.Length;
        byte[][] asciiTerms = new byte[termCount][];
        bool[] termIsAscii = new bool[termCount];
        for (int t = 0; t < termCount; t++)
        {
            string term = terms[t];
            if (IsAscii(term))
            {
                termIsAscii[t] = true;
                byte[] lower = new byte[term.Length];
                for (int c = 0; c < term.Length; c++)
                {
                    char ch = term[c];
                    lower[c] = (byte)(ch is >= 'A' and <= 'Z' ? ch + 32 : ch);
                }
                asciiTerms[t] = lower;
            }
        }

        var matches = new List<SpotlightSearchItem>(Math.Min(limit * 4, 200));

        await Task.Run(() =>
        {
            const int maxScanMatches = 300;
            int lastDirId = -1;
            string currentDirPath = string.Empty;
            uint dirMatchMask = 0;
            uint allTermsMask = (1u << termCount) - 1u;
            char[] nameCharBuf = new char[512];

            for (int i = 0; i < entryCount; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                ref readonly EntryRecord rec = ref arena.GetEntry(i);
                if ((rec.Flags & FlagDeleted) != 0)
                    continue;

                if (rec.DirId != lastDirId)
                {
                    lastDirId = rec.DirId;
                    currentDirPath = arena.GetDir(lastDirId);
                    dirMatchMask = 0;
                    for (int t = 0; t < termCount; t++)
                    {
                        if (currentDirPath.IndexOf(terms[t], StringComparison.OrdinalIgnoreCase) >= 0)
                            dirMatchMask |= 1u << t;
                    }
                }

                if (dirMatchMask != allTermsMask)
                {
                    ReadOnlySpan<byte> nameUtf8 = arena.GetNameBytes(in rec);
                    bool nameIsAscii = IsAllAscii(nameUtf8);
                    bool allMatched = true;
                    string? decodedName = null;

                    for (int t = 0; t < termCount; t++)
                    {
                        if ((dirMatchMask & (1u << t)) != 0)
                            continue;

                        if (nameIsAscii && termIsAscii[t])
                        {
                            if (!ContainsAsciiCaseInsensitive(nameUtf8, asciiTerms[t]))
                            {
                                allMatched = false;
                                break;
                            }
                        }
                        else
                        {
                            if (decodedName == null)
                            {
                                int charsNeeded = Encoding.UTF8.GetCharCount(nameUtf8);
                                if (charsNeeded <= nameCharBuf.Length)
                                {
                                    int written = Encoding.UTF8.GetChars(nameUtf8, nameCharBuf);
                                    if (((ReadOnlySpan<char>)nameCharBuf.AsSpan(0, written)).IndexOf(terms[t].AsSpan(), StringComparison.OrdinalIgnoreCase) < 0)
                                    {
                                        allMatched = false;
                                        break;
                                    }
                                    continue;
                                }
                                decodedName = Encoding.UTF8.GetString(nameUtf8);
                            }

                            if (decodedName.IndexOf(terms[t], StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                allMatched = false;
                                break;
                            }
                        }
                    }

                    if (!allMatched)
                        continue;
                }

                string fileName = arena.GetNameString(in rec);
                string fullPath = Path.Combine(currentDirPath, fileName);

                if (IsExcluded?.Invoke(fullPath) == true) continue;

                bool isDir = (rec.Flags & FlagIsDirectory) != 0;
                var item = new SpotlightSearchItem(
                    $"multidrive:{fullPath}",
                    isDir ? SpotlightResultKind.Folder : SpotlightResultKind.File,
                    fileName,
                    currentDirPath,
                    fullPath,
                    fullPath);

                if (!SpotlightFileVisibility.ShouldInclude(item, query)) continue;

                double score = SpotlightRanker.Score(item, query);
                if (score <= 0 && termCount > 1)
                {
                    double sum = 0;
                    for (int t = 0; t < termCount; t++)
                        sum += SpotlightRanker.Score(item, terms[t]);
                    score = Math.Max(120.0, (sum / termCount) * 0.65);
                }

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
        var crawlArena = new CompactArena();
        _arena = crawlArena;
        var queue = new Queue<string>();

        if (_customRoots != null)
        {
            foreach (string rawRoot in _customRoots)
            {
                if (string.IsNullOrWhiteSpace(rawRoot)) continue;
                try
                {
                    string full = Path.GetFullPath(rawRoot);
                    if (Directory.Exists(full))
                        queue.Enqueue(full);
                }
                catch { }
            }
        }
        else
        {
            DriveInfo[] drives = GetReadyDrives();
            foreach (var drive in drives)
            {
                string rootPath = drive.RootDirectory.FullName;
                bool isSystemDrive = string.Equals(drive.Name.TrimEnd('\\'), "C:", StringComparison.OrdinalIgnoreCase);

                if (isSystemDrive)
                {
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
                            if (!string.IsNullOrEmpty(dirName) && !IsExcludedCDriveRoot(dirName))
                            {
                                queue.Enqueue(dir);
                                lock (crawlArena.SyncRoot)
                                    crawlArena.AppendCharNameLocked(rootPath, dirName, isDirectory: true);
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    queue.Enqueue(rootPath);
                }
            }
        }

        int processedSinceThrottle = 0;

        while (queue.Count > 0 && crawlArena.ActiveCount < MaxIndexedEntries)
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

                    if (subName[0] == '$' || subName[0] == '.' || IsExcludedDirSegment(subName))
                        continue;

                    queue.Enqueue(subDir);
                    lock (crawlArena.SyncRoot)
                    {
                        if (!crawlArena.AppendCharNameLocked(current, subName, isDirectory: true))
                            break;
                    }
                }

                if (crawlArena.ActiveCount >= MaxIndexedEntries)
                    break;

                foreach (string file in Directory.EnumerateFiles(current, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    if (HasExcludedExtension(file)) continue;

                    string fileName = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(fileName) || fileName[0] == '$') continue;

                    lock (crawlArena.SyncRoot)
                    {
                        if (!crawlArena.AppendCharNameLocked(current, fileName, isDirectory: false))
                            break;
                    }
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
            }
        }

        SaveCache(crawlArena);
        RuntimeLog.Info("MULTIDRIVE-INDEX", $"Indexed {crawlArena.ActiveCount} items across all ready drives");
    }

    private static bool HasExcludedExtension(string filePath)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(filePath.AsSpan());
        return !ext.IsEmpty && IsExcludedExtension(ext);
    }

    private void SetupWatchers()
    {
        if (!_enableWatchers) return;

        try
        {
            IEnumerable<string> watchRoots;
            if (_customRoots != null)
            {
                watchRoots = _customRoots.Where(Directory.Exists);
            }
            else
            {
                var roots = new List<string>();
                foreach (var drive in GetReadyDrives())
                {
                    if (string.Equals(drive.Name.TrimEnd('\\'), "C:", StringComparison.OrdinalIgnoreCase))
                    {
                        string userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        if (Directory.Exists(userDir))
                            roots.Add(userDir);
                    }
                    else
                    {
                        roots.Add(drive.RootDirectory.FullName);
                    }
                }
                watchRoots = roots;
            }

            foreach (string watchPath in watchRoots)
            {
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
            bool isDir = Directory.Exists(e.FullPath);
            if (ShouldExcludePath(e.FullPath, isDir)) return;

            string? dirPath = Path.GetDirectoryName(e.FullPath);
            string name = Path.GetFileName(e.FullPath);
            if (string.IsNullOrEmpty(dirPath) || string.IsNullOrEmpty(name)) return;

            var arena = _arena;
            lock (arena.SyncRoot)
            {
                if (!arena.ContainsExactLocked(dirPath, name))
                    arena.AppendCharNameLocked(dirPath, name, isDir);
            }
        }
        catch { }
    }

    private void OnFileSystemDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            // When deleted, disk state is gone; skip only if excluded both as file and as directory segment.
            if (ShouldExcludePath(e.FullPath, isDirectory: true) && ShouldExcludePath(e.FullPath, isDirectory: false))
                return;

            string? dirPath = Path.GetDirectoryName(e.FullPath);
            string name = Path.GetFileName(e.FullPath);
            if (string.IsNullOrEmpty(dirPath) || string.IsNullOrEmpty(name)) return;

            var arena = _arena;
            lock (arena.SyncRoot)
            {
                arena.MarkPathDeletedLocked(e.FullPath, dirPath, name);
            }
        }
        catch { }
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            bool oldExcluded = ShouldExcludePath(e.OldFullPath, isDirectory: true) &&
                               ShouldExcludePath(e.OldFullPath, isDirectory: false);
            bool isDir = Directory.Exists(e.FullPath);
            bool newExcluded = ShouldExcludePath(e.FullPath, isDir);

            if (oldExcluded && newExcluded)
                return;

            var arena = _arena;
            lock (arena.SyncRoot)
            {
                if (!oldExcluded)
                {
                    string? oldDir = Path.GetDirectoryName(e.OldFullPath);
                    string oldName = Path.GetFileName(e.OldFullPath);
                    if (!string.IsNullOrEmpty(oldDir) && !string.IsNullOrEmpty(oldName))
                        arena.MarkPathDeletedLocked(e.OldFullPath, oldDir, oldName);
                }

                if (!newExcluded)
                {
                    string? newDir = Path.GetDirectoryName(e.FullPath);
                    string newName = Path.GetFileName(e.FullPath);
                    if (!string.IsNullOrEmpty(newDir) && !string.IsNullOrEmpty(newName) &&
                        !arena.ContainsExactLocked(newDir, newName))
                    {
                        arena.AppendCharNameLocked(newDir, newName, isDir);
                    }
                }
            }
        }
        catch { }
    }

    private bool LoadCache(out bool isFresh)
    {
        isFresh = false;
        try
        {
            if (!File.Exists(_cacheFilePath)) return false;

            bool migrated = false;
            int skippedExcluded = 0;

            using (var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            using (var reader = new BinaryReader(stream))
            {
                string header = reader.ReadString();
                if (header == CompactCacheHeader)
                    return LoadCompactCache(reader, out isFresh);

                if (header == LegacyCacheHeader)
                    migrated = MigrateLegacyCache(reader, out isFresh, out skippedExcluded);
                else
                    return false;
            }

            if (migrated)
            {
                SaveCache(_arena);
                RuntimeLog.Info("MULTIDRIVE-INDEX", $"Migrated legacy PINNMDI1 cache -> PINNMDI2 ({_arena.ActiveCount} kept, {skippedExcluded} excluded purged)");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MULTIDRIVE-INDEX", $"Cache load failed: {ex.Message}");
            return false;
        }
    }

    private bool LoadCompactCache(BinaryReader reader, out bool isFresh)
    {
        isFresh = false;
        long savedUtcTicks = reader.ReadInt64();
        string drivesSignature = reader.ReadString();
        int dirCount = reader.ReadInt32();
        if (dirCount < 0 || dirCount > MaxDirSlabs * DirSlabSize)
            return false;

        var arena = new CompactArena();
        lock (arena.SyncRoot)
        {
            for (int d = 0; d < dirCount; d++)
            {
                string dirPath = reader.ReadString();
                arena.EnsureDirectoryLocked(dirPath);
            }

            int entryCount = reader.ReadInt32();
            if (entryCount <= 0 || entryCount > MaxIndexedEntries)
                return false;

            byte[] scratch = new byte[4096];
            for (int i = 0; i < entryCount; i++)
            {
                byte flags = reader.ReadByte();
                int dirId = reader.ReadInt32();
                ushort byteLen = reader.ReadUInt16();
                if (byteLen > scratch.Length)
                    scratch = new byte[byteLen];

                int read = 0;
                while (read < byteLen)
                {
                    int n = reader.Read(scratch, read, byteLen - read);
                    if (n <= 0) return false;
                    read += n;
                }

                arena.AppendUtf8NameLocked(dirId, scratch.AsSpan(0, byteLen), (flags & FlagIsDirectory) != 0);
            }
        }

        _arena = arena;
        string expectedSig = _customRoots != null ? "CUSTOM" : GetReadyDrivesSignature();
        isFresh = arena.ActiveCount > 0 &&
                  string.Equals(drivesSignature, expectedSig, StringComparison.OrdinalIgnoreCase) &&
                  (DateTime.UtcNow - new DateTime(savedUtcTicks, DateTimeKind.Utc)) < CacheFreshnessWindow;
        return arena.ActiveCount > 0;
    }

    private bool MigrateLegacyCache(BinaryReader reader, out bool isFresh, out int skippedExcluded)
    {
        isFresh = false;
        skippedExcluded = 0;
        int count = reader.ReadInt32();
        if (count <= 0 || count > 1_000_000) return false;

        var arena = new CompactArena();

        lock (arena.SyncRoot)
        {
            for (int i = 0; i < count; i++)
            {
                bool isDir = reader.ReadBoolean();
                string fullPath = reader.ReadString();

                if (ShouldExcludePath(fullPath, isDir))
                {
                    skippedExcluded++;
                    continue;
                }

                string? dirPath = Path.GetDirectoryName(fullPath);
                string name = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(dirPath) || string.IsNullOrEmpty(name))
                {
                    if (isDir)
                    {
                        dirPath = Path.GetPathRoot(fullPath) ?? fullPath;
                        name = fullPath.TrimEnd('\\');
                    }
                    else
                    {
                        continue;
                    }
                }

                arena.AppendCharNameLocked(dirPath, name, isDir);
            }
        }

        if (arena.ActiveCount == 0)
            return false;

        _arena = arena;
        DateTime legacyWriteUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
        isFresh = (DateTime.UtcNow - legacyWriteUtc) < CacheFreshnessWindow;
        return true;
    }

    private void SaveCache(CompactArena arena)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string drivesSig = _customRoots != null ? "CUSTOM" : GetReadyDrivesSignature();
            string tmpPath = _cacheFilePath + ".tmp";
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream))
            {
                arena.WriteToCache(writer, drivesSig);
            }

            File.Move(tmpPath, _cacheFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MULTIDRIVE-INDEX", $"Cache save failed: {ex.Message}");
        }
    }

    private static DriveInfo[] GetReadyDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .ToArray();
        }
        catch
        {
            return Array.Empty<DriveInfo>();
        }
    }

    private static string GetReadyDrivesSignature()
    {
        var drives = GetReadyDrives();
        if (drives.Length == 0) return string.Empty;
        return string.Join('|', drives
            .Select(d => d.RootDirectory.FullName.ToUpperInvariant())
            .OrderBy(r => r, StringComparer.Ordinal));
    }

    private static bool IsAscii(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] >= 0x80) return false;
        }
        return true;
    }

    private static bool IsAllAscii(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] >= 0x80) return false;
        }
        return true;
    }

    private static bool ContainsAsciiCaseInsensitive(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerNeedle)
    {
        if (lowerNeedle.Length == 0) return true;
        if (lowerNeedle.Length > haystack.Length) return false;

        int maxStart = haystack.Length - lowerNeedle.Length;
        byte firstLower = lowerNeedle[0];
        for (int i = 0; i <= maxStart; i++)
        {
            byte hb = haystack[i];
            if (hb is >= (byte)'A' and <= (byte)'Z') hb += 32;
            if (hb != firstLower) continue;

            bool match = true;
            for (int j = 1; j < lowerNeedle.Length; j++)
            {
                byte hc = haystack[i + j];
                if (hc is >= (byte)'A' and <= (byte)'Z') hc += 32;
                if (hc != lowerNeedle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
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
            catch { }
        }
        _watchers.Clear();
    }
}
