using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Windows;

public partial class DockWindow : Window
{
    private readonly ISettingsService _settingsService;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _procCheckTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly (Grid Grid, ScaleTransform Scale, TranslateTransform Trans, Ellipse Dot, string Title)[] _items;
    private bool _isDockSlidDown;
    private bool _isRevealedByMouse;
    private long _mouseLeaveTime;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public DockWindow(ISettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;

        _items = new[]
        {
            (ItemExplorer, ScaleExplorer, TransExplorer, DotExplorer, "File Explorer"),
            (ItemBrowser, ScaleBrowser, TransBrowser, DotBrowser, "Browser"),
            (ItemChat, ScaleChat, TransChat, DotChat, "Pesan & Chat"),
            (ItemMusic, ScaleMusic, TransMusic, DotMusic, "Musik"),
            (ItemNotes, ScaleNotes, TransNotes, DotNotes, "Catatan"),
            (ItemCalendar, ScaleCalendar, TransCalendar, DotCalendar, "Kalender"),
            (ItemSettings, ScaleSettings, TransSettings, DotSettings, "Pengaturan")
        };

        // Live Clock Timer
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClockAndCalendar();
        _clockTimer.Start();
        UpdateClockAndCalendar();

        // Process running check timer
        _procCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _procCheckTimer.Tick += (_, _) => CheckRunningApps();
        _procCheckTimer.Start();
        CheckRunningApps();

        // Intelligent Auto-Hide & Collision Detection Timer (macOS style window dodging)
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _autoHideTimer.Tick += AutoHideTimer_Tick;
        _autoHideTimer.Start();

        Loaded += DockWindow_Loaded;
        SizeChanged += DockWindow_SizeChanged;
    }

