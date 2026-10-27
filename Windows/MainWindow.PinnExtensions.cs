using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private bool _isGhostMode = false;
    public bool IsGhostMode => _isGhostMode;

    private bool _isNotesView = false;
    private UiScheduledTimer? _usbDismissTimer;

    // --- 1. GHOST / FOCUS MODE (Triple Click) ---
    public void ToggleGhostMode()
    {
        SetGhostMode(!_isGhostMode);
    }

    public void SetGhostMode(bool enabled)
    {
        _isGhostMode = enabled;
        if (MenuGhost != null)
        {
            MenuGhost.IsChecked = _isGhostMode;
        }

        if (_isGhostMode)
        {
            if (_isExpanded)
            {
                CollapseNotch();
            }
            NotchWrapper.Opacity = Math.Clamp(_settings.GhostModeOpacity, 0.2, 0.9);
            InteractionSounds.Play(InteractionCue.ToggleOn);
            ShowUsbPillCustom("Ghost Mode Active 👻 (Triple-click / tray to exit)");
        }
        else
        {
            NotchWrapper.Opacity = _settings.Opacity;
            InteractionSounds.Play(InteractionCue.ToggleOff);
            ShowUsbPillCustom("Ghost Mode Disabled ✨");
        }
    }

    private void ToggleGhost_Click(object sender, RoutedEventArgs e)
    {
        ToggleGhostMode();
    }

    // --- 2. QUICK NOTES & TO-DOS WIDGET ---
    private void NotesIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isNotesView || _isAnimating) return;

        SwitchToNotesView();
    }

    private void SwitchToNotesView()
    {
        _isNotesView = true;
        _isAudioView = false;
        _isTimerView = false;
        _isSecondaryView = false;

        // Hide other views
        if (AudioContent != null) { AudioContent.Visibility = Visibility.Collapsed; AudioContent.Opacity = 0; }
        if (TimerContent != null) { TimerContent.Visibility = Visibility.Collapsed; TimerContent.Opacity = 0; }
        if (SecondaryContent != null) { SecondaryContent.Visibility = Visibility.Collapsed; SecondaryContent.Opacity = 0; }
        if (ExpandedContent != null) { ExpandedContent.Visibility = Visibility.Collapsed; }

        // Update nav icon opacities
        if (NotesIconButton != null) NotesIconButton.Opacity = 1.0;
        if (HomeIconButton != null) HomeIconButton.Opacity = 0.45;
        if (ClipboardIconButton != null) ClipboardIconButton.Opacity = 0.45;
        if (TimerIconButton != null) TimerIconButton.Opacity = 0.45;
        if (AudioIconButton != null) AudioIconButton.Opacity = 0.45;

        // Show Notes view
        if (NotesContent != null)
        {
            NotesContent.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NotesContent.BeginAnimation(OpacityProperty, fadeIn);
        }

        if (!_isExpanded)
        {
            ExpandNotch();
        }
    }

    private void HideNotesView()
    {
        if (!_isNotesView) return;
        _isNotesView = false;

        if (NotesContent != null)
        {
            NotesContent.BeginAnimation(OpacityProperty, null);
            NotesContent.Opacity = 0;
            NotesContent.Visibility = Visibility.Collapsed;
        }

        if (NotesIconButton != null)
        {
            NotesIconButton.Opacity = 0.45;
        }
    }

    // --- 3. USB CONNECT / DISCONNECT ALERTS ---
    private void HandleUsbDeviceChanged(bool isConnected)
    {
        if (!_settings.EnableUsbNotifications) return;

        string message = isConnected ? "USB Device Connected 🔌" : "USB Device Disconnected ⏏️";
        ShowUsbPillCustom(message);
    }

    private void ShowUsbPillCustom(string message)
    {
        if (UsbNotification == null || UsbNotificationText == null || UsbNotificationTranslate == null) return;

        UsbNotificationText.Text = message;
        UsbNotification.Visibility = Visibility.Visible;

        var fadeIn = MakeCompactNotificationEntrance(0, 1);
        UsbNotification.BeginAnimation(OpacityProperty, fadeIn);

        var slideUp = MakeCompactNotificationEntrance(6, 0);
        UsbNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);

        InteractionSounds.Play(InteractionCue.Click);
        NotifyNotchNotificationPresented();

        _usbDismissTimer?.Stop();
        _usbDismissTimer?.Dispose();

        _usbDismissTimer = new UiScheduledTimer
        {
            Interval = TimeSpan.FromMilliseconds(2500)
        };
        _usbDismissTimer.Tick += (s, e) =>
        {
            _usbDismissTimer.Stop();
            _usbDismissTimer.Dispose();
            _usbDismissTimer = null;
            DismissUsbNotification();
        };
        _usbDismissTimer.Start();
    }

    private void DismissUsbNotification()
    {
        if (UsbNotification == null || UsbNotificationTranslate == null) return;

        var fadeOut = MakeCompactNotificationExit(1, 0);
        fadeOut.Completed += (s, e) =>
        {
            UsbNotification.Visibility = Visibility.Collapsed;
        };
        UsbNotification.BeginAnimation(OpacityProperty, fadeOut);

        var slideDown = MakeCompactNotificationExit(0, -4);
        UsbNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
    }

    // --- 4. PINN THE CAT MASCOT HOOKS ---
    private void InitializePinnMascot()
    {
        if (PinnMascot == null) return;

        PinnMascot.Visibility = _settings.EnablePinnMascot ? Visibility.Visible : Visibility.Collapsed;

        PinnMascot.HorizontalAlignment = _settings.PinnMascotPosition switch
        {
            "Left" => HorizontalAlignment.Left,
            "Center" => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Right
        };

        if (_settings.PinnMascotPosition == "Left")
        {
            PinnMascot.Margin = new Thickness(24, -16, 0, 0);
        }
        else if (_settings.PinnMascotPosition == "Center")
        {
            PinnMascot.Margin = new Thickness(0, -18, 0, 0);
        }
        else
        {
            PinnMascot.Margin = new Thickness(0, -16, 24, 0);
        }
    }

    public void OnMediaPlayingStateChanged(bool isPlaying)
    {
        if (PinnMascot != null && _settings.PinnMascotReactToMusic)
        {
            PinnMascot.IsDancing = isPlaying;
        }
    }

    // --- 5. FLOATING DOCK LAYOUT MODES ---
    public void SetLayoutMode(string mode)
    {
        _settings.LayoutMode = mode;
        _settingsService.Save(_settings);

        if (MenuModeNotchAndDock != null) MenuModeNotchAndDock.IsChecked = mode == "NotchAndDock";
        if (MenuModeNotchOnly != null) MenuModeNotchOnly.IsChecked = mode == "NotchOnly";
        if (MenuModeDockOnly != null) MenuModeDockOnly.IsChecked = mode == "DockOnly";

        var dockWindow = App.Services?.GetService(typeof(Windows.DockWindow)) as Windows.DockWindow;

        if (mode == "DockOnly")
        {
            if (_isNotchVisible)
            {
                _isNotchVisible = false;
                CollapseAll();
                ApplyNotchVisibilityState();
            }
            dockWindow?.Show();
            dockWindow?.RepositionAtBottom();
            ShowUsbPillCustom("Mode: Dock Saja 💻");
        }
        else if (mode == "NotchOnly")
        {
            if (!_isNotchVisible)
            {
                _isNotchVisible = true;
                ApplyNotchVisibilityState();
            }
            dockWindow?.Hide();
            ShowUsbPillCustom("Mode: Notch Saja 📱");
        }
        else // "NotchAndDock"
        {
            if (!_isNotchVisible)
            {
                _isNotchVisible = true;
                ApplyNotchVisibilityState();
            }
            dockWindow?.Show();
            dockWindow?.RepositionAtBottom();
            ShowUsbPillCustom("Mode: Notch + Dock ✨");
        }
    }

    private void LayoutMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string mode })
        {
            SetLayoutMode(mode);
        }
    }

    // --- 6. NOTCH SMART AUTO-HIDE & PRECISION REVEAL ---
    private WindowOverlapMonitor? _notchOverlapMonitor;
    private bool _isNotchOverlapped;
    private bool _isNotchSmartHidden;
    private bool _isNotchRevealedByMouse;
    private bool _isNotchTemporarilyRevealed;
    private long _notchRevealStartTick;
    private long _notchHideStartTick;
    private UiScheduledTimer? _notchNotificationRevealTimer;
    private UiScheduledTimer? _notchCursorPollTimer;

    public void InitNotchSmartAutoHide()
    {
        _notchOverlapMonitor = App.Services?.GetService(typeof(WindowOverlapMonitor)) as WindowOverlapMonitor;
        if (_notchOverlapMonitor != null)
        {
            _notchOverlapMonitor.RegisterNotchRegionProvider(GetNotchScreenBounds);
            _notchOverlapMonitor.NotchOverlapChanged += OnNotchOverlapChanged;
        }

        _notchCursorPollTimer = new UiScheduledTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _notchCursorPollTimer.Tick += NotchCursorPollTimer_Tick;
        _notchCursorPollTimer.Start();
    }

    public Rect? GetNotchScreenBounds()
    {
        if (!IsLoaded || !_isNotchVisible || NotchBorder == null) return null;
        try
        {
            Point topLeft = NotchBorder.PointToScreen(new Point(0, 0));
            Point bottomRight = NotchBorder.PointToScreen(new Point(NotchBorder.ActualWidth, NotchBorder.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return null;
        }
    }

    private void OnNotchOverlapChanged(bool isOverlapped)
    {
        _isNotchOverlapped = isOverlapped;
        UpdateNotchSmartAutoHideState();
    }

    private void NotchCursorPollTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsLoaded || !_isNotchVisible) return;

        if (_settings.NotchAutoHideMode == "Never")
        {
            if (_isNotchSmartHidden)
            {
                _isNotchSmartHidden = false;
                ApplyNotchSmartAutoHideSlide(false);
            }
            return;
        }

        if (Win32Interop.GetCursorPos(out var mousePt))
        {
            double notchWidth = NotchBorder?.ActualWidth > 0 ? NotchBorder.ActualWidth : 200;
            var triggerZone = TriggerZoneCalculator.CalculateNotchTriggerZone(
                Left, Width, notchWidth, _settings.NotchRevealHeightPx, _settings.NotchRevealExtraWidthPx);

            bool inTriggerZone = TriggerZoneCalculator.IsPointInZone(triggerZone, new Point(mousePt.X, mousePt.Y));
            bool isOverNotch = mousePt.X >= Left && mousePt.X <= Left + Width &&
                               mousePt.Y >= Top && mousePt.Y <= Top + (NotchBorder?.ActualHeight ?? 40) + 10;

            if (inTriggerZone || isOverNotch || _isExpanded || _isMusicExpanded)
            {
                if (!_isNotchRevealedByMouse)
                {
                    long now = Environment.TickCount64;
                    if (_notchRevealStartTick == 0)
                    {
                        _notchRevealStartTick = now;
                    }
                    else if (now - _notchRevealStartTick >= _settings.NotchRevealDelayMs)
                    {
                        _isNotchRevealedByMouse = true;
                        _notchRevealStartTick = 0;
                        _notchHideStartTick = 0;
                        UpdateNotchSmartAutoHideState();
                    }
                }
            }
            else
            {
                _notchRevealStartTick = 0;
                if (_isNotchRevealedByMouse)
                {
                    long now = Environment.TickCount64;
                    if (_notchHideStartTick == 0)
                    {
                        _notchHideStartTick = now;
                    }
                    else if (now - _notchHideStartTick >= _settings.NotchHideDelayMs)
                    {
                        _isNotchRevealedByMouse = false;
                        _notchHideStartTick = 0;
                        UpdateNotchSmartAutoHideState();
                    }
                }
            }
        }
    }

    public void NotifyNotchNotificationPresented()
    {
        if (!_settings.NotchNotificationRevealEnabled || !_isNotchSmartHidden) return;

        _isNotchTemporarilyRevealed = true;
        UpdateNotchSmartAutoHideState();

        _notchNotificationRevealTimer?.Stop();
        _notchNotificationRevealTimer = new UiScheduledTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(_settings.NotchNotificationRevealDurationMs)
        };
        _notchNotificationRevealTimer.Tick += (s, e) =>
        {
            _notchNotificationRevealTimer.Stop();
            _isNotchTemporarilyRevealed = false;
            UpdateNotchSmartAutoHideState();
        };
        _notchNotificationRevealTimer.Start();
    }

    public void UpdateNotchSmartAutoHideState()
    {
        bool shouldHide = false;
        if (_settings.NotchAutoHideMode == "Always")
        {
            shouldHide = !_isNotchRevealedByMouse && !_isNotchTemporarilyRevealed && !_isExpanded && !_isMusicExpanded;
        }
        else if (_settings.NotchAutoHideMode == "Smart")
        {
            shouldHide = _isNotchOverlapped && !_isNotchRevealedByMouse && !_isNotchTemporarilyRevealed && !_isExpanded && !_isMusicExpanded;
        }
        else // "Never"
        {
            shouldHide = false;
        }

        if (shouldHide != _isNotchSmartHidden)
        {
            _isNotchSmartHidden = shouldHide;
            ApplyNotchSmartAutoHideSlide(shouldHide);
        }
    }

    private void ApplyNotchSmartAutoHideSlide(bool hide)
    {
        if (NotchContainer == null || NotchContainerTranslate == null) return;

        double slideDistance = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight + 10 : _collapsedHeight + 10;
        double targetY = 0;
        double targetOpacity = 1.0;

        if (hide)
        {
            if (_settings.NotchHiddenPeekEnabled)
            {
                double peekHeight = Math.Clamp(_settings.NotchHiddenPeekHeightPx, 1, 12);
                targetY = -(slideDistance - peekHeight);
                targetOpacity = 0.92;
            }
            else
            {
                targetY = -slideDistance;
                targetOpacity = 0.0;
            }
        }

        var animDuration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var transAnim = new DoubleAnimation(targetY, animDuration) { EasingFunction = ease };
        var opacityAnim = new DoubleAnimation(targetOpacity, animDuration) { EasingFunction = ease };

        NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, transAnim);
        NotchContainer.BeginAnimation(OpacityProperty, opacityAnim);
    }
}
