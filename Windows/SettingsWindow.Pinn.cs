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
    }

    private void SavePinnSettings(NotchSettings snapshot)
    {
        snapshot.EnablePinnMascot = EnablePinnMascotCheck.IsChecked ?? true;
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
}
