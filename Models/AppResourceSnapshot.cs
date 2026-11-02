using System;

namespace VNotch.Models;

public sealed class AppResourceSnapshot
{
    // Combined Total (Pinn-Notch Main Process + WebView2 Child Workers)
    public double AppCpuPercent { get; init; }
    public double AppGpuPercent { get; init; }
    public ulong AppRamBytes { get; init; }

    // Breakdown: Main Application Process (Pinn-Notch.exe)
    public double MainProcCpuPercent { get; init; }
    public ulong MainProcRamBytes { get; init; }

    // Breakdown: Background WebView2 Workers (YouTube Music Stream)
    public bool HasWebViewWorkers { get; init; }
    public double WebViewCpuPercent { get; init; }
    public ulong WebViewRamBytes { get; init; }
    public int WorkerProcessCount { get; init; }

    // Global System Context (for comparison)
    public double SystemCpuPercent { get; init; }
    public double SystemGpuPercent { get; init; }
    public double SystemRamPercent { get; init; }
    public ulong SystemRamUsedBytes { get; init; }
    public ulong SystemRamTotalBytes { get; init; }

    // Process Health Diagnostics
    public string GpuName { get; init; } = "";
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public ulong ManagedHeapBytes { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
