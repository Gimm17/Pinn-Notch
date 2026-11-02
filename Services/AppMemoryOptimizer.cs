using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VNotch.Services;

public static partial class AppMemoryOptimizer
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32EmptyWorkingSet(IntPtr hProcess);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_SET_QUOTA = 0x0100;

    private static long _lastOptimizationTicks = 0;
    private static readonly DispatcherTimer _autoTrimTimer;

    static AppMemoryOptimizer()
    {
        _autoTrimTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _autoTrimTimer.Tick += (_, _) => OptimizeMemory(aggressive: false);
        _autoTrimTimer.Start();
    }

    /// <summary>
    /// Returns the process IDs of all processes descending from Pinn-Notch or its WebView2 instance.
    /// Excludes any unrelated system processes.
    /// </summary>
    public static HashSet<int> GetDescendantProcessIds()
    {
        var result = new HashSet<int>();
        int mainPid = Environment.ProcessId;

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
        {
            return result;
        }

        try
        {
            var entry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };

            var parentMap = new Dictionary<int, (int ParentPid, string Name)>();
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    parentMap[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
                }
                while (Process32Next(snapshot, ref entry));
            }

            var toExplore = new Queue<int>();
            toExplore.Enqueue(mainPid);

            uint? ytmBrowserPid = YouTubeMusicPlayerService.Instance.BrowserProcessId;
            if (ytmBrowserPid.HasValue && ytmBrowserPid.Value > 0)
            {
                toExplore.Enqueue((int)ytmBrowserPid.Value);
            }

            while (toExplore.Count > 0)
            {
                int current = toExplore.Dequeue();
                foreach (var kvp in parentMap)
                {
                    if (kvp.Value.ParentPid == current && !result.Contains(kvp.Key))
                    {
                        result.Add(kvp.Key);
                        toExplore.Enqueue(kvp.Key);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MEM-OPTIMIZE", $"GetDescendantProcessIds error: {ex.Message}");
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    public static void OptimizeMemory(bool aggressive = false)
    {
        long now = Environment.TickCount64;
        if (!aggressive && now - _lastOptimizationTicks < 15_000)
        {
            return;
        }
        _lastOptimizationTicks = now;

        try
        {
            // 1. Managed GC collect
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: aggressive);

            // 2. Trim Pinn-Notch main process working set
            _ = K32EmptyWorkingSet(GetCurrentProcess());

            // 3. Trim our own child WebView2 processes
            var childPids = GetDescendantProcessIds();
            foreach (int pid in childPids)
            {
                try
                {
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        _ = K32EmptyWorkingSet(h);
                        _ = CloseHandle(h);
                    }
                }
                catch
                {
                    // Ignore transient process access errors
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MEM-OPTIMIZE", $"Trim error: {ex.Message}");
        }
    }
}