    private void DockWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RepositionAtBottom();
        EvaluateDockAutoHide();
    }

    private void DockWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RepositionAtBottom();
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

    private void CheckRunningApps()
    {
        try
        {
            var procs = Process.GetProcesses().Select(p => p.ProcessName.ToLowerInvariant()).ToHashSet();

            DotExplorer.Opacity = procs.Contains("explorer") ? 1.0 : 0.0;
            DotBrowser.Opacity = (procs.Contains("chrome") || procs.Contains("msedge") || procs.Contains("firefox") || procs.Contains("brave")) ? 1.0 : 0.0;
            DotChat.Opacity = (procs.Contains("whatsapp") || procs.Contains("discord") || procs.Contains("telegram") || procs.Contains("teams")) ? 1.0 : 0.0;
            DotMusic.Opacity = (procs.Contains("spotify") || procs.Contains("music") || procs.Contains("itunes")) ? 1.0 : 0.0;
            DotNotes.Opacity = (procs.Contains("notepad") || procs.Contains("onenote")) ? 1.0 : 0.0;
            DotCalendar.Opacity = procs.Contains("olk") || procs.Contains("outlook") ? 1.0 : 0.0;
            DotSettings.Opacity = procs.Contains("systemsettings") ? 1.0 : 0.0;
        }
        catch
        {
            // Best effort process check
        }
    }

    #region macOS Dock Magnification & Tooltip

    private void IconsPanel_MouseMove(object sender, MouseEventArgs e)
    {
        var mousePos = e.GetPosition(IconsPanel);
        const double range = 100.0;
        const double maxScale = 1.30;
        const double maxLift = 14.0;
        const double maxPush = 8.0;

        (Grid Grid, ScaleTransform Scale, TranslateTransform Trans, Ellipse Dot, string Title)? closestItem = null;
        double minDistance = double.MaxValue;

        foreach (var item in _items)
        {
            var itemPos = item.Grid.TransformToAncestor(IconsPanel).Transform(new Point(item.Grid.ActualWidth / 2, item.Grid.ActualHeight / 2));
            double distX = itemPos.X - mousePos.X;
            double absDist = Math.Abs(distX);

            if (absDist < minDistance)
            {
                minDistance = absDist;
                closestItem = item;
            }

            if (absDist < range)
            {
                double factor = Math.Cos((absDist / range) * (Math.PI / 2)); // 1.0 down to 0.0
                double targetScale = 1.0 + factor * (maxScale - 1.0);
                double targetY = -factor * maxLift;

                // Push adjacent icons outward to prevent any overlapping
                double pushFactor = Math.Sin((absDist / range) * Math.PI);
                double targetX = (distX >= 0 ? 1.0 : -1.0) * pushFactor * maxPush;

                item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                item.Trans.BeginAnimation(TranslateTransform.XProperty, null);
                item.Trans.BeginAnimation(TranslateTransform.YProperty, null);

                item.Scale.ScaleX = targetScale;
                item.Scale.ScaleY = targetScale;
                item.Trans.X = targetX;
                item.Trans.Y = targetY;
            }
            else
            {
                item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                item.Trans.BeginAnimation(TranslateTransform.XProperty, null);
                item.Trans.BeginAnimation(TranslateTransform.YProperty, null);

                item.Scale.ScaleX = 1.0;
                item.Scale.ScaleY = 1.0;
                item.Trans.X = 0.0;
                item.Trans.Y = 0.0;
            }
        }

        // Tooltip badge over closest icon
        if (closestItem != null && minDistance < 36.0)
        {
            var rootPos = closestItem.Value.Grid.TransformToAncestor(DockRootGrid).Transform(new Point(closestItem.Value.Grid.ActualWidth / 2, 0));
            TooltipText.Text = closestItem.Value.Title;
            TooltipBadge.UpdateLayout();

            double badgeW = TooltipBadge.ActualWidth > 0 ? TooltipBadge.ActualWidth : 60;
            double badgeH = TooltipBadge.ActualHeight > 0 ? TooltipBadge.ActualHeight : 24;

            double badgeX = rootPos.X - (badgeW / 2);
            TooltipTranslate.X = Math.Max(12, badgeX);

            double badgeY = Math.Max(6, rootPos.Y - badgeH - 10);
            TooltipTranslate.Y = badgeY;

            var fadeAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            TooltipBadge.BeginAnimation(OpacityProperty, fadeAnim);
        }
        else
        {
            HideTooltip();
        }
    }

    private void IconsPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        ResetAllIcons();
        HideTooltip();
    }

    private void ResetAllIcons()
    {
        var animDuration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        foreach (var item in _items)
        {
            var scaleAnimX = new DoubleAnimation(1.0, animDuration) { EasingFunction = ease };
            var scaleAnimY = new DoubleAnimation(1.0, animDuration) { EasingFunction = ease };
            var transAnimX = new DoubleAnimation(0.0, animDuration) { EasingFunction = ease };
            var transAnimY = new DoubleAnimation(0.0, animDuration) { EasingFunction = ease };

            item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimX);
            item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimY);
            item.Trans.BeginAnimation(TranslateTransform.XProperty, transAnimX);
            item.Trans.BeginAnimation(TranslateTransform.YProperty, transAnimY);
        }
    }

    private void HideTooltip()
    {
        var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() };
        TooltipBadge.BeginAnimation(OpacityProperty, fadeOut);
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
        // Toggle Windows Start menu
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    }

    private void Launcher_MouseEnter(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(1.1, TimeSpan.FromMilliseconds(120));
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
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
    }

    private void AppBrowser_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemBrowser);
        try { Process.Start(new ProcessStartInfo("https://www.google.com") { UseShellExecute = true }); } catch { }
    }

    private void AppChat_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemChat);
        // Try WhatsApp protocol, fallback to Discord, fallback to web
        try
        {
            Process.Start(new ProcessStartInfo("whatsapp:") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("discord:") { UseShellExecute = true }); }
            catch { Process.Start(new ProcessStartInfo("https://web.whatsapp.com") { UseShellExecute = true }); }
        }
    }

    private void AppMusic_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemMusic);
        try
        {
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("mswindowsmusic:") { UseShellExecute = true }); } catch { }
        }
    }

    private void AppNotes_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemNotes);
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        }
        catch { }
    }

    private void AppCalendar_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemCalendar);
        try
        {
            Process.Start(new ProcessStartInfo("ms-clock:") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("outlookcal:") { UseShellExecute = true }); } catch { }
        }
    }

    private void AppSettings_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateBounce(ItemSettings);
        if (Application.Current is App app && App.Services != null)
        {
            try
            {
                var settingsWindow = App.Services.GetService(typeof(SettingsWindow)) as SettingsWindow;
                if (settingsWindow != null)
                {
                    settingsWindow.Show();
                    settingsWindow.Activate();
                    return;
                }
            }
            catch { }
        }
        try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); } catch { }
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

    #region Intelligent Auto-Hide & Window Collision Dodging

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        EvaluateDockAutoHide();
    }

    private void EvaluateDockAutoHide()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        // 1. Get current mouse position in physical screen coordinates
        bool hasMouse = Win32Interop.GetCursorPos(out var mousePt);

        Point dockTopLeft;
        Point dockBottomRight;
        try
        {
            dockTopLeft = PointToScreen(new Point(0, 0));
            dockBottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        }
        catch
        {
            return;
        }

        // Is mouse cursor hovering over the dock window?
        bool isMouseOverDock = hasMouse &&
            mousePt.X >= dockTopLeft.X - 15 && mousePt.X <= dockBottomRight.X + 15 &&
            mousePt.Y >= dockTopLeft.Y && mousePt.Y <= dockBottomRight.Y + 25;

        // Is mouse touching the bottom reveal strip of the screen where the dock sits?
        bool isMouseAtBottomEdge = hasMouse &&
            mousePt.X >= dockTopLeft.X - 35 && mousePt.X <= dockBottomRight.X + 35 &&
            mousePt.Y >= dockBottomRight.Y - 8;

        if (isMouseAtBottomEdge || isMouseOverDock)
        {
            _isRevealedByMouse = true;
            _mouseLeaveTime = 0;
        }
        else if (_isRevealedByMouse)
        {
            long now = Environment.TickCount64;
            if (_mouseLeaveTime == 0)
            {
                _mouseLeaveTime = now;
            }
            else if (now - _mouseLeaveTime > 320) // 320ms grace period after cursor leaves
            {
                _isRevealedByMouse = false;
                _mouseLeaveTime = 0;
            }
        }

        // 2. Check if any application window is actively overlapping the dock area
        bool isOverlapping = IsAnyWindowOverlappingDock();

        // 3. Dock hides only if there is an overlapping window AND mouse is not actively revealing it
        bool shouldHide = isOverlapping && !_isRevealedByMouse;

        if (shouldHide != _isDockSlidDown)
        {
            _isDockSlidDown = shouldHide;
            AnimateDockSlide(shouldHide);
        }
    }

    private bool IsAnyWindowOverlappingDock()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return false;

        Point topLeft;
        Point bottomRight;
        try
        {
            // Visual dock pill bounds in screen coordinates (from Y=50 down to window height)
            topLeft = PointToScreen(new Point(12, 50));
            bottomRight = PointToScreen(new Point(ActualWidth - 12, Height));
        }
        catch
        {
            return false;
        }

        double dockLeft = topLeft.X;
        double dockRight = bottomRight.X;
        double dockTop = topLeft.Y;
        double dockBottom = bottomRight.Y;

        uint currentPid = (uint)Environment.ProcessId;
        IntPtr dockHwnd = new WindowInteropHelper(this).Handle;
        bool hasOverlap = false;

        Win32Interop.EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == dockHwnd) return true;

            // Ignore our own process windows (Settings, Notch, Tray Menu, Spotlight)
            Win32Interop.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == currentPid) return true;

            // Ignore invisible, minimized, or cloaked windows
            if (!Win32Interop.IsWindowVisible(hWnd) || Win32Interop.IsIconic(hWnd)) return true;
            if (FullscreenDetector.IsWindowCloaked(hWnd)) return true;
            if (FullscreenDetector.IsBlockedClass(hWnd)) return true;

            // Get window bounds using DwmGetWindowAttribute (excludes drop shadows)
            if (FullscreenDetector.TryGetWindowBounds(hWnd, out var wr))
            {
                int width = wr.Right - wr.Left;
                int height = wr.Bottom - wr.Top;
                if (width < 80 || height < 80) return true; // ignore tiny tooltips/widgets

                // Check intersection with the Dock area
                bool intersects = wr.Right > dockLeft &&
                                 wr.Left < dockRight &&
                                 wr.Bottom > dockTop &&
                                 wr.Top < dockBottom;

                if (intersects)
                {
                    hasOverlap = true;
                    return false; // Found overlapping window! Stop enum.
                }
            }

            return true;
        }, IntPtr.Zero);

        return hasOverlap;
    }

    private void AnimateDockSlide(bool hide)
    {
        var animDuration = TimeSpan.FromMilliseconds(240);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        double targetY = hide ? 95.0 : 0.0;
        double targetOpacity = hide ? 0.0 : 1.0;

        var transAnim = new DoubleAnimation(targetY, animDuration) { EasingFunction = ease };
        var opacityAnim = new DoubleAnimation(targetOpacity, animDuration) { EasingFunction = ease };

        DockRootTranslate.BeginAnimation(TranslateTransform.YProperty, transAnim);
        DockRootGrid.BeginAnimation(OpacityProperty, opacityAnim);
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _procCheckTimer.Stop();
        _autoHideTimer.Stop();
        base.OnClosed(e);
    }
}
