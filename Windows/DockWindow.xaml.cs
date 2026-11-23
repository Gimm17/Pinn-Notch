using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Windows;

public partial class DockWindow : Window
{
    private class DockItemState
    {
        public Grid Grid { get; set; } = null!;
        public ScaleTransform Scale { get; set; } = null!;
        public TranslateTransform Trans { get; set; } = null!;
        public Border Indicator { get; set; } = null!;
        public Border? ActiveGlow { get; set; }
        public Border? SquircleBorder { get; set; }
        public TextBlock? FallbackInitial { get; set; }
        public string Title { get; set; } = "";
        public SpringValue ScaleSpring = new(1.0);
        public SpringValue TransXSpring = new(0.0);
        public SpringValue TransYSpring = new(0.0);
        public bool IsPinned { get; set; } = true;
        public string[] ProcessNames { get; set; } = Array.Empty<string>();
        public string ExePath { get; set; } = "";
        public IntPtr WindowHwnd { get; set; } = IntPtr.Zero;
        public object? Tag { get; set; }
        public bool IsRunning { get; set; }
        public bool IsActive { get; set; }
    }

    private sealed class RunningAppInfo
    {
        public string Key { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Title { get; set; } = "";
        public IntPtr Hwnd { get; set; }
        public bool IsActive { get; set; }
        public ImageSource? Icon { get; set; }
    }

    private readonly ISettingsService _settingsService;
    private readonly WindowOverlapMonitor _overlapMonitor;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _procCheckTimer;
    private readonly DispatcherTimer _cursorPollTimer;
    private readonly List<DockItemState> _items;
    private readonly Debouncer _fastCheckDebouncer;
    private IntPtr _winEventHook = IntPtr.Zero;
    private WinEventDelegate? _winEventDelegate;
    private int _isCheckingApps = 0;
    private volatile bool _isFullscreenAppActive = false;
    private readonly IBatteryService? _batteryService;
    private readonly IVolumeService? _volumeService;
    private long _lastBytesReceived = 0;
    private long _lastBytesSent = 0;
    private long _lastNetworkTick = 0;

    private bool _isRenderLoopActive;
    private long _lastRenderTimestamp;
    private SpringValue _tooltipXSpring = new(0.0);
    private SpringValue _tooltipYSpring = new(0.0);

    private bool _isDockOverlapped;
    private bool _isDockSlidDown;
    private bool _isRevealedByMouse;
    private long _revealStartTick;
    private long _hideStartTick;
    private AppDrawerWindow? _appDrawer;
    private SystemTrayPopupWindow? _systemTrayPopup;
    private bool _isContextMenuOpen;
    private bool _isTrayMenuHoldActive;
    private IReadOnlyList<TrayIconItem> _cachedTrayIcons = Array.Empty<TrayIconItem>();
    private long _lastTrayCacheTicks;
    private int _lastLoggedTrayCount = -1;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint GW_OWNER = 4;
    private const uint WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int SW_MINIMIZE = 6;

    public DockWindow(ISettingsService settingsService, WindowOverlapMonitor overlapMonitor, IBatteryService? batteryService = null, IVolumeService? volumeService = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _overlapMonitor = overlapMonitor;
        _batteryService = batteryService;
        _volumeService = volumeService;
        _overlapMonitor.RegisterDockRegionProvider(GetDockScreenBounds);
        _overlapMonitor.DockOverlapChanged += OnDockOverlapChanged;

        _items = new List<DockItemState>
        {
            new DockItemState { Grid = ItemExplorer, Scale = ScaleExplorer, Trans = TransExplorer, Indicator = IndicatorExplorer, ActiveGlow = ActiveGlowExplorer, Title = "File Explorer", IsPinned = true, ProcessNames = new[] { "explorer" } },
            new DockItemState { Grid = ItemBrowser, Scale = ScaleBrowser, Trans = TransBrowser, Indicator = IndicatorBrowser, ActiveGlow = ActiveGlowBrowser, Title = "Browser", IsPinned = true, ProcessNames = new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc" } },
            new DockItemState { Grid = ItemChat, Scale = ScaleChat, Trans = TransChat, Indicator = IndicatorChat, ActiveGlow = ActiveGlowChat, Title = "Pesan & Chat", IsPinned = true, ProcessNames = new[] { "whatsapp", "discord", "telegram", "teams", "slack" } },
            new DockItemState { Grid = ItemMusic, Scale = ScaleMusic, Trans = TransMusic, Indicator = IndicatorMusic, ActiveGlow = ActiveGlowMusic, Title = "Musik", IsPinned = true, ProcessNames = new[] { "spotify", "music", "itunes", "wmplayer", "vlc" } },
            new DockItemState { Grid = ItemNotes, Scale = ScaleNotes, Trans = TransNotes, Indicator = IndicatorNotes, ActiveGlow = ActiveGlowNotes, Title = "Catatan", IsPinned = true, ProcessNames = new[] { "notepad", "onenote" } },
            new DockItemState { Grid = ItemCalendar, Scale = ScaleCalendar, Trans = TransCalendar, Indicator = IndicatorCalendar, ActiveGlow = ActiveGlowCalendar, Title = "Kalender", IsPinned = true, ProcessNames = new[] { "olk", "outlook", "time", "calendar" } },
            new DockItemState { Grid = ItemSettings, Scale = ScaleSettings, Trans = TransSettings, Indicator = IndicatorSettings, ActiveGlow = ActiveGlowSettings, Title = "Pengaturan", IsPinned = true, ProcessNames = new[] { "systemsettings" } }
        };

        // Live Clock Timer
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClockAndCalendar();
            UpdateNetworkSpeed();
            UpdateBatteryStatus();
            UpdateVolumeStatus();
        };
        _clockTimer.Start();
        UpdateNetworkSpeed();
        UpdateBatteryStatus();
        UpdateVolumeStatus();
        UpdateClockAndCalendar();

        _fastCheckDebouncer = new Debouncer(TimeSpan.FromMilliseconds(200));

        // Process running check timer (adaptive: 1.5s when visible, 5s when slid down)
        _procCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _procCheckTimer.Tick += (_, _) => CheckRunningApps();
        _procCheckTimer.Start();
        CheckRunningApps();

        // Native 0ms event hook for instant window focus / Alt+Tab tracking
        _winEventDelegate = OnForegroundChanged;
        _winEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // High-frequency, lightweight cursor poll timer (checks only point-in-rect for reveal trigger)
        _cursorPollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        _cursorPollTimer.Tick += CursorPollTimer_Tick;
        _cursorPollTimer.Start();

        Loaded += DockWindow_Loaded;
        SizeChanged += DockWindow_SizeChanged;
    }

    private void DockWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RepositionAtBottom();
        InitializeDockLiquidGlass();
        _overlapMonitor.TriggerRecheck();
    }

