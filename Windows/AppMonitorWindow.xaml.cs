using System;
using System.Windows;
using System.Windows.Input;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Windows;

public partial class AppMonitorWindow : Window
{
    private bool _isCompact = false;
    private bool _isTopmost = true;

    public AppMonitorWindow()
    {
        InitializeComponent();

        Loaded += AppMonitorWindow_Loaded;
        IsVisibleChanged += AppMonitorWindow_IsVisibleChanged;
        Closed += AppMonitorWindow_Closed;
    }

    private void AppMonitorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if ((double.IsNaN(Left) || Left <= 0) && (double.IsNaN(Top) || Top <= 0))
        {
            // Position on top-right corner of primary screen by default
            double screenW = SystemParameters.WorkArea.Width;
            Left = Math.Max(20, screenW - Width - 30);
            Top = 60;
        }

        AppMemoryOptimizer.OptimizeMemory(aggressive: true);
        AppResourceMonitorService.Instance.SnapshotUpdated += OnSnapshotUpdated;
        AppResourceMonitorService.Instance.Start();

        var initial = AppResourceMonitorService.Instance.SampleCurrentSnapshot();
        UpdateUI(initial);
    }

    private void TrimRamButton_Click(object sender, RoutedEventArgs e)
    {
        AppMemoryOptimizer.OptimizeMemory(aggressive: true);
        var snapshot = AppResourceMonitorService.Instance.SampleCurrentSnapshot();
        UpdateUI(snapshot);
    }

    private void AppMonitorWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            AppMemoryOptimizer.OptimizeMemory(aggressive: true);
            AppResourceMonitorService.Instance.Start();
            var snapshot = AppResourceMonitorService.Instance.SampleCurrentSnapshot();
            UpdateUI(snapshot);
        }
        else
        {
            AppResourceMonitorService.Instance.Stop();
        }
    }

    private void AppMonitorWindow_Closed(object? sender, EventArgs e)
    {
        AppResourceMonitorService.Instance.SnapshotUpdated -= OnSnapshotUpdated;
        AppResourceMonitorService.Instance.Stop();
    }

    private void OnSnapshotUpdated(object? sender, AppResourceSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateUI(snapshot));
            return;
        }
        UpdateUI(snapshot);
    }

    private void UpdateUI(AppResourceSnapshot snapshot)
    {
        if (snapshot == null) return;

        // 1. CPU Usage
        CpuValueText.Text = $"{snapshot.AppCpuPercent:0.0}%";
        CompactCpuText.Text = $"{snapshot.AppCpuPercent:0.0}%";
        SetBarProgress(CpuBar, snapshot.AppCpuPercent, 100);

        CpuMainProcText.Text = $"Pinn-Notch: {snapshot.MainProcCpuPercent:0.0}%";
        if (snapshot.HasWebViewWorkers)
        {
            CpuWebViewBadge.Visibility = Visibility.Visible;
            CpuWebViewText.Text = $"YTM Web: {snapshot.WebViewCpuPercent:0.0}%";
        }
        else
        {
            CpuWebViewBadge.Visibility = Visibility.Collapsed;
        }
        CpuSystemText.Text = $"System: {snapshot.SystemCpuPercent:0.0}%";

        // 2. GPU Usage
        GpuValueText.Text = $"{snapshot.AppGpuPercent:0.0}%";
        CompactGpuText.Text = $"{snapshot.AppGpuPercent:0.0}%";
        SetBarProgress(GpuBar, snapshot.AppGpuPercent, 100);

        string gpuName = string.IsNullOrWhiteSpace(snapshot.GpuName) ? "GPU" : snapshot.GpuName;
        GpuNameText.Text = $"Adapter: {gpuName}";
        GpuSystemText.Text = $"System: {snapshot.SystemGpuPercent:0.0}%";

        // 3. RAM Usage
        string ramStr = AppResourceMonitorService.FormatBytes(snapshot.AppRamBytes);
        RamValueText.Text = ramStr;
        CompactRamText.Text = ramStr;
        // Scale bar to 512MB reference
        double ramScalePercent = Math.Clamp((snapshot.AppRamBytes / 1024.0 / 1024.0) / 512.0 * 100.0, 0, 100);
        SetBarProgress(RamBar, ramScalePercent, 100);

        RamMainProcText.Text = $"Pinn-Notch: {AppResourceMonitorService.FormatBytes(snapshot.MainProcRamBytes)}";
        if (snapshot.HasWebViewWorkers)
        {
            RamWebViewBadge.Visibility = Visibility.Visible;
            RamWebViewText.Text = $"YTM Web: {AppResourceMonitorService.FormatBytes(snapshot.WebViewRamBytes)}";
        }
        else
        {
            RamWebViewBadge.Visibility = Visibility.Collapsed;
        }
        string sysUsed = AppResourceMonitorService.FormatBytes(snapshot.SystemRamUsedBytes);
        string sysTotal = AppResourceMonitorService.FormatBytes(snapshot.SystemRamTotalBytes);
        RamSystemText.Text = $"Sys: {sysUsed}/{sysTotal} ({snapshot.SystemRamPercent:0}%)";

        // 4. Diagnostics Footer
        string heapStr = AppResourceMonitorService.FormatBytes(snapshot.ManagedHeapBytes);
        ProcSummaryText.Text = $"Threads: {snapshot.ThreadCount} • Handles: {snapshot.HandleCount} • Heap: {heapStr}";
    }

    private static void SetBarProgress(System.Windows.Controls.Border bar, double value, double max)
    {
        if (bar == null) return;
        double widthFraction = Math.Clamp(value / max, 0.0, 1.0);
        const double maxBarWidth = 296.0;
        bar.Width = Math.Max(4.0, widthFraction * maxBarWidth);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        _isTopmost = !_isTopmost;
        Topmost = _isTopmost;
        PinIconPath.Fill = _isTopmost
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xD2, 0xFF))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
    }

    private void BtnCompact_Click(object sender, RoutedEventArgs e)
    {
        SetCompactMode(true);
    }

    private void BtnExpand_Click(object sender, RoutedEventArgs e)
    {
        SetCompactMode(false);
    }

    private void SetCompactMode(bool compact)
    {
        _isCompact = compact;
        if (_isCompact)
        {
            FullCardPanel.Visibility = Visibility.Collapsed;
            CompactPillPanel.Visibility = Visibility.Visible;
            Width = Double.NaN;
        }
        else
        {
            CompactPillPanel.Visibility = Visibility.Collapsed;
            FullCardPanel.Visibility = Visibility.Visible;
            Width = 360;
        }
    }
}
