using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ControlCenterView : UserControl
{
    private ControlCenterViewModel? _viewModel;
    private bool _isUpdatingInternally = false;
    private bool _isLoaded = false;

    public ControlCenterView()
    {
        InitializeComponent();
        Loaded += (_, _) => _isLoaded = true;
    }

    public void Attach(ControlCenterViewModel viewModel)
    {
        _isUpdatingInternally = true;
        try
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }

    public void RefreshState()
    {
        if (_viewModel == null) return;
        _isUpdatingInternally = true;
        try
        {
            _ = _viewModel.RefreshStateAsync();
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }

    private void WifiTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _viewModel?.ToggleWifiCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void WifiDetails_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel?.OpenWifiSettingsCommand.Execute(null);
        e.Handled = true;
    }

    private void BluetoothTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _viewModel?.ToggleBluetoothCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void BluetoothDetails_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel?.OpenBluetoothSettingsCommand.Execute(null);
        e.Handled = true;
    }

    private void NightLightTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _viewModel?.ToggleNightLightCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void EnergySaverTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _viewModel?.ToggleEnergySaverCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void VolumeIcon_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ToggleMuteCommand.Execute(null);
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isLoaded || _isUpdatingInternally || _viewModel == null) return;
        _viewModel.OnBrightnessSliderChanged((int)Math.Round(e.NewValue));
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isLoaded || _isUpdatingInternally || _viewModel == null) return;
        _viewModel.OnVolumeSliderChanged((int)Math.Round(e.NewValue));
    }

    private void SettingsBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel?.OpenSettingsCommand.Execute(null);
        e.Handled = true;
    }

    private void MonitorBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel?.OpenAppMonitorCommand.Execute(null);
        e.Handled = true;
    }
}
