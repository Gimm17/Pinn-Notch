using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Windows;

namespace VNotch;

public partial class SettingsWindow
{
    private void LoadPinnSettings(NotchSettings settings)
    {
        EnablePinnMascotCheck.IsChecked = settings.EnablePinnMascot;
        PinnMusicReactionCheck.IsChecked = settings.PinnMascotReactToMusic;
        EnableGhostModeCheck.IsChecked = settings.EnableGhostMode;
        SetComboSelectedTag(PinnMascotPositionCombo, settings.PinnMascotPosition);
        if (PinnMascotPositionRow != null)
            PinnMascotPositionRow.Visibility = settings.EnablePinnMascot ? Visibility.Visible : Visibility.Collapsed;
        if (PinnMascotPositionLabel != null) PinnMascotPositionLabel.Text = Loc.Get("settings.pinnMascotPosition");
        if (PinnMascotPositionHint != null) PinnMascotPositionHint.Text = Loc.Get("settings.pinnMascotPosition.hint");
        if (PinnMascotPosRightItem != null) PinnMascotPosRightItem.Content = Loc.Get("settings.pinnMascotPosition.right");
        if (PinnMascotPosCenterItem != null) PinnMascotPosCenterItem.Content = Loc.Get("settings.pinnMascotPosition.center");
        if (PinnMascotPosLeftItem != null) PinnMascotPosLeftItem.Content = Loc.Get("settings.pinnMascotPosition.left");
        EnableUsbNotifCheck.IsChecked = settings.EnableUsbNotifications;

        PinnThemeCombo.ItemsSource = UiPalette.AllThemes;
        PinnThemeCombo.SelectedValuePath = nameof(PinnThemeDefinition.Id);
        PinnThemeCombo.SelectedValue = settings.PinnTheme;

        // Auto-Hide & Precision Trigger Zones
        SetComboSelectedTag(DockAutoHideModeCombo, settings.DockAutoHideMode);
        DockRevealHeightSlider.Value = settings.DockRevealHeightPx;
        DockRevealExtraWidthSlider.Value = settings.DockRevealExtraWidthPx;

        SetComboSelectedTag(NotchAutoHideModeCombo, settings.NotchAutoHideMode);
        NotchRevealHeightSlider.Value = settings.NotchRevealHeightPx;
        NotchRevealExtraWidthSlider.Value = settings.NotchRevealExtraWidthPx;

        NotchHiddenPeekCheck.IsChecked = settings.NotchHiddenPeekEnabled;
        NotchHiddenPeekHeightSlider.Value = settings.NotchHiddenPeekHeightPx;
        NotchNotificationRevealCheck.IsChecked = settings.NotchNotificationRevealEnabled;

        // Transparent & Freeze Taskbar Integration
        EnableTransparentTaskbarCheck.IsChecked = settings.EnableTransparentTaskbar;
        SetComboSelectedTag(TaskbarStyleCombo, settings.TaskbarStyle);
        TaskbarOpacitySlider.Value = settings.TaskbarOpacity * 100.0;
        TaskbarDynamicMaximizedCheck.IsChecked = settings.TaskbarDynamicMaximized;
        UpdateTransparentTaskbarUi(settings.EnableTransparentTaskbar);

        EnableFreezeTaskbarCheck.IsChecked = settings.EnableFreezeTaskbar;
        SetComboSelectedTag(TaskbarFreezeModeCombo, settings.TaskbarFreezeMode);
        UpdateFreezeTaskbarUi(settings.EnableFreezeTaskbar);
    }

    private void SavePinnSettings(NotchSettings snapshot)
    {
        snapshot.EnablePinnMascot = EnablePinnMascotCheck.IsChecked ?? true;
        snapshot.PinnMascotPosition = GetComboSelectedTag(PinnMascotPositionCombo, "Right");
        snapshot.PinnMascotReactToMusic = PinnMusicReactionCheck.IsChecked ?? true;
        snapshot.EnableGhostMode = EnableGhostModeCheck.IsChecked ?? true;
        snapshot.EnableUsbNotifications = EnableUsbNotifCheck.IsChecked ?? true;

        if (PinnThemeCombo.SelectedValue is string themeId)
        {
            snapshot.PinnTheme = themeId;
            UiPalette.ApplyTheme(themeId, Application.Current.Resources);
        }

        // Auto-Hide & Precision Trigger Zones
        snapshot.DockAutoHideMode = GetComboSelectedTag(DockAutoHideModeCombo, "Smart");
        snapshot.DockRevealHeightPx = (int)DockRevealHeightSlider.Value;
        snapshot.DockRevealExtraWidthPx = (int)DockRevealExtraWidthSlider.Value;

        snapshot.NotchAutoHideMode = GetComboSelectedTag(NotchAutoHideModeCombo, "Smart");
        snapshot.NotchRevealHeightPx = (int)NotchRevealHeightSlider.Value;
        snapshot.NotchRevealExtraWidthPx = (int)NotchRevealExtraWidthSlider.Value;

        snapshot.NotchHiddenPeekEnabled = NotchHiddenPeekCheck.IsChecked ?? true;
        snapshot.NotchHiddenPeekHeightPx = (int)NotchHiddenPeekHeightSlider.Value;
        snapshot.NotchNotificationRevealEnabled = NotchNotificationRevealCheck.IsChecked ?? true;

        // Transparent & Freeze Taskbar Integration
        snapshot.EnableTransparentTaskbar = EnableTransparentTaskbarCheck.IsChecked ?? false;
        snapshot.TaskbarStyle = GetComboSelectedTag(TaskbarStyleCombo, "Clear");
        snapshot.TaskbarOpacity = TaskbarOpacitySlider.Value / 100.0;
        snapshot.TaskbarDynamicMaximized = TaskbarDynamicMaximizedCheck.IsChecked ?? true;

        snapshot.EnableFreezeTaskbar = EnableFreezeTaskbarCheck.IsChecked ?? false;
        snapshot.TaskbarFreezeMode = GetComboSelectedTag(TaskbarFreezeModeCombo, "HideSuppress");
    }

