using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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
        public Ellipse Dot { get; set; } = null!;
        public string Title { get; set; } = "";
        public SpringValue ScaleSpring = new(1.0);
        public SpringValue TransXSpring = new(0.0);
        public SpringValue TransYSpring = new(0.0);
    }

    private readonly ISettingsService _settingsService;
    private readonly WindowOverlapMonitor _overlapMonitor;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _procCheckTimer;
    private readonly DispatcherTimer _cursorPollTimer;
    private readonly DockItemState[] _items;

    private bool _isRenderLoopActive;
    private long _lastRenderTimestamp;
    private SpringValue _tooltipXSpring = new(0.0);
    private SpringValue _tooltipYSpring = new(0.0);

    private bool _isDockOverlapped;
    private bool _isDockSlidDown;
    private bool _isRevealedByMouse;
    private long _revealStartTick;
    private long _hideStartTick;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public DockWindow(ISettingsService settingsService, WindowOverlapMonitor overlapMonitor)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _overlapMonitor = overlapMonitor;
        _overlapMonitor.RegisterDockRegionProvider(GetDockScreenBounds);
        _overlapMonitor.DockOverlapChanged += OnDockOverlapChanged;

        _items = new[]
        {
            new DockItemState { Grid = ItemExplorer, Scale = ScaleExplorer, Trans = TransExplorer, Dot = DotExplorer, Title = "File Explorer" },
            new DockItemState { Grid = ItemBrowser, Scale = ScaleBrowser, Trans = TransBrowser, Dot = DotBrowser, Title = "Browser" },
            new DockItemState { Grid = ItemChat, Scale = ScaleChat, Trans = TransChat, Dot = DotChat, Title = "Pesan & Chat" },
            new DockItemState { Grid = ItemMusic, Scale = ScaleMusic, Trans = TransMusic, Dot = DotMusic, Title = "Musik" },
            new DockItemState { Grid = ItemNotes, Scale = ScaleNotes, Trans = TransNotes, Dot = DotNotes, Title = "Catatan" },
            new DockItemState { Grid = ItemCalendar, Scale = ScaleCalendar, Trans = TransCalendar, Dot = DotCalendar, Title = "Kalender" },
            new DockItemState { Grid = ItemSettings, Scale = ScaleSettings, Trans = TransSettings, Dot = DotSettings, Title = "Pengaturan" }
        };

        // Live Clock Timer
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClockAndCalendar();
        _clockTimer.Start();
        UpdateClockAndCalendar();

        // Process running check timer (offloaded to background thread)
        _procCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _procCheckTimer.Tick += (_, _) => CheckRunningApps();
        _procCheckTimer.Start();
        CheckRunningApps();

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

    private void CheckRunningApps()
    {
        Task.Run(() =>
        {
            try
            {
                var procs = Process.GetProcesses().Select(p => p.ProcessName.ToLowerInvariant()).ToHashSet();
                bool hasExplorer = procs.Contains("explorer");
                bool hasBrowser = procs.Contains("chrome") || procs.Contains("msedge") || procs.Contains("firefox") || procs.Contains("brave");
                bool hasChat = procs.Contains("whatsapp") || procs.Contains("discord") || procs.Contains("telegram") || procs.Contains("teams");
                bool hasMusic = procs.Contains("spotify") || procs.Contains("music") || procs.Contains("itunes");
                bool hasNotes = procs.Contains("notepad") || procs.Contains("onenote");
                bool hasCalendar = procs.Contains("olk") || procs.Contains("outlook");
                bool hasSettings = procs.Contains("systemsettings");

                Dispatcher.BeginInvoke(() =>
                {
                    DotExplorer.Opacity = hasExplorer ? 1.0 : 0.0;
                    DotBrowser.Opacity = hasBrowser ? 1.0 : 0.0;
                    DotChat.Opacity = hasChat ? 1.0 : 0.0;
                    DotMusic.Opacity = hasMusic ? 1.0 : 0.0;
                    DotNotes.Opacity = hasNotes ? 1.0 : 0.0;
                    DotCalendar.Opacity = hasCalendar ? 1.0 : 0.0;
                    DotSettings.Opacity = hasSettings ? 1.0 : 0.0;
                });
            }
            catch
            {
                // Best effort background check
            }
        });
    }

    #region macOS Dock Frame-Synced Spring Magnification & Tooltip

    private void IconsPanel_MouseMove(object sender, MouseEventArgs e)
    {
        var mousePos = e.GetPosition(IconsPanel);
        const double range = 100.0;
        const double maxScale = 1.30;
        const double maxLift = 14.0;
        const double maxPush = 8.0;

        DockItemState? closestItem = null;
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
                double factor = Math.Cos((absDist / range) * (Math.PI / 2));
                item.ScaleSpring.Target = 1.0 + factor * (maxScale - 1.0);
                item.TransYSpring.Target = -factor * maxLift;

                double pushFactor = Math.Sin((absDist / range) * Math.PI);
                item.TransXSpring.Target = (distX >= 0 ? 1.0 : -1.0) * pushFactor * maxPush;
            }
            else
            {
                item.ScaleSpring.Target = 1.0;
                item.TransXSpring.Target = 0.0;
                item.TransYSpring.Target = 0.0;
            }
        }

        StartRenderLoop();

        // Tooltip badge target calculation
        if (closestItem != null && minDistance < 36.0)
        {
            var rootPos = closestItem.Grid.TransformToAncestor(DockRootGrid).Transform(new Point(closestItem.Grid.ActualWidth / 2, 0));
            TooltipText.Text = closestItem.Title;

            double badgeW = TooltipBadge.ActualWidth > 0 ? TooltipBadge.ActualWidth : 60;
            double badgeH = TooltipBadge.ActualHeight > 0 ? TooltipBadge.ActualHeight : 24;

            _tooltipXSpring.Target = Math.Max(12, rootPos.X - (badgeW / 2));
            _tooltipYSpring.Target = Math.Max(6, rootPos.Y - badgeH - 10);

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
        foreach (var item in _items)
        {
            item.ScaleSpring.Target = 1.0;
            item.TransXSpring.Target = 0.0;
            item.TransYSpring.Target = 0.0;
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
        foreach (var item in _items)
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
        if (!IsLoaded || Visibility != Visibility.Visible || ActualWidth <= 0 || ActualHeight <= 0)
            return null;
        try
        {
            Point topLeft = PointToScreen(new Point(12, 50));
            Point bottomRight = PointToScreen(new Point(ActualWidth - 12, Height));
            return new Rect(topLeft, bottomRight);
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
        if (!IsLoaded || Visibility != Visibility.Visible) return;

        var settings = _settingsService.Load();
        if (settings.DockAutoHideMode == "Never")
        {
            if (_isDockSlidDown) SlideDock(false);
            return;
        }

        if (Win32Interop.GetCursorPos(out var mousePt))
        {
            var workArea = SystemParameters.WorkArea;
            double dockWidth = ActualWidth > 0 ? ActualWidth : 580;
            var triggerZone = TriggerZoneCalculator.CalculateDockTriggerZone(
                Left, dockWidth, workArea.Bottom,
                settings.DockRevealHeightPx, settings.DockRevealExtraWidthPx);

            bool inTriggerZone = TriggerZoneCalculator.IsPointInZone(triggerZone, new Point(mousePt.X, mousePt.Y));
            bool isOverDockWindow = mousePt.X >= Left - 15 && mousePt.X <= Left + dockWidth + 15 &&
                                   mousePt.Y >= Top && mousePt.Y <= Top + Height + 25;

            if (inTriggerZone || isOverDockWindow)
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

    private void UpdateDockSlideState()
    {
        var settings = _settingsService.Load();
        bool shouldHide = false;
        if (settings.DockAutoHideMode == "Always")
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

    private void SlideDock(bool hide)
    {
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
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);
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

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _procCheckTimer.Stop();
        _cursorPollTimer.Stop();
        StopRenderLoop();
        _overlapMonitor.DockOverlapChanged -= OnDockOverlapChanged;
        base.OnClosed(e);
    }
}
