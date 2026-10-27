using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;

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
        PinnThemeCombo.DisplayMemberPath = nameof(PinnThemeDefinition.DisplayName);
        PinnThemeCombo.SelectedValuePath = nameof(PinnThemeDefinition.Id);
        PinnThemeCombo.SelectedValue = settings.PinnTheme;
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
}