    private static void SetComboSelectedTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string GetComboSelectedTag(ComboBox combo, string defaultVal)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag;
        return defaultVal;
    }

    private void EnablePinnMascotCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (PinnMascotPositionRow != null)
            PinnMascotPositionRow.Visibility = EnablePinnMascotCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PushLivePreview();
    }

    private void PinnMascotPositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void PinnMusicReactionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void PinnThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (PinnThemeCombo.SelectedValue is string themeId)
        {
            UiPalette.ApplyTheme(themeId, Application.Current.Resources);
        }
        PushLivePreview();
    }

    private void EnableGhostModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void EnableUsbNotifCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    // Auto-Hide & Trigger Area Event Handlers
    private void DockAutoHideModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void DockRevealHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;
        ShowDockTriggerPreview();
        PushLivePreview();
    }

    private void DockRevealExtraWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;
        ShowDockTriggerPreview();
        PushLivePreview();
    }

    private void NotchAutoHideModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void NotchRevealHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;
        ShowNotchTriggerPreview();
        PushLivePreview();
    }

    private void NotchRevealExtraWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;
        ShowNotchTriggerPreview();
        PushLivePreview();
    }

    private void NotchHiddenPeekCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void NotchHiddenPeekHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void NotchNotificationRevealCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void ShowNotchTriggerPreview()
    {
        var workArea = SystemParameters.WorkArea;
        double revealHeight = NotchRevealHeightSlider.Value;
        double extraWidth = NotchRevealExtraWidthSlider.Value;
        double notchWidth = 220; // standard notch base width

        var zone = TriggerZoneCalculator.CalculateNotchTriggerZone(
            workArea.Left, workArea.Width, notchWidth, revealHeight, extraWidth);

        TriggerZonePreviewWindow.ShowPreview(zone, $"Area Trigger Notch: {(int)zone.Height}px × {(int)zone.Width}px");
    }

    private void ShowDockTriggerPreview()
    {
        var workArea = SystemParameters.WorkArea;
        double revealHeight = DockRevealHeightSlider.Value;
        double extraWidth = DockRevealExtraWidthSlider.Value;
        double dockWidth = 580; // standard dock base width
        double dockLeft = workArea.Left + (workArea.Width - dockWidth) / 2.0;

        var zone = TriggerZoneCalculator.CalculateDockTriggerZone(
            dockLeft, dockWidth, workArea.Bottom, revealHeight, extraWidth);

        TriggerZonePreviewWindow.ShowPreview(zone, $"Area Trigger Dock: {(int)zone.Height}px × {(int)zone.Width}px");
    }

    private void UpdateTransparentTaskbarUi(bool enabled)
    {
        if (TransparentTaskbarOptionsPanel != null)
        {
            TransparentTaskbarOptionsPanel.IsEnabled = enabled;
            TransparentTaskbarOptionsPanel.Opacity = enabled ? 1.0 : 0.45;
        }
    }

    private void UpdateFreezeTaskbarUi(bool enabled)
    {
        if (FreezeTaskbarOptionsPanel != null)
        {
            FreezeTaskbarOptionsPanel.IsEnabled = enabled;
            FreezeTaskbarOptionsPanel.Opacity = enabled ? 1.0 : 0.45;
        }
    }

    private void EnableTransparentTaskbarCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTransparentTaskbarUi(EnableTransparentTaskbarCheck.IsChecked == true);
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void TaskbarStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void TaskbarOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void TaskbarDynamicMaximizedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void EnableFreezeTaskbarCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFreezeTaskbarUi(EnableFreezeTaskbarCheck.IsChecked == true);
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void TaskbarFreezeModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }
}
