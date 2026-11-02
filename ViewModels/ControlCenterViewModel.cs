using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class ControlCenterViewModel : ObservableObject
{
    private readonly SystemControlService _systemControl;
    private readonly IVolumeService _volumeService;
    private readonly IBatteryService _batteryService;
    private readonly Dispatcher _dispatcher;
    private readonly Debouncer _brightnessDebouncer;

    [ObservableProperty]
    private string _wifiSsid = "Wi-Fi";

    [ObservableProperty]
    private bool _isWifiActive = false;

    [ObservableProperty]
    private bool _isBluetoothActive = false;

    [ObservableProperty]
    private bool _isNightLightActive = false;

    [ObservableProperty]
    private bool _isEnergySaverActive = false;

    [ObservableProperty]
    private int _brightness = 0;

    [ObservableProperty]
    private int _volume = 0;

    private bool _isRefreshing = false;

    [ObservableProperty]
    private bool _isMuted = false;

    [ObservableProperty]
    private string _batteryPercentText = "100%";

    [ObservableProperty]
    private double _batteryFillRatio = 1.0;

    [ObservableProperty]
    private bool _isCharging = false;

    [ObservableProperty]
    private bool _hasBattery = true;

    [ObservableProperty]
    private string _volumeIconText = "\uE767";

    public Action? RequestOpenAppMonitor { get; set; }

    public ControlCenterViewModel(SystemControlService systemControl, IVolumeService volumeService, IBatteryService batteryService)
    {
        _systemControl = systemControl;
        _volumeService = volumeService;
        _batteryService = batteryService;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _brightnessDebouncer = new Debouncer(TimeSpan.FromMilliseconds(50));

        // Read CURRENT live system values immediately so app NEVER overrides them on startup
        try
        {
            if (_volumeService.IsAvailable)
            {
                float vol = _volumeService.GetVolume();
                _volume = Math.Clamp((int)Math.Round(vol * 100), 0, 100);
                _isMuted = _volumeService.GetMute();
                UpdateVolumeIcon(_volume, _isMuted);
            }
        }
        catch { }

        try
        {
            int b = _systemControl.GetBrightness();
            _brightness = Math.Clamp(b, 0, 100);
        }
        catch { }

        try
        {
            var batInfo = _batteryService.GetBatteryInfo();
            _hasBattery = batInfo.HasBattery;
            _batteryPercentText = batInfo.GetPercentageText();
            _batteryFillRatio = batInfo.FillRatio;
            _isCharging = batInfo.IsPowerConnected;
            _isEnergySaverActive = batInfo.IsBatterySaver;
            _isNightLightActive = _systemControl.IsNightLightActive();
        }
        catch { }

        _ = Task.Run(async () =>
        {
            try
            {
                string ssid = _systemControl.GetWifiSsid();
                bool wifiOn = await _systemControl.IsWifiEnabledAsync();
                bool btOn = await _systemControl.IsBluetoothEnabledAsync();
                _dispatcher.Invoke(() =>
                {
                    WifiSsid = !string.IsNullOrWhiteSpace(ssid) && ssid != "Disconnected" ? ssid : "Wi-Fi";
                    IsWifiActive = wifiOn;
                    IsBluetoothActive = btOn;
                });
            }
            catch { }
        });
    }

    public async Task RefreshStateAsync()
    {
        _isRefreshing = true;
        try
        {
        // 1. Brightness
        int curBrightness = await Task.Run(() => _systemControl.GetBrightness());
        _dispatcher.Invoke(() => Brightness = curBrightness);

        // 2. Volume
        if (_volumeService.IsAvailable)
        {
            float volRatio = _volumeService.GetVolume();
            bool mute = _volumeService.GetMute();
            _dispatcher.Invoke(() =>
            {
                Volume = (int)Math.Round(volRatio * 100);
                IsMuted = mute;
                UpdateVolumeIcon(Volume, IsMuted);
            });
        }

        // 3. Wi-Fi
        string ssid = await Task.Run(() => _systemControl.GetWifiSsid());
        bool wifiOn = await _systemControl.IsWifiEnabledAsync();
        _dispatcher.Invoke(() =>
        {
            WifiSsid = !string.IsNullOrWhiteSpace(ssid) && ssid != "Disconnected" ? ssid : "Wi-Fi";
            IsWifiActive = wifiOn;
        });

        // 4. Bluetooth
        bool btOn = await _systemControl.IsBluetoothEnabledAsync();
        _dispatcher.Invoke(() => IsBluetoothActive = btOn);

        // 5. Night Light
        bool nlOn = _systemControl.IsNightLightActive();
        _dispatcher.Invoke(() => IsNightLightActive = nlOn);

        // 6. Battery
        var batInfo = _batteryService.GetBatteryInfo();
        _dispatcher.Invoke(() =>
        {
            HasBattery = batInfo.HasBattery;
            BatteryPercentText = batInfo.GetPercentageText();
            BatteryFillRatio = batInfo.FillRatio;
            IsCharging = batInfo.IsPowerConnected;
            IsEnergySaverActive = batInfo.IsBatterySaver;
        });
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public void OnBrightnessSliderChanged(int value)
    {
        if (_isRefreshing) return;
        int clamped = Math.Clamp(value, 0, 100);
        Brightness = clamped;
        _brightnessDebouncer.Debounce(() =>
        {
            Task.Run(() => _systemControl.SetBrightness(clamped));
        });
    }

    public void OnVolumeSliderChanged(int value)
    {
        if (_isRefreshing) return;
        int clamped = Math.Clamp(value, 0, 100);
        Volume = clamped;
        UpdateVolumeIcon(Volume, IsMuted);
        if (_volumeService.IsAvailable)
        {
            _volumeService.SetVolume(clamped / 100f);
        }
    }

    [RelayCommand]
    public void ToggleMute()
    {
        if (!_volumeService.IsAvailable) return;
        _volumeService.ToggleMute();
        IsMuted = _volumeService.GetMute();
        UpdateVolumeIcon(Volume, IsMuted);
    }

    private void UpdateVolumeIcon(int vol, bool muted)
    {
        if (muted || vol == 0) VolumeIconText = "\uE74F"; // Volume mute glyph
        else if (vol < 33) VolumeIconText = "\uE992"; // Low volume
        else if (vol < 66) VolumeIconText = "\uE993"; // Mid volume
        else VolumeIconText = "\uE767"; // High volume
    }

    [RelayCommand]
    public async Task ToggleWifi()
    {
        await _systemControl.ToggleWifiAsync();
        await Task.Delay(300);
        await RefreshStateAsync();
    }

    [RelayCommand]
    public void OpenWifiSettings()
    {
        _systemControl.OpenWifiSettings();
    }

    [RelayCommand]
    public async Task ToggleBluetooth()
    {
        await _systemControl.ToggleBluetoothAsync();
        await Task.Delay(300);
        await RefreshStateAsync();
    }

    [RelayCommand]
    public void OpenBluetoothSettings()
    {
        _systemControl.OpenBluetoothSettings();
    }

    [RelayCommand]
    public void ToggleNightLight()
    {
        _systemControl.OpenNightLightSettings();
    }

    [RelayCommand]
    public void ToggleEnergySaver()
    {
        _systemControl.OpenEnergySaverSettings();
    }

    [RelayCommand]
    public void OpenSettings()
    {
        _systemControl.OpenWindowsSettings();
    }

    [RelayCommand]
    public void OpenAppMonitor()
    {
        RequestOpenAppMonitor?.Invoke();
    }
}
