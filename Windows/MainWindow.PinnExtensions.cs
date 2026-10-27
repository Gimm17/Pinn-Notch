using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
}