    private void DockWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RepositionAtBottom();
        _overlapMonitor.TriggerRecheck();
    }

    public void RepositionAtBottom()
    {
        var workArea = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 580;
        Left = workArea.Left + (workArea.Width - w) / 2;
        Top = Math.Max(0, workArea.Bottom - Height - 8);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RepositionAtBottom();
    }

    private void UpdateClockAndCalendar()
    {
        var now = DateTime.Now;
        ClockTimeText.Text = now.ToString("HH:mm");
        ClockDateText.Text = now.ToString("dd/MM/yyyy");

        try
        {
            CalendarMonthText.Text = now.ToString("MMM", new CultureInfo("id-ID")).ToUpperInvariant();
        }
        catch
        {
            CalendarMonthText.Text = now.ToString("MMM").ToUpperInvariant();
        }
        CalendarDayText.Text = now.ToString("dd");
    }

    private void UpdateNetworkSpeed()
    {
        long now = Stopwatch.GetTimestamp();
        long currentBytesReceived = 0;
        long currentBytesSent = 0;

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                {
                    var stats = ni.GetIPStatistics();
                    currentBytesReceived += stats.BytesReceived;
                    currentBytesSent += stats.BytesSent;
                }
            }
        }
        catch
        {
            return;
        }

        if (_lastNetworkTick > 0)
        {
            double elapsedSeconds = (double)(now - _lastNetworkTick) / Stopwatch.Frequency;
            if (elapsedSeconds > 0.4)
            {
                long deltaReceived = Math.Max(0, currentBytesReceived - _lastBytesReceived);
                long deltaSent = Math.Max(0, currentBytesSent - _lastBytesSent);

                double bytesPerSecDown = deltaReceived / elapsedSeconds;
                double bytesPerSecUp = deltaSent / elapsedSeconds;

                NetSpeedDownText.Text = FormatNetworkSpeed(bytesPerSecDown);
                NetSpeedWidget.ToolTip = $"Internet Real-Time:\n↓ Download: {FormatNetworkSpeed(bytesPerSecDown)}\n↑ Upload: {FormatNetworkSpeed(bytesPerSecUp)}\n\n(Klik untuk Pengaturan Jaringan)";
            }
        }

        _lastBytesReceived = currentBytesReceived;
        _lastBytesSent = currentBytesSent;
        _lastNetworkTick = now;
    }

    private static string FormatNetworkSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0)
            return "0 B/s";
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024.0 * 1024.0)
            return $"{(bytesPerSec / 1024.0):0} KB/s";
        if (bytesPerSec < 1024.0 * 1024.0 * 1024.0)
        {
            double mb = bytesPerSec / (1024.0 * 1024.0);
            return mb >= 100 ? $"{mb:0} MB/s" : $"{mb:0.1} MB/s";
        }
        return $"{(bytesPerSec / (1024.0 * 1024.0 * 1024.0)):0.1} GB/s";
    }

    private void UpdateBatteryStatus()
    {
        try
        {
            var batterySvc = _batteryService ?? App.Services?.GetService(typeof(IBatteryService)) as IBatteryService;
            var info = batterySvc?.GetBatteryInfo();

            bool isLight = IsLightGlassBackdrop;
            var primaryFg = new SolidColorBrush(isLight
                ? Color.FromArgb(0xEB, 0x14, 0x17, 0x1F)
                : Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            primaryFg.Freeze();

            if (info == null || !info.HasBattery || info.Percentage < 0)
            {
                // Desktop PC or AC connected
                BatteryFill.Width = 15.6;
                BatteryFill.Background = new SolidColorBrush(isLight ? Color.FromRgb(0x02, 0x84, 0xC7) : Color.FromRgb(0x38, 0xBD, 0xF8));
                BatteryChargingBolt.Visibility = Visibility.Visible;
                BatteryPercentText.Text = "AC";
                BatteryPercentText.Foreground = primaryFg;
                BatteryWidget.ToolTip = "Daya Listrik AC (Desktop / Wall Plug / Dock)\n(Klik untuk Pengaturan Daya)";
                return;
            }

            int percent = Math.Clamp(info.Percentage, 0, 100);
            BatteryPercentText.Text = $"{percent}%";

            // Max inner width is 15.6 DIPs (inner cavity: 20 frame width - 2.4 borders - 2 margin)
            double fillWidth = Math.Max(1.5, (percent / 100.0) * 15.6);
            BatteryFill.Width = fillWidth;

            if (info.IsCharging || info.IsPluggedIn)
            {
                BatteryChargingBolt.Visibility = Visibility.Visible;
                BatteryFill.Background = new SolidColorBrush(isLight ? Color.FromRgb(0x16, 0xA3, 0x4A) : Color.FromRgb(0x4A, 0xDE, 0x80));
                BatteryPercentText.Foreground = primaryFg;
                BatteryWidget.ToolTip = $"Baterai: {percent}%\nStatus: Sedang Mengisi Daya (Charging)\nMode: Terhubung Listrik (AC Adapter / Dock)\n\n(Klik untuk Pengaturan Daya)";
            }
            else
            {
                BatteryChargingBolt.Visibility = Visibility.Collapsed;
                if (percent <= 20)
                {
                    var lowBrush = new SolidColorBrush(isLight ? Color.FromRgb(0xDC, 0x26, 0x26) : Color.FromRgb(0xF8, 0x71, 0x71));
                    lowBrush.Freeze();
                    BatteryFill.Background = lowBrush;
                    BatteryPercentText.Foreground = lowBrush;
                }
                else
                {
                    var fillBrush = new SolidColorBrush(isLight
                        ? Color.FromArgb(0xDC, 0x16, 0x1A, 0x24)
                        : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
                    fillBrush.Freeze();
                    BatteryFill.Background = fillBrush;
                    BatteryPercentText.Foreground = primaryFg;
                }

                string remTime = info.RemainingMinutes > 0 ? $"{info.RemainingMinutes / 60}j {info.RemainingMinutes % 60}m" : "Menghitung...";
                BatteryWidget.ToolTip = $"Baterai: {percent}%\nStatus: Menggunakan Baterai\nPerkiraan Sisa Waktu: {remTime}\n\n(Klik untuk Pengaturan Daya)";
            }
        }
        catch
        {
            // Fallback gracefully
        }
    }

    private void UpdateVolumeStatus()
    {
        try
        {
            bool isLight = IsLightGlassBackdrop;
            var primaryFg = new SolidColorBrush(isLight
                ? Color.FromArgb(0xEB, 0x14, 0x17, 0x1F)
                : Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            primaryFg.Freeze();
            var iconFg = new SolidColorBrush(isLight
                ? Color.FromArgb(0xD8, 0x16, 0x1A, 0x24)
                : Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
            iconFg.Freeze();

            var volumeSvc = _volumeService ?? App.Services?.GetService(typeof(IVolumeService)) as IVolumeService;
            if (volumeSvc == null || !volumeSvc.IsAvailable)
            {
                VolumeIcon.Text = "\uE995";
                VolumeIcon.Foreground = iconFg;
                VolumePercentText.Text = "--";
                VolumePercentText.Foreground = primaryFg;
                return;
            }

            bool isMuted = volumeSvc.GetMute();
            float vol = volumeSvc.GetVolume();
            int volPercent = (int)Math.Round(vol * 100);

            if (isMuted || volPercent == 0)
            {
                var muteBrush = new SolidColorBrush(isLight ? Color.FromRgb(0xDC, 0x26, 0x26) : Color.FromRgb(0xE0, 0x40, 0x40));
                muteBrush.Freeze();
                VolumeIcon.Text = "\uE74F"; // Mute glyph
                VolumeIcon.Foreground = muteBrush;
                VolumePercentText.Text = "Mute";
                VolumePercentText.Foreground = muteBrush;
                VolumeWidget.ToolTip = "Volume Master: Mute (Bungkam)\n• Klik untuk Nyalakan Suara\n• Scroll mouse untuk atur volume";
            }
            else
            {
                if (volPercent < 33)
                    VolumeIcon.Text = "\uE993"; // Low
                else if (volPercent < 66)
                    VolumeIcon.Text = "\uE994"; // Medium
                else
                    VolumeIcon.Text = "\uE995"; // High

                VolumeIcon.Foreground = iconFg;
                VolumePercentText.Text = $"{volPercent}%";
                VolumePercentText.Foreground = primaryFg;
                VolumeWidget.ToolTip = $"Volume Master: {volPercent}%\n• Klik untuk Mute / Unmute\n• Scroll mouse untuk atur volume";
            }
        }
        catch
        {
            // Fallback gracefully
        }
    }

    private void VolumeWidget_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            var volumeSvc = _volumeService ?? App.Services?.GetService(typeof(IVolumeService)) as IVolumeService;
            if (volumeSvc != null && volumeSvc.IsAvailable)
            {
                volumeSvc.ToggleMute();
                UpdateVolumeStatus();
            }
        }
        catch { }
    }

    private void VolumeWidget_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        try
        {
            var volumeSvc = _volumeService ?? App.Services?.GetService(typeof(IVolumeService)) as IVolumeService;
            if (volumeSvc != null && volumeSvc.IsAvailable)
            {
                float currentVol = volumeSvc.GetVolume();
                float delta = e.Delta > 0 ? 0.02f : -0.02f;
                float newVol = Math.Clamp(currentVol + delta, 0.0f, 1.0f);
                volumeSvc.SetVolume(newVol);
                if (volumeSvc.GetMute() && e.Delta > 0)
                {
                    volumeSvc.SetMute(false);
                }
                UpdateVolumeStatus();
            }
        }
        catch { }
    }

    private void BatteryWidget_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true });
        }
        catch { }
    }

    private void NetSpeedWidget_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });
        }
        catch { }
    }

    private static string GetProcessExecutablePath(uint pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(hProc, 0, sb, ref size))
                {
                    return sb.ToString();
                }
            }
            finally
            {
                CloseHandle(hProc);
            }
        }
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static ImageSource? GetWindowIcon(IntPtr hwnd, string exePath)
    {
        if (!string.IsNullOrEmpty(exePath))
        {
            try
            {
                var icon = FileIconProvider.GetAppIcon(exePath, small: false);
                if (icon != null) return icon;
            }
            catch { }
        }

        try
        {
            IntPtr hIcon = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);

            if (hIcon != IntPtr.Zero)
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                DestroyIcon(hIcon);
                return src;
            }
        }
        catch { }

        return null;
    }

    private void SetItemActiveState(DockItemState item, bool isRunning, bool isActive)
    {
        if (item.IsRunning == isRunning && item.IsActive == isActive) return;
        item.IsRunning = isRunning;
        item.IsActive = isActive;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(200);

        // Indicator Opacity
        double targetIndOpacity = isRunning ? 1.0 : 0.0;
        item.Indicator.BeginAnimation(UIElement.OpacityProperty, 
            new DoubleAnimation(targetIndOpacity, dur) { EasingFunction = ease });

        // Active Glow Opacity
        if (item.ActiveGlow != null)
        {
            double targetGlowOpacity = isActive ? 1.0 : 0.0;
            item.ActiveGlow.BeginAnimation(UIElement.OpacityProperty, 
                new DoubleAnimation(targetGlowOpacity, dur) { EasingFunction = ease });
        }

        // Indicator Width & Height
        double targetWidth = isActive ? 14.0 : 5.0;
        double targetHeight = isActive ? 3.5 : 5.0;
        double targetRadius = isActive ? 1.75 : 2.5;

        item.Indicator.BeginAnimation(FrameworkElement.WidthProperty, 
            new DoubleAnimation(targetWidth, dur) { EasingFunction = ease });
        item.Indicator.BeginAnimation(FrameworkElement.HeightProperty, 
            new DoubleAnimation(targetHeight, dur) { EasingFunction = ease });
        item.Indicator.CornerRadius = new CornerRadius(targetRadius);

        ApplyIndicatorColors(item, isActive);
    }

    private void ApplyIndicatorColors(DockItemState item, bool isActive)
    {
        bool isLight = IsLightGlassBackdrop;
        if (isActive)
        {
            var bg = new SolidColorBrush(isLight
                ? Color.FromRgb(0x14, 0x17, 0x1F)
                : Color.FromRgb(0xFF, 0xFF, 0xFF));
            bg.Freeze();
            item.Indicator.Background = bg;
            item.Indicator.Effect = new DropShadowEffect
            {
                Color = isLight ? Color.FromRgb(0x02, 0x84, 0xC7) : Color.FromRgb(0x38, 0xBD, 0xF8),
                BlurRadius = isLight ? 6 : 8,
                ShadowDepth = 0,
                Opacity = isLight ? 0.55 : 0.85
            };
        }
        else
        {
            var bg = new SolidColorBrush(isLight
                ? Color.FromArgb(0x8A, 0x1E, 0x24, 0x30)
                : Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
            bg.Freeze();
            item.Indicator.Background = bg;
            item.Indicator.Effect = null;
        }
    }

    private void ApplyDynamicItemAdaptiveBrushes(DockItemState state)
    {
        bool isLight = IsLightGlassBackdrop;
        if (state.SquircleBorder != null)
        {
            var borderBrush = new SolidColorBrush(isLight
                ? Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
            borderBrush.Freeze();
            state.SquircleBorder.BorderBrush = borderBrush;

            var tileBg = new LinearGradientBrush(
                isLight ? Color.FromArgb(0x58, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
                isLight ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                90.0);
            tileBg.Freeze();
            state.SquircleBorder.Background = tileBg;
        }

        if (state.FallbackInitial != null)
        {
            var textFg = new SolidColorBrush(isLight
                ? Color.FromArgb(0xEB, 0x14, 0x17, 0x1F)
                : Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            textFg.Freeze();
            state.FallbackInitial.Foreground = textFg;
        }

        if (state.ActiveGlow != null)
        {
            var glowBorder = new SolidColorBrush(isLight
                ? Color.FromArgb(0x72, 0x02, 0x84, 0xC7)
                : Color.FromArgb(0x75, 0xFF, 0xFF, 0xFF));
            glowBorder.Freeze();
            var glowBg = new SolidColorBrush(isLight
                ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            glowBg.Freeze();
            state.ActiveGlow.BorderBrush = glowBorder;
            state.ActiveGlow.Background = glowBg;
        }
    }

    internal void RefreshAdaptiveStatusColors()
    {
        if (_isCleanedUp) return;
        UpdateBatteryStatus();
        UpdateVolumeStatus();

        List<DockItemState> snapshot;
        lock (_items)
        {
            snapshot = new List<DockItemState>(_items);
        }

        foreach (var item in snapshot)
        {
            ApplyIndicatorColors(item, item.IsActive);
            if (!item.IsPinned)
                ApplyDynamicItemAdaptiveBrushes(item);
        }
    }

    private DockItemState CreateDynamicDockItem(RunningAppInfo app)
    {
        var grid = new Grid
        {
            Width = 44,
            Height = 52,
            Margin = new Thickness(6, 0, 6, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Tag = app.Key
        };

        var scale = new ScaleTransform(1.0, 1.0, 22, 44);
        var trans = new TranslateTransform(0, 0);
        grid.RenderTransform = new TransformGroup
        {
            Children = new TransformCollection { scale, trans }
        };

        // Clear Adaptive Glass Squircle Container
        var squircleBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12.5),
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1)
        };
        squircleBorder.Effect = new DropShadowEffect
        {
            BlurRadius = 8,
            Color = Colors.Black,
            Opacity = 0.20,
            ShadowDepth = 2,
            Direction = 270
        };

        TextBlock? fallbackInitial = null;
        // App Icon
        if (app.Icon != null)
        {
            var iconImg = new Image
            {
                Width = 26,
                Height = 26,
                Source = app.Icon,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);
            squircleBorder.Child = iconImg;
        }
        else
        {
            string initial = string.IsNullOrWhiteSpace(app.Title) ? "?" : app.Title[0].ToString().ToUpperInvariant();
            fallbackInitial = new TextBlock
            {
                Text = initial,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            squircleBorder.Child = fallbackInitial;
        }
        grid.Children.Add(squircleBorder);

        // Subtle Frosted Active Halo
        var activeGlow = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12.5),
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1.2),
            IsHitTestVisible = false,
            Opacity = 0.0
        };
        grid.Children.Add(activeGlow);

        // Running / Active Indicator Pill
        var indicator = new Border
        {
            Width = 5,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1),
            Opacity = 1.0
        };
        grid.Children.Add(indicator);

        var state = new DockItemState
        {
            Grid = grid,
            Scale = scale,
            Trans = trans,
            Indicator = indicator,
            ActiveGlow = activeGlow,
            SquircleBorder = squircleBorder,
            FallbackInitial = fallbackInitial,
            Title = app.Title,
            IsPinned = false,
            WindowHwnd = app.Hwnd,
            ExePath = app.ExePath,
            Tag = app.Key,
            ProcessNames = new[] { app.ProcessName }
        };

        ApplyDynamicItemAdaptiveBrushes(state);
        ApplyIndicatorColors(state, app.IsActive);

        grid.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            AnimateBounce(grid);
            DynamicApp_Click(state);
        };

        grid.MouseRightButtonUp += (s, e) =>
        {
            e.Handled = true;
            ShowAppContextMenu(state);
        };

        // Entrance fade-in animation
        grid.Opacity = 0.0;
        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        grid.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        return state;
    }

    private void DynamicApp_Click(DockItemState item)
    {
        if (item.WindowHwnd == IntPtr.Zero) return;
        IntPtr hwnd = item.WindowHwnd;

        if (!IsWindow(hwnd))
        {
            CheckRunningApps();
            return;
        }

        IntPtr foreground = Win32Interop.GetForegroundWindow();
        if (foreground == hwnd)
        {
            Win32Interop.ShowWindow(hwnd, SW_MINIMIZE);
            item.IsActive = false;
            SetItemActiveState(item, isRunning: true, isActive: false);
        }
        else
        {
            if (Win32Interop.IsIconic(hwnd))
            {
                Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);
            }
            Win32Interop.SetForegroundWindow(hwnd);
            item.IsActive = true;
            SetItemActiveState(item, isRunning: true, isActive: true);
        }

        CheckRunningApps();
    }

    private void UpdateRunningAppsUI(List<RunningAppInfo> unpinnedApps)
    {
        var currentKeys = new HashSet<string>(unpinnedApps.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);

        // 1. Remove closed apps
        var toRemove = new List<DockItemState>();
        lock (_items)
        {
            foreach (var item in _items)
            {
                if (!item.IsPinned && item.Tag is string key && !currentKeys.Contains(key))
                {
                    toRemove.Add(item);
                }
            }
        }

        bool changed = toRemove.Count > 0;
        foreach (var item in toRemove)
        {
            RunningAppsPanel.Children.Remove(item.Grid);
            lock (_items)
            {
                _items.Remove(item);
            }
        }

        // 2. Add or update running apps
        foreach (var app in unpinnedApps)
        {
            DockItemState? existing;
            lock (_items)
            {
                existing = _items.FirstOrDefault(it => !it.IsPinned && string.Equals(it.Tag as string, app.Key, StringComparison.OrdinalIgnoreCase));
            }

            if (existing != null)
            {
                existing.WindowHwnd = app.Hwnd;
                existing.ExePath = app.ExePath;
                existing.Title = app.Title;
                SetItemActiveState(existing, isRunning: true, isActive: app.IsActive);
            }
            else
            {
                var newItem = CreateDynamicDockItem(app);
                RunningAppsPanel.Children.Add(newItem.Grid);
                lock (_items)
                {
                    _items.Add(newItem);
                }
                SetItemActiveState(newItem, isRunning: true, isActive: app.IsActive);
                changed = true;
            }
        }

        // 3. Update divider visibility
        RunningAppsDivider.Visibility = (RunningAppsPanel.Children.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

        // 4. If dock items changed, re-center at bottom smoothly
        if (changed)
        {
            Dispatcher.BeginInvoke(RepositionAtBottom, DispatcherPriority.Loaded);
        }
    }

    private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        // 1. Check if user switched into a fullscreen game or video
        if (FullscreenDetector.IsForegroundWindowFullscreen(hwnd, IntPtr.Zero))
        {
            _isFullscreenAppActive = true;
            SetDockGlassPaused(true);
            return;
        }
        _isFullscreenAppActive = false;
        SetDockGlassPaused(_isDockSlidDown);

        // 2. Instant 0ms event-driven active state switch on UI thread
        Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return;

        string procName = "";
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            procName = proc.ProcessName.ToLowerInvariant();
        }
        catch { }

        bool anyActive = false;
        lock (_items)
        {
            foreach (var item in _items)
            {
                bool matches = false;
                if (item.IsPinned)
                {
                    if (!string.IsNullOrEmpty(procName))
                    {
                        foreach (var pn in item.ProcessNames)
                        {
                            if (string.Equals(pn, procName, StringComparison.OrdinalIgnoreCase))
                            {
                                matches = true;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    matches = (item.WindowHwnd == hwnd);
                }

                if (matches)
                {
                    SetItemActiveState(item, isRunning: true, isActive: true);
                    anyActive = true;
                }
                else if (item.IsActive)
                {
                    SetItemActiveState(item, isRunning: item.IsRunning, isActive: false);
                }
            }
        }

        // 3. If focused window is an unpinned app not yet in the dock, schedule fast scan
        if (!anyActive)
        {
            _fastCheckDebouncer.Debounce(CheckRunningApps);
        }
    }

    private void CheckRunningApps()
    {
        // Don't poll during fullscreen gaming/movies to save 100% CPU/GPU
        if (_isFullscreenAppActive) return;

        // Skip if already scanning
        if (Interlocked.CompareExchange(ref _isCheckingApps, 1, 0) != 0) return;

        Task.Run(() =>
        {
            try
            {
                IntPtr foregroundHwnd = Win32Interop.GetForegroundWindow();
                if (foregroundHwnd != IntPtr.Zero && FullscreenDetector.IsForegroundWindowFullscreen(foregroundHwnd, IntPtr.Zero))
                {
                    _isFullscreenAppActive = true;
                    return;
                }
                _isFullscreenAppActive = false;
                uint currentPid = (uint)Environment.ProcessId;

                bool runExplorer = false, actExplorer = false;
                bool runBrowser = false, actBrowser = false;
                bool runChat = false, actChat = false;
                bool runMusic = false, actMusic = false;
                bool runNotes = false, actNotes = false;
                bool runCalendar = false, actCalendar = false;
                bool runSettings = false, actSettings = false;

                if (YouTubeMusicPlayerService.Instance.HasActiveMedia)
                {
                    runMusic = true;
                }

                var unpinnedMap = new Dictionary<string, RunningAppInfo>(StringComparer.OrdinalIgnoreCase);

                Win32Interop.EnumWindows((hwnd, _) =>
                {
                    if (!Win32Interop.IsWindowVisible(hwnd)) return true;
                    if (FullscreenDetector.IsWindowCloaked(hwnd)) return true;

                    int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
                    if ((exStyle & Win32Interop.WS_EX_TOOLWINDOW) != 0 && (exStyle & Win32Interop.WS_EX_APPWINDOW) == 0)
                        return true;

                    IntPtr owner = GetWindow(hwnd, GW_OWNER);
                    if (owner != IntPtr.Zero && (exStyle & Win32Interop.WS_EX_APPWINDOW) == 0)
                        return true;

                    string cls = GetWindowClassName(hwnd);
                    if (ShellWindowClasses.Contains(cls) || FullscreenDetector.IsBlockedClass(hwnd))
                        return true;

                    Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0 || pid == currentPid) return true;

                    if (!Win32Interop.GetWindowRect(hwnd, out var r)) return true;
                    if (r.Right - r.Left <= 60 || r.Bottom - r.Top <= 60) return true;

                    var sbTitle = new StringBuilder(512);
                    GetWindowText(hwnd, sbTitle, 512);
                    string title = sbTitle.ToString().Trim();

                    string procName = "";
                    string exePath = "";
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        procName = proc.ProcessName.ToLowerInvariant();
                        exePath = GetProcessExecutablePath(pid);
                    }
                    catch
                    {
                        return true;
                    }

                    if (string.Equals(procName, "RuntimeBroker", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(title))
                    {
                        return true;
                    }

                    bool isFg = (hwnd == foregroundHwnd);

                    // 1. Check Explorer
                    if (string.Equals(procName, "explorer", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(cls, "CabinetWClass", StringComparison.OrdinalIgnoreCase))
                        {
                            runExplorer = true;
                            if (isFg) actExplorer = true;
                        }
                        return true;
                    }

                    // 2. Check Browser
                    if (procName is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "arc")
                    {
                        runBrowser = true;
                        if (isFg) actBrowser = true;
                        return true;
                    }

                    // 3. Check Chat
                    if (procName is "whatsapp" or "discord" or "telegram" or "teams" or "slack")
                    {
                        runChat = true;
                        if (isFg) actChat = true;
                        return true;
                    }

                    // 4. Check Music
                    if (procName is "spotify" or "music" or "itunes" or "wmplayer" or "vlc")
                    {
                        runMusic = true;
                        if (isFg) actMusic = true;
                        return true;
                    }

                    // 5. Check Notes
                    if (procName is "notepad" or "onenote")
                    {
                        runNotes = true;
                        if (isFg) actNotes = true;
                        return true;
                    }

                    // 6. Check Calendar
                    if (procName is "olk" or "outlook" or "time" or "calendar")
                    {
                        runCalendar = true;
                        if (isFg) actCalendar = true;
                        return true;
                    }

                    // 7. Check Settings
                    if (procName is "systemsettings")
                    {
                        runSettings = true;
                        if (isFg) actSettings = true;
                        return true;
                    }

                    // Unpinned Running App
                    string key = string.IsNullOrEmpty(exePath) ? procName : exePath.ToLowerInvariant();
                    string displayTitle = !string.IsNullOrEmpty(title) ? title : procName;

                    if (!unpinnedMap.TryGetValue(key, out var existingApp))
                    {
                        var icon = GetWindowIcon(hwnd, exePath);
                        unpinnedMap[key] = new RunningAppInfo
                        {
                            Key = key,
                            ProcessName = procName,
                            ExePath = exePath,
                            Title = displayTitle,
                            Hwnd = hwnd,
                            IsActive = isFg,
                            Icon = icon
                        };
                    }
                    else
                    {
                        if (isFg)
                        {
                            existingApp.IsActive = true;
                            existingApp.Hwnd = hwnd;
                            existingApp.Title = displayTitle;
                        }
                    }

                    return true;
                }, IntPtr.Zero);

                var unpinnedList = unpinnedMap.Values.Take(10).ToList();

                long nowTicks = Environment.TickCount64;
                if (nowTicks - _lastTrayCacheTicks >= 4000)
                {
                    _lastTrayCacheTicks = nowTicks;
                    try
                    {
                        _cachedTrayIcons = SystemTrayService.EnumerateLiveTrayIcons();
                        if (_cachedTrayIcons.Count != _lastLoggedTrayCount && RuntimeLog.IsEnabled(LogLevel.Info))
                        {
                            _lastLoggedTrayCount = _cachedTrayIcons.Count;
                            RuntimeLog.Info("TRAY", () => $"Live system tray icons cached: {_cachedTrayIcons.Count}");
                        }
                    }
                    catch { }
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (_items.Count >= 7)
                    {
                        SetItemActiveState(_items[0], runExplorer, actExplorer);
                        SetItemActiveState(_items[1], runBrowser, actBrowser);
                        SetItemActiveState(_items[2], runChat, actChat);
                        SetItemActiveState(_items[3], runMusic, actMusic);
                        SetItemActiveState(_items[4], runNotes, actNotes);
                        SetItemActiveState(_items[5], runCalendar, actCalendar);
                        SetItemActiveState(_items[6], runSettings, actSettings);
                    }

                    UpdateRunningAppsUI(unpinnedList);
                });
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("DOCK", $"CheckRunningApps error: {ex.Message}");
            }
            finally
            {
                _isCheckingApps = 0;
            }
        });
    }

    #region macOS Dock Frame-Synced Spring Magnification & Tooltip

    private void DockRootGrid_MouseMove(object sender, MouseEventArgs e)
    {
        var mousePos = e.GetPosition(IconsPanel);

        // Active vertical band for app icons (includes elevated icon height and dock pill)
        bool inVerticalBand = mousePos.Y >= -40.0 && mousePos.Y <= 65.0;
        bool inHorizontalBand = mousePos.X >= -10.0 && mousePos.X <= IconsPanel.ActualWidth + 10.0;

        DockItemState? activeItem = null;

        if (inVerticalBand && inHorizontalBand)
        {
            double closestDistance = double.MaxValue;
            lock (_items)
            {
                foreach (var item in _items)
                {
                    if (item.Grid.ActualWidth <= 0) continue;
                    Point gridOrigin = item.Grid.TranslatePoint(new Point(0, 0), IconsPanel);
                    double staticCenterX = gridOrigin.X - item.Trans.X + (item.Grid.ActualWidth / 2.0);

                    double distX = Math.Abs(staticCenterX - mousePos.X);
                    if (distX < closestDistance)
                    {
                        closestDistance = distX;
                        activeItem = item;
                    }
                }
            }

            if (closestDistance > 38.0)
            {
                activeItem = null;
            }
        }

        lock (_items)
        {
            foreach (var item in _items)
            {
                if (item == activeItem)
                {
                    item.ScaleSpring.Target = 1.25;
                    item.TransYSpring.Target = -12.0;
                    item.TransXSpring.Target = 0.0;
                }
                else
                {
                    item.ScaleSpring.Target = 1.0;
                    item.TransYSpring.Target = 0.0;
                    item.TransXSpring.Target = 0.0;
                }
            }
        }

        StartRenderLoop();

        // Tooltip badge target calculation
        if (activeItem != null)
        {
            Point rootPos = activeItem.Grid.TranslatePoint(new Point(activeItem.Grid.ActualWidth / 2.0, 0), DockRootGrid);
            double staticX = rootPos.X - activeItem.Trans.X;
            double staticY = rootPos.Y - activeItem.Trans.Y;

            TooltipText.Text = activeItem.Title;

            double badgeW = TooltipBadge.ActualWidth > 0 ? TooltipBadge.ActualWidth : 60;
            double badgeH = TooltipBadge.ActualHeight > 0 ? TooltipBadge.ActualHeight : 24;

            _tooltipXSpring.Target = Math.Max(12, staticX - (badgeW / 2.0));
            _tooltipYSpring.Target = Math.Max(4, staticY - badgeH - 12.0);

            var fadeAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            TooltipBadge.BeginAnimation(OpacityProperty, fadeAnim);
        }
        else
        {
            HideTooltip();
        }
    }

    private void DockRootGrid_MouseLeave(object sender, MouseEventArgs e)
    {
        lock (_items)
        {
            foreach (var item in _items)
            {
                item.ScaleSpring.Target = 1.0;
                item.TransXSpring.Target = 0.0;
                item.TransYSpring.Target = 0.0;
            }
        }
        StartRenderLoop();
        HideTooltip();
    }

    private void StartRenderLoop()
    {
        if (_isRenderLoopActive) return;
        _isRenderLoopActive = true;
        _lastRenderTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRenderFrame;
    }

    private void StopRenderLoop()
    {
        if (!_isRenderLoopActive) return;
        _isRenderLoopActive = false;
        _lastRenderTimestamp = 0;
        CompositionTarget.Rendering -= OnRenderFrame;
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = _lastRenderTimestamp == 0 ? 0.016 : (double)(now - _lastRenderTimestamp) / Stopwatch.Frequency;
        _lastRenderTimestamp = now;
        dt = Math.Clamp(dt, 0.001, 0.05);

        bool stillMoving = false;
        List<DockItemState> snapshot;
        lock (_items)
        {
            snapshot = _items.ToList();
        }

        foreach (var item in snapshot)
        {
            bool sMove = item.ScaleSpring.Update(dt, speed: 28.0);
            bool xMove = item.TransXSpring.Update(dt, speed: 28.0);
            bool yMove = item.TransYSpring.Update(dt, speed: 28.0);

            item.Scale.ScaleX = item.ScaleSpring.Current;
            item.Scale.ScaleY = item.ScaleSpring.Current;
            item.Trans.X = item.TransXSpring.Current;
            item.Trans.Y = item.TransYSpring.Current;

            stillMoving |= sMove || xMove || yMove;
        }

        bool txMove = _tooltipXSpring.Update(dt, speed: 32.0);
        bool tyMove = _tooltipYSpring.Update(dt, speed: 32.0);
        TooltipTranslate.X = _tooltipXSpring.Current;
        TooltipTranslate.Y = _tooltipYSpring.Current;
        stillMoving |= txMove || tyMove;

        if (!stillMoving)
        {
            StopRenderLoop();
        }
    }

    private void HideTooltip()
    {
        var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() };
        TooltipBadge.BeginAnimation(OpacityProperty, fadeOut);
    }

    #endregion

    #region Intelligent Auto-Hide & Precision Trigger Zone

    public Rect? GetDockScreenBounds()
    {
        if (!IsLoaded || Visibility != Visibility.Visible)
            return null;
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return null;
            double scale = MonitorSelection.GetScale(screen);

            double dockWidthDip = ActualWidth > 0 ? ActualWidth : 600;
            double pillHeightDip = 64.0;
            double dockWidthPx = dockWidthDip * scale;
            double pillHeightPx = pillHeightDip * scale;

            double screenLeft = screen.Bounds.Left;
            double screenWidth = screen.Bounds.Width;
            double screenBottom = screen.Bounds.Bottom;

            double dockLeftPx = screenLeft + (screenWidth - dockWidthPx) / 2.0;
            double pillBottomPx = screenBottom - (10.0 * scale);
            double pillTopPx = pillBottomPx - pillHeightPx;

            // Stable resting bounds (immune to slide-down animations)
            return new Rect(dockLeftPx, pillTopPx, dockWidthPx, pillHeightPx);
        }
        catch
        {
            return null;
        }
    }

    private void OnDockOverlapChanged(bool isOverlapped)
    {
        _isDockOverlapped = isOverlapped;
        UpdateDockSlideState();
    }

    private void CursorPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_isCleanedUp || !IsLoaded || Visibility != Visibility.Visible || (Application.Current is App { IsShutdownRequested: true }) || Dispatcher.HasShutdownStarted)
        {
            _cursorPollTimer.Stop();
            return;
        }

        try
        {
            var settings = _settingsService.Load();
        if (settings.DockAutoHideMode == "Never")
        {
            if (_isDockSlidDown) SlideDock(false);
            return;
        }

        if (Win32Interop.GetCursorPos(out var mousePt))
        {
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(mousePt.X, mousePt.Y))
                         ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;
            double scale = MonitorSelection.GetScale(screen);

            double dockWidthDip = ActualWidth > 0 ? ActualWidth : 580;
            double dockHeightDip = Height > 0 ? Height : 136;
            double dockWidthPx = dockWidthDip * scale;
            double dockHeightPx = dockHeightDip * scale;

            double screenLeft = screen.Bounds.Left;
            double screenWidth = screen.Bounds.Width;
            double screenBottom = screen.Bounds.Bottom;

            double dockLeftPx = screenLeft + (screenWidth - dockWidthPx) / 2.0;
            double dockRightPx = dockLeftPx + dockWidthPx;
            double dockTopPx = screenBottom - dockHeightPx;

            // Trigger Zone at the very bottom edge of the screen (in physical pixels)
            double extraPx = settings.DockRevealExtraWidthPx * scale;
            double revealHPx = Math.Max(1, settings.DockRevealHeightPx * scale);
            var triggerZone = new Rect(dockLeftPx - extraPx, screenBottom - revealHPx, dockWidthPx + (extraPx * 2), revealHPx);

            bool inTriggerZone = mousePt.X >= triggerZone.Left && mousePt.X <= triggerZone.Right &&
                                 mousePt.Y >= triggerZone.Top && mousePt.Y <= triggerZone.Bottom;

            // Body hover check (only valid when dock is revealed/visible)
            bool isOverRevealedDock = false;
            if (!_isDockSlidDown || _isRevealedByMouse)
            {
                isOverRevealedDock = mousePt.X >= dockLeftPx - 20 && mousePt.X <= dockRightPx + 20 &&
                                     mousePt.Y >= dockTopPx - 20 && mousePt.Y <= screenBottom + 5;
            }

            if (inTriggerZone || isOverRevealedDock)
            {
                if (!_isRevealedByMouse)
                {
                    long now = Environment.TickCount64;
                    if (_revealStartTick == 0)
                    {
                        _revealStartTick = now;
                    }
                    else if (now - _revealStartTick >= settings.DockRevealDelayMs)
                    {
                        _isRevealedByMouse = true;
                        _revealStartTick = 0;
                        _hideStartTick = 0;
                        UpdateDockSlideState();
                        CheckRunningApps();
                    }
                }
            }
            else
            {
                _revealStartTick = 0;
                if (_isRevealedByMouse)
                {
                    long now = Environment.TickCount64;
                    if (_hideStartTick == 0)
                    {
                        _hideStartTick = now;
                    }
                    else if (now - _hideStartTick >= settings.DockHideDelayMs)
                    {
                        _isRevealedByMouse = false;
                        _hideStartTick = 0;
                        UpdateDockSlideState();
                    }
                }
            }
        }
        }
        catch (ObjectDisposedException)
        {
            _cursorPollTimer.Stop();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("DOCK", $"CursorPollTimer exception: {ex.Message}");
        }
    }

    private void UpdateDockSlideState()
    {
        if (_isCleanedUp || (Application.Current is App { IsShutdownRequested: true }) || Dispatcher.HasShutdownStarted)
            return;

        try
        {
            var settings = _settingsService.Load();
            bool shouldHide = false;
            if (_isContextMenuOpen || _isTrayMenuHoldActive || (_systemTrayPopup?.IsVisible == true) || (_appDrawer?.IsVisible == true))
            {
                shouldHide = false;
            }
            else if (settings.DockAutoHideMode == "Always")
            {
                shouldHide = !_isRevealedByMouse;
            }
            else if (settings.DockAutoHideMode == "Smart")
            {
                shouldHide = _isDockOverlapped && !_isRevealedByMouse;
            }
            else // "Never"
            {
                shouldHide = false;
            }

            if (shouldHide != _isDockSlidDown)
            {
                _isDockSlidDown = shouldHide;
                SlideDock(shouldHide);
            }
        }
        catch (ObjectDisposedException)
        {
            // App is shutting down, ignore
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("DOCK", $"UpdateDockSlideState exception: {ex.Message}");
        }
    }

    private void SlideDock(bool hide)
    {
        // Adaptive Polling: 5 seconds when hidden under the edge, 1.5 seconds when active
        _procCheckTimer.Interval = hide ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(1500);
        SetDockGlassPaused(hide || _isFullscreenAppActive);

        var animDuration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        double targetY = hide ? 95.0 : 0.0;
        double targetOpacity = hide ? 0.0 : 1.0;

        var transAnim = new DoubleAnimation(targetY, animDuration) { EasingFunction = ease };
        var opacityAnim = new DoubleAnimation(targetOpacity, animDuration) { EasingFunction = ease };

        DockRootTranslate.BeginAnimation(TranslateTransform.YProperty, transAnim);
        DockRootGrid.BeginAnimation(OpacityProperty, opacityAnim);
    }

    #endregion

    #region Click Actions & Bounce

    private static void AnimateBounce(UIElement element)
    {
        if (element.RenderTransform is not TransformGroup group) return;
        var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
        if (scale == null) return;

        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Launcher_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(LauncherContainer);
        ToggleAppDrawer();
    }

    private void ToggleAppDrawer()
    {
        _appDrawer ??= new AppDrawerWindow();

        if (_appDrawer.IsVisible)
        {
            _appDrawer.CloseWithAnimation();
        }
        else
        {
            try
            {
                // Align bottom-right corner of AppDrawer directly above LauncherContainer in DIPs
                Point rel = LauncherContainer.TranslatePoint(new Point(0, 0), this);
                double launcherLeftDip = this.Left + rel.X;
                double launcherRightDip = launcherLeftDip + LauncherContainer.ActualWidth;
                double launcherTopDip = this.Top + rel.Y;

                // AppDrawerWindow has Margin="16" around GlassBorder.
                // Inner border right is Left + Width - 16.
                // Align inner border right with launcherRightDip: targetX = launcherRightDip - Width + 16
                // Inner border bottom is Top + Height - 16.
                // Align inner border bottom strictly 12 DIPs above launcherTopDip (above the head of the icon):
                // targetY + Height - 16 = launcherTopDip - 12 => targetY = launcherTopDip - Height + 4
                double targetX = launcherRightDip - _appDrawer.Width + 16;
                double targetY = launcherTopDip - _appDrawer.Height + 4;

                _appDrawer.ShowAt(targetX, targetY);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("DOCK", $"Failed to calculate AppDrawer position: {ex.Message}");
                _appDrawer.ShowAt(60, SystemParameters.PrimaryScreenHeight - 480);
            }
        }
    }

    private void LaunchOrActivateApp(FrameworkElement anchor, string[] processNames, Action launchAction)
    {
        // 1. Check if the app is already open with a visible window
        IntPtr existingHwnd = FindExistingAppWindow(processNames);
        if (existingHwnd != IntPtr.Zero)
        {
            IntPtr fg = Win32Interop.GetForegroundWindow();
            if (fg == existingHwnd)
            {
                Win32Interop.ShowWindow(existingHwnd, SW_MINIMIZE);
            }
            else
            {
                if (Win32Interop.IsIconic(existingHwnd))
                {
                    Win32Interop.ShowWindow(existingHwnd, Win32Interop.SW_RESTORE);
                }
                Win32Interop.SetForegroundWindow(existingHwnd);
            }
            CheckRunningApps();
            return;
        }

        // 2. Newly launching the app:
        var beforeWindows = new HashSet<IntPtr>(GetVisibleWindows());

        try
        {
            launchAction();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("DOCK", $"Failed to launch app: {ex.Message}");
            return;
        }

        Point anchorPt;
        try
        {
            anchorPt = anchor.PointToScreen(new Point(0, 0));
        }
        catch
        {
            return;
        }

        double dpiScale = 1.0;
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                dpiScale = source.CompositionTarget.TransformToDevice.M11;
            }
        }
        catch { }

        double anchorRightPx = anchorPt.X + (anchor.ActualWidth * dpiScale);
        double anchorTopPx = anchorPt.Y;

        Task.Run(async () =>
        {
            for (int i = 0; i < 35; i++)
            {
                await Task.Delay(80);
                var currentWindows = GetVisibleWindows();
                foreach (var hwnd in currentWindows)
                {
                    if (!beforeWindows.Contains(hwnd) && IsAppWindowForProcesses(hwnd, processNames))
                    {
                        // Ensure newly opened window is NOT fullscreen / maximized
                        Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);

                        double screenWidthPx = SystemParameters.PrimaryScreenWidth * dpiScale;
                        double screenHeightPx = SystemParameters.PrimaryScreenHeight * dpiScale;

                        if (Win32Interop.GetWindowRect(hwnd, out var rect))
                        {
                            int w = rect.Right - rect.Left;
                            int h = rect.Bottom - rect.Top;

                            if (w <= 120 || w >= screenWidthPx) w = (int)Math.Min(1020 * dpiScale, screenWidthPx * 0.72);
                            if (h <= 120 || h >= screenHeightPx) h = (int)Math.Min(680 * dpiScale, screenHeightPx * 0.72);

                            int targetLeft = Math.Clamp((int)(anchorRightPx - w), 16, (int)(screenWidthPx - w - 16));
                            int targetTop = Math.Clamp((int)(anchorTopPx - h - (12 * dpiScale)), 16, (int)(screenHeightPx - h - 16));

                            Win32Interop.SetWindowPos(hwnd, IntPtr.Zero, targetLeft, targetTop, w, h, Win32Interop.SWP_NOZORDER | Win32Interop.SWP_SHOWWINDOW);
                            Win32Interop.SetForegroundWindow(hwnd);
                        }
                        return;
                    }
                }
            }
        });
    }

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "DV2ControlHost",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Composition.DesktopWindowTarget",
        "EdgeUiInputTopWndClass"
    };

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32Interop.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static List<IntPtr> GetVisibleWindows()
    {
        var list = new List<IntPtr>();
        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (Win32Interop.IsWindowVisible(hwnd))
            {
                list.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static IntPtr FindExistingAppWindow(string[] processNames)
    {
        if (processNames == null || processNames.Length == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (Win32Interop.IsWindowVisible(hwnd))
            {
                string cls = GetWindowClassName(hwnd);
                if (ShellWindowClasses.Contains(cls)) return true; // skip desktop and shell windows

                // For Windows Explorer, only CabinetWClass is an actual File Explorer window
                bool isExplorerSearch = processNames.Any(p => string.Equals(p, "explorer", StringComparison.OrdinalIgnoreCase));
                if (isExplorerSearch)
                {
                    if (string.Equals(cls, "CabinetWClass", StringComparison.OrdinalIgnoreCase))
                    {
                        found = hwnd;
                        return false;
                    }
                    return true;
                }

                if (IsAppWindowForProcesses(hwnd, processNames))
                {
                    if (Win32Interop.GetWindowRect(hwnd, out var r))
                    {
                        if (r.Right - r.Left > 100 && r.Bottom - r.Top > 100)
                        {
                            found = hwnd;
                            return false;
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsAppWindowForProcesses(IntPtr hwnd, string[] processNames)
    {
        if (processNames == null || processNames.Length == 0) return true;
        Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            if (string.Equals(name, "RuntimeBroker", StringComparison.OrdinalIgnoreCase)) return false;

            string cls = GetWindowClassName(hwnd);
            if (ShellWindowClasses.Contains(cls)) return false;

            // Explorer process is only an app window if class is CabinetWClass
            if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(cls, "CabinetWClass", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var p in processNames)
            {
                if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private void Launcher_MouseEnter(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(1.15, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() };
        LauncherScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        LauncherScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Launcher_MouseLeave(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150));
        LauncherScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        LauncherScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void AppExplorer_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemExplorer);
        LaunchOrActivateApp(ItemExplorer, new[] { "explorer" }, () =>
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        });
    }

    private void AppBrowser_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemBrowser);
        LaunchOrActivateApp(ItemBrowser, new[] { "chrome", "msedge", "firefox", "brave" }, () =>
        {
            Process.Start(new ProcessStartInfo("https://www.google.com") { UseShellExecute = true });
        });
    }

    private void AppChat_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemChat);
        LaunchOrActivateApp(ItemChat, new[] { "whatsapp", "discord", "telegram" }, () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("whatsapp:") { UseShellExecute = true });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("discord:") { UseShellExecute = true }); }
                catch { Process.Start(new ProcessStartInfo("https://web.whatsapp.com") { UseShellExecute = true }); }
            }
        });
    }

    private void AppMusic_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemMusic);
        if (YouTubeMusicPlayerService.Instance.IsConfiguredAsDefault)
        {
            YouTubeMusicPlayerService.Instance.OpenWindow(activate: true);
            return;
        }

        LaunchOrActivateApp(ItemMusic, new[] { "spotify", "music", "wmplayer" }, () =>
        {
            YouTubeMusicPlayerService.Instance.OpenWindow(activate: true);
        });
    }

    private void AppNotes_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemNotes);
        LaunchOrActivateApp(ItemNotes, new[] { "notepad", "onenote" }, () =>
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        });
    }

    private void AppCalendar_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemCalendar);
        LaunchOrActivateApp(ItemCalendar, new[] { "Time", "olk", "outlook" }, () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-clock:") { UseShellExecute = true });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("outlookcal:") { UseShellExecute = true }); } catch { }
            }
        });
    }

    private void AppSettings_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemSettings);
        if (Application.Current is App && App.Services != null)
        {
            var mainWindow = App.Services.GetService(typeof(MainWindow)) as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.OpenAppSettings();
                return;
            }
        }
        LaunchOrActivateApp(ItemSettings, new[] { "systemsettings" }, () =>
        {
            Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
        });
    }

    private void Clock_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-actioncenter:") { UseShellExecute = true });
        }
        catch { }
    }

    #endregion

    #region System Tray Flyout & Taskbar-Style Context Menus

    private void SystemTrayButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleSystemTrayPopup();
    }

    private void ToggleSystemTrayPopup()
    {
        if (_systemTrayPopup == null)
        {
            _systemTrayPopup = new SystemTrayPopupWindow();
            _systemTrayPopup.TrayInteractionStateChanged += active =>
            {
                _isTrayMenuHoldActive = active;
                UpdateDockSlideState();
            };
            _systemTrayPopup.PopupClosed += () =>
            {
                AnimateTrayChevron(0.0);
                UpdateDockSlideState();
            };
        }

        if (_systemTrayPopup.IsVisible)
        {
            _systemTrayPopup.CloseWithAnimation();
            return;
        }

        try
        {
            Point btnRel = SystemTrayButton.TranslatePoint(new Point(SystemTrayButton.ActualWidth * 0.5, 0), this);
            Point pillRel = DockPill.TranslatePoint(new Point(0, 0), this);
            double anchorCenterXDip = Left + btnRel.X;
            double dockPillTopDip = Top + pillRel.Y;

            int? dockTopPx = null;
            try
            {
                Point screenPillTop = DockPill.PointToScreen(new Point(0, 0));
                dockTopPx = (int)Math.Round(screenPillTop.Y);
            }
            catch { }

            AnimateTrayChevron(180.0);
            _systemTrayPopup.ShowAt(anchorCenterXDip, dockPillTopDip, dockTopPx, _cachedTrayIcons);
            UpdateDockSlideState();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("DOCK", $"Failed to position SystemTrayPopup: {ex.Message}");
        }
    }

    private void AnimateTrayChevron(double targetAngle)
    {
        if (SystemTrayChevronRotate == null) return;
        var anim = new DoubleAnimation(targetAngle, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SystemTrayChevronRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void PinnedApp_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not Grid grid) return;

        DockItemState? item;
        lock (_items)
        {
            item = _items.FirstOrDefault(i => ReferenceEquals(i.Grid, grid));
        }
        if (item != null)
            ShowAppContextMenu(item);
    }

    private void ShowAppContextMenu(DockItemState item)
    {
        var menu = CreateDockContextMenu();

        if (ReferenceEquals(item.Grid, ItemSettings))
        {
            menu.Items.Add(CreateDockMenuItem("Pengaturan Pinn-Notch", "\uE713", () => AppSettings_Click(ItemSettings, null!)));
            menu.Items.Add(CreateDockMenuItem("Pengaturan Windows", "\uE770", () =>
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); } catch { }
            }));
            menu.Items.Add(new Separator { Style = (Style)FindResource("DockGlassSeparator") });
            menu.Items.Add(CreateDockMenuItem("Keluar dari Pinn-Notch (Exit)", "\uE7E8", () => _ = App.RequestShutdownAsync(), isDestructive: true));

            OpenDockContextMenu(menu, item.Grid);
            return;
        }

        menu.Items.Add(CreateDockMenuItem($"Buka / Fokus {item.Title}", "\uE8A7", () => ActivateDockItem(item)));

        if (item.IsPinned)
        {
            if (ReferenceEquals(item.Grid, ItemExplorer))
            {
                menu.Items.Add(CreateDockMenuItem("Jendela Explorer Baru", "\uE8B7", () =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
                }));
            }
            else if (ReferenceEquals(item.Grid, ItemBrowser))
            {
                menu.Items.Add(CreateDockMenuItem("Tab Browser Baru", "\uE710", () =>
                {
                    try { Process.Start(new ProcessStartInfo("https://www.google.com") { UseShellExecute = true }); } catch { }
                }));
            }
            else if (ReferenceEquals(item.Grid, ItemNotes))
            {
                menu.Items.Add(CreateDockMenuItem("Catatan Baru (Notepad)", "\uE70F", () =>
                {
                    try { Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true }); } catch { }
                }));
            }
        }

        IntPtr targetHwnd = !item.IsPinned ? item.WindowHwnd : FindExistingAppWindow(item.ProcessNames);
        if (targetHwnd != IntPtr.Zero && IsWindow(targetHwnd))
        {
            bool isMinimized = Win32Interop.IsIconic(targetHwnd);
            menu.Items.Add(CreateDockMenuItem(isMinimized ? "Pulihkan Jendela (Restore)" : "Perkecil Jendela (Minimize)",
                isMinimized ? "\uE923" : "\uE921", () =>
                {
                    if (Win32Interop.IsIconic(targetHwnd))
                    {
                        Win32Interop.ShowWindow(targetHwnd, Win32Interop.SW_RESTORE);
                        Win32Interop.SetForegroundWindow(targetHwnd);
                    }
                    else
                    {
                        Win32Interop.ShowWindow(targetHwnd, SW_MINIMIZE);
                    }
                    CheckRunningApps();
                }));
        }

        var trayMatch = SystemTrayService.FindMatchingTrayIcon(_cachedTrayIcons, item.ExePath, item.ProcessNames);
        if (trayMatch != null)
        {
            menu.Items.Add(CreateDockMenuItem("Menu System Tray", "\uE712", async () =>
            {
                _isTrayMenuHoldActive = true;
                UpdateDockSlideState();
                try
                {
                    int ax = 0, ay = 0;
                    int? dockTopPx = null;
                    try
                    {
                        Point pt = item.Grid.PointToScreen(new Point(item.Grid.ActualWidth * 0.5, 0));
                        ax = (int)Math.Round(pt.X);
                        ay = (int)Math.Round(pt.Y);
                        dockTopPx = (int)Math.Round(DockPill.PointToScreen(new Point(0, 0)).Y);
                    }
                    catch { }
                    await SystemTrayService.InteractWithTrayIconAsync(trayMatch, contextMenu: true, ax, ay, dockTopPx);
                }
                finally
                {
                    _isTrayMenuHoldActive = false;
                    UpdateDockSlideState();
                }
            }));
        }

        string exeToOpen = !string.IsNullOrEmpty(item.ExePath) ? item.ExePath : (trayMatch?.ExecutablePath ?? "");
        if (!string.IsNullOrEmpty(exeToOpen) && System.IO.File.Exists(exeToOpen))
        {
            menu.Items.Add(CreateDockMenuItem("Buka Lokasi File", "\uED25", () => SystemTrayService.OpenFileLocation(exeToOpen)));
        }

        bool canExit = item.IsRunning || (targetHwnd != IntPtr.Zero) || (trayMatch != null && !trayMatch.IsOwnProcess);
        if (canExit)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("DockGlassSeparator") });
            menu.Items.Add(CreateDockMenuItem($"Tutup {item.Title} (Exit)", "\uE8BB", () =>
            {
                SystemTrayService.CloseApplicationGracefully(exeToOpen, item.ProcessNames, targetHwnd);
                _fastCheckDebouncer.Debounce(CheckRunningApps);
            }));
            menu.Items.Add(CreateDockMenuItem("Paksa Tutup (End Task)", "\uE711", () =>
            {
                SystemTrayService.ForceKillApplication(exeToOpen, item.ProcessNames, targetHwnd);
                _fastCheckDebouncer.Debounce(CheckRunningApps);
            }, isDestructive: true));
        }

        OpenDockContextMenu(menu, item.Grid);
    }

    private void ActivateDockItem(DockItemState item)
    {
        if (!item.IsPinned)
        {
            DynamicApp_Click(item);
            return;
        }
        if (ReferenceEquals(item.Grid, ItemExplorer)) AppExplorer_Click(ItemExplorer, null!);
        else if (ReferenceEquals(item.Grid, ItemBrowser)) AppBrowser_Click(ItemBrowser, null!);
        else if (ReferenceEquals(item.Grid, ItemChat)) AppChat_Click(ItemChat, null!);
        else if (ReferenceEquals(item.Grid, ItemMusic)) AppMusic_Click(ItemMusic, null!);
        else if (ReferenceEquals(item.Grid, ItemNotes)) AppNotes_Click(ItemNotes, null!);
        else if (ReferenceEquals(item.Grid, ItemCalendar)) AppCalendar_Click(ItemCalendar, null!);
        else if (ReferenceEquals(item.Grid, ItemSettings)) AppSettings_Click(ItemSettings, null!);
    }

    private void DockBackground_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;

        var menu = CreateDockContextMenu();
        menu.Items.Add(CreateDockMenuItem("Buka System Tray", "\uE70E", ToggleSystemTrayPopup));
        menu.Items.Add(CreateDockMenuItem("Task Manager", "\uE9D9", () =>
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
        }));
        menu.Items.Add(new Separator { Style = (Style)FindResource("DockGlassSeparator") });
        menu.Items.Add(CreateDockMenuItem("Pengaturan Pinn-Notch", "\uE713", () => AppSettings_Click(ItemSettings, null!)));
        menu.Items.Add(CreateDockMenuItem("Pengaturan Taskbar Windows", "\uE7F4", () =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); } catch { }
        }));
        menu.Items.Add(new Separator { Style = (Style)FindResource("DockGlassSeparator") });
        menu.Items.Add(CreateDockMenuItem("Keluar dari Pinn-Notch (Exit)", "\uE7E8", () => _ = App.RequestShutdownAsync(), isDestructive: true));

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _isContextMenuOpen = true;
        UpdateDockSlideState();
        menu.Closed += (_, _) =>
        {
            _isContextMenuOpen = false;
            UpdateDockSlideState();
        };
        menu.IsOpen = true;
    }

    private ContextMenu CreateDockContextMenu() => new()
    {
        Style = (Style)FindResource("DockGlassContextMenu")
    };

    private MenuItem CreateDockMenuItem(string header, string? glyph, Action onClick, bool isDestructive = false)
    {
        var mi = new MenuItem
        {
            Header = header,
            Tag = glyph,
            Style = (Style)FindResource("DockGlassMenuItem")
        };
        if (isDestructive)
            mi.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void OpenDockContextMenu(ContextMenu menu, FrameworkElement placementTarget)
    {
        menu.PlacementTarget = placementTarget;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        _isContextMenuOpen = true;
        UpdateDockSlideState();
        menu.Closed += (_, _) =>
        {
            _isContextMenuOpen = false;
            UpdateDockSlideState();
        };
        menu.IsOpen = true;
    }

    #endregion

    private bool _isCleanedUp;

    public void Cleanup()
    {
        if (_isCleanedUp) return;
        _isCleanedUp = true;

        try { DisposeDockLiquidGlass(); } catch { }
        try { _cursorPollTimer.Stop(); } catch { }
        try { _clockTimer.Stop(); } catch { }
        try { _procCheckTimer.Stop(); } catch { }
        try { StopRenderLoop(); } catch { }
        try { _fastCheckDebouncer.Dispose(); } catch { }
        try
        {
            if (_winEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
        }
        catch { }
        try { _overlapMonitor.DockOverlapChanged -= OnDockOverlapChanged; } catch { }
        try { _systemTrayPopup?.Close(); _systemTrayPopup = null; } catch { }
        try { _appDrawer?.Close(); _appDrawer = null; } catch { }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.System && (e.SystemKey == Key.F4 || e.Key == Key.F4))
        {
            e.Handled = true;
            ShellHelper.ShowWindowsShutdownDialog();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && (Application.Current is App { IsShutdownRequested: false }))
        {
            e.Cancel = true;
            ShellHelper.ShowWindowsShutdownDialog();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Cleanup();
        base.OnClosed(e);
    }
}
