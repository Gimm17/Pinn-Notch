using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Service that monitors the CPU, GPU, and RAM resource consumption of the Pinn-Notch application
/// (including child WebView2 processes for YouTube Music) and overall system context.
/// </summary>
public sealed class AppResourceMonitorService
{
    private static readonly Lazy<AppResourceMonitorService> _lazy =
        new(() => new AppResourceMonitorService());

    public static AppResourceMonitorService Instance => _lazy.Value;

    private readonly IntPtr _currentProcessHandle = Win32Interop.GetCurrentProcess();
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);
    private readonly DispatcherTimer _timer;
    private int _consumerCount = 0;
    private readonly object _lock = new();

    // Main Process CPU Tracking
    private ulong _lastProcTime = 0;
    private long _lastProcTicks = 0;
    private double _smoothedProcCpu = 0;

    // WebView2 CPU Tracking
    private ulong _lastWebViewCpuTime = 0;
    private long _lastWebViewTicks = 0;
    private double _smoothedWebViewCpu = 0;

    // System CPU Tracking
    private ulong _lastSysIdle = 0;
    private ulong _lastSysKernel = 0;
    private ulong _lastSysUser = 0;
    private double _smoothedGlobalCpu = 0;

    public AppResourceSnapshot? LastSnapshot { get; private set; }
    public event EventHandler<AppResourceSnapshot>? SnapshotUpdated;

    private AppResourceMonitorService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
    }

    public void Start()
    {
        lock (_lock)
        {
            _consumerCount++;
            if (_consumerCount == 1)
            {
                GpuMonitorService.Instance.Start();
                _timer.Start();
                // Perform initial immediate sample
                _ = SampleCurrentSnapshot();
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_consumerCount > 0) _consumerCount--;
            if (_consumerCount == 0)
            {
                _timer.Stop();
                GpuMonitorService.Instance.Stop();
            }
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var snapshot = SampleCurrentSnapshot();
            SnapshotUpdated?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("APP-RESOURCE-MONITOR", $"Sample error: {ex.Message}");
        }
    }

    public AppResourceSnapshot SampleCurrentSnapshot()
    {
        long nowTicks = Stopwatch.GetTimestamp();

        // 1. Main Process (Pinn-Notch.exe) Metrics
        double mainCpu = SampleMainProcessCpu(nowTicks);
        SampleMainProcessMemory(out ulong mainRamBytes, out _, out int threadCount, out int handleCount);

        // 2. Child WebView2 Workers (YouTube Music Stream) Metrics
        var (webViewCpu, webViewRam, workerCount) = SampleWebViewMetrics(nowTicks);
        bool hasWorkers = workerCount > 0;

        // 3. Combined App Footprint
        double totalAppCpu = Math.Clamp(mainCpu + webViewCpu, 0, 100);
        ulong totalAppRam = mainRamBytes + webViewRam;

        // 4. GPU Metrics (Hardware / DirectX Engine Utilization)
        var (gpuName, _) = GpuMonitorService.Instance.GetGpuInfo();
        var (procGpu, globalGpu) = GpuMonitorService.Instance.GetGpuUsage();

        // 5. System Context
        double sysCpu = SampleSystemCpu();
        SampleSystemMemory(out ulong sysRamUsed, out ulong sysRamTotal, out double sysRamPercent);

        // 6. GC Heap
        long managedHeap = GC.GetTotalMemory(false);

        var snapshot = new AppResourceSnapshot
        {
            AppCpuPercent = totalAppCpu,
            AppGpuPercent = Math.Clamp(procGpu, 0, 100),
            AppRamBytes = totalAppRam,
            MainProcCpuPercent = mainCpu,
            MainProcRamBytes = mainRamBytes,
            HasWebViewWorkers = hasWorkers,
            WebViewCpuPercent = webViewCpu,
            WebViewRamBytes = webViewRam,
            WorkerProcessCount = workerCount,
            SystemCpuPercent = sysCpu,
            SystemGpuPercent = Math.Clamp(globalGpu, 0, 100),
            SystemRamPercent = sysRamPercent,
            SystemRamUsedBytes = sysRamUsed,
            SystemRamTotalBytes = sysRamTotal,
            GpuName = gpuName,
            ThreadCount = threadCount,
            HandleCount = handleCount,
            ManagedHeapBytes = managedHeap > 0 ? (ulong)managedHeap : 0,
            Timestamp = DateTime.Now
        };

        LastSnapshot = snapshot;
        return snapshot;
    }

    private double SampleMainProcessCpu(long nowTicks)
    {
        try
        {
            if (!Win32Interop.GetProcessTimes(_currentProcessHandle, out _, out _, out var procKernel, out var procUser))
            {
                return 0.0;
            }

            ulong procTotalTime = procKernel.ToUInt64() + procUser.ToUInt64();
            if (_lastProcTicks > 0 && nowTicks > _lastProcTicks)
            {
                long tickDelta = nowTicks - _lastProcTicks;
                double wallClockSeconds = (double)tickDelta / Stopwatch.Frequency;
                if (wallClockSeconds > 0.001)
                {
                    ulong delta100ns = procTotalTime >= _lastProcTime ? procTotalTime - _lastProcTime : 0;
                    double processSeconds = delta100ns / 10_000_000.0;
                    double rawCpu = (processSeconds / (wallClockSeconds * _processorCount)) * 100.0;
                    _smoothedProcCpu = (_smoothedProcCpu * 0.3) + (rawCpu * 0.7);
                }
            }

            _lastProcTime = procTotalTime;
            _lastProcTicks = nowTicks;
            return Math.Clamp(_smoothedProcCpu, 0, 100);
        }
        catch
        {
            return 0.0;
        }
    }

    private (double CpuPercent, ulong RamBytes, int ProcessCount) SampleWebViewMetrics(long nowTicks)
    {
        try
        {
            var ourChildPids = AppMemoryOptimizer.GetDescendantProcessIds();
            if (ourChildPids.Count == 0)
            {
                _lastWebViewTicks = nowTicks;
                _smoothedWebViewCpu = 0;
                return (0, 0, 0);
            }

            var procs = Process.GetProcessesByName("msedgewebview2");
            if (procs.Length == 0)
            {
                _lastWebViewTicks = nowTicks;
                _smoothedWebViewCpu = 0;
                return (0, 0, 0);
            }

            ulong totalRam = 0;
            ulong totalCpuTime100ns = 0;
            int count = 0;

            foreach (var p in procs)
            {
                try
                {
                    if (ourChildPids.Contains(p.Id))
                    {
                        totalRam += (ulong)p.WorkingSet64;
                        totalCpuTime100ns += (ulong)p.TotalProcessorTime.Ticks;
                        count++;
                    }
                }
                catch
                {
                    // Ignore transient process access errors
                }
                finally
                {
                    p.Dispose();
                }
            }

            if (count == 0)
            {
                _lastWebViewTicks = nowTicks;
                _smoothedWebViewCpu = 0;
                return (0, 0, 0);
            }

            if (_lastWebViewTicks > 0 && nowTicks > _lastWebViewTicks && count > 0)
            {
                long tickDelta = nowTicks - _lastWebViewTicks;
                double wallClockSeconds = (double)tickDelta / Stopwatch.Frequency;
                if (wallClockSeconds > 0.001)
                {
                    ulong delta100ns = totalCpuTime100ns >= _lastWebViewCpuTime ? totalCpuTime100ns - _lastWebViewCpuTime : 0;
                    double processSeconds = delta100ns / 10_000_000.0;
                    double raw = (processSeconds / (wallClockSeconds * _processorCount)) * 100.0;
                    _smoothedWebViewCpu = (_smoothedWebViewCpu * 0.3) + (raw * 0.7);
                }
            }
            else
            {
                _smoothedWebViewCpu = 0;
            }

            _lastWebViewCpuTime = totalCpuTime100ns;
            _lastWebViewTicks = nowTicks;

            return (Math.Clamp(_smoothedWebViewCpu, 0, 100), totalRam, count);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private void SampleMainProcessMemory(out ulong workingSet, out ulong privateBytes, out int threadCount, out int handleCount)
    {
        workingSet = 0;
        privateBytes = 0;
        threadCount = 1;
        handleCount = 0;

        try
        {
            uint cb = (uint)Marshal.SizeOf<Win32Interop.PROCESS_MEMORY_COUNTERS_EX>();
            if (Win32Interop.GetProcessMemoryInfo(_currentProcessHandle, out var memCounters, cb))
            {
                workingSet = (ulong)memCounters.WorkingSetSize;
                privateBytes = (ulong)memCounters.PrivateUsage;
            }

            using var p = Process.GetCurrentProcess();
            threadCount = p.Threads.Count;
            handleCount = p.HandleCount;
        }
        catch
        {
            // Best effort
        }
    }

    private double SampleSystemCpu()
    {
        try
        {
            if (!Win32Interop.GetSystemTimes(out var sysIdle, out var sysKernel, out var sysUser))
            {
                return 0.0;
            }

            ulong idle = sysIdle.ToUInt64();
            ulong kernel = sysKernel.ToUInt64();
            ulong user = sysUser.ToUInt64();

            if (_lastSysIdle > 0)
            {
                ulong idleDelta = idle >= _lastSysIdle ? idle - _lastSysIdle : 0;
                ulong kernelDelta = kernel >= _lastSysKernel ? kernel - _lastSysKernel : 0;
                ulong userDelta = user >= _lastSysUser ? user - _lastSysUser : 0;
                ulong totalDelta = kernelDelta + userDelta;
                if (totalDelta > 0)
                {
                    double raw = (double)(totalDelta - Math.Min(totalDelta, idleDelta)) / totalDelta * 100.0;
                    _smoothedGlobalCpu = (_smoothedGlobalCpu * 0.3) + (raw * 0.7);
                }
            }

            _lastSysIdle = idle;
            _lastSysKernel = kernel;
            _lastSysUser = user;
            return Math.Clamp(_smoothedGlobalCpu, 0, 100);
        }
        catch
        {
            return 0.0;
        }
    }

    private static void SampleSystemMemory(out ulong ramUsed, out ulong ramTotal, out double ramPercent)
    {
        var memStatus = new Win32Interop.MEMORYSTATUSEX_METRICS
        {
            dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX_METRICS>()
        };

        if (Win32Interop.GlobalMemoryStatusEx(ref memStatus))
        {
            ramTotal = memStatus.ullTotalPhys;
            ulong avail = memStatus.ullAvailPhys;
            ramUsed = ramTotal > avail ? ramTotal - avail : 0;
            ramPercent = memStatus.dwMemoryLoad;
        }
        else
        {
            ramUsed = 0;
            ramTotal = 0;
            ramPercent = 0;
        }
    }

    public static string FormatBytes(ulong bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1024.0)
        {
            return (mb / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
        }
        return mb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }
}
