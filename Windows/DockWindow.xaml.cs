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
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Windows;

public partial class DockWindow : Window
{
    private readonly ISettingsService _settingsService;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _procCheckTimer;
    private readonly (Grid Grid, ScaleTransform Scale, TranslateTransform Trans, Ellipse Dot, string Title)[] _items;

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

        Loaded += DockWindow_Loaded;
        SizeChanged += DockWindow_SizeChanged;
    }

    private void DockWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RepositionAtBottom();
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
        const double range = 90.0;
        const double maxScale = 1.45;
        const double maxLift = 13.0;

        (Grid Grid, ScaleTransform Scale, TranslateTransform Trans, Ellipse Dot, string Title)? closestItem = null;
        double minDistance = double.MaxValue;

        foreach (var item in _items)
        {
            var itemPos = item.Grid.TransformToAncestor(IconsPanel).Transform(new Point(item.Grid.ActualWidth / 2, item.Grid.ActualHeight / 2));
            double dist = Math.Abs(mousePos.X - itemPos.X);

            if (dist < minDistance)
            {
                minDistance = dist;
                closestItem = item;
            }

            if (dist < range)
            {
                double factor = Math.Cos((dist / range) * (Math.PI / 2)); // 1.0 down to 0.0
                double targetScale = 1.0 + factor * (maxScale - 1.0);
                double targetY = -factor * maxLift;

                item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                item.Trans.BeginAnimation(TranslateTransform.YProperty, null);

                item.Scale.ScaleX = targetScale;
                item.Scale.ScaleY = targetScale;
                item.Trans.Y = targetY;
            }
            else
            {
                item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                item.Trans.BeginAnimation(TranslateTransform.YProperty, null);

                item.Scale.ScaleX = 1.0;
                item.Scale.ScaleY = 1.0;
                item.Trans.Y = 0.0;
            }
        }

        // Tooltip badge over closest icon
        if (closestItem != null && minDistance < 42.0)
        {
            var rootPos = closestItem.Value.Grid.TransformToAncestor(DockRootGrid).Transform(new Point(closestItem.Value.Grid.ActualWidth / 2, 0));
            TooltipText.Text = closestItem.Value.Title;
            TooltipBadge.UpdateLayout();

            double badgeX = rootPos.X - (TooltipBadge.ActualWidth / 2);
            TooltipTranslate.X = Math.Max(10, badgeX);

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
            var transAnimY = new DoubleAnimation(0.0, animDuration) { EasingFunction = ease };

            item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimX);
            item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimY);
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

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _procCheckTimer.Stop();
        base.OnClosed(e);
    }
}
