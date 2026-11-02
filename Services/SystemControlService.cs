using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VNotch.Services;

public sealed class SystemControlService
{
    private const string LogTag = "SYSTEM-CONTROL";

    #region Win32 DXVA2 Fallback for External Monitors

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    #endregion

    #region Brightness Control

    public int GetBrightness()
    {
        // 1. Try WMI (Internal laptop displays)
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"), new SelectQuery("WmiMonitorBrightness"));
            using var collection = searcher.Get();
            foreach (ManagementObject obj in collection)
            {
                var cur = obj["CurrentBrightness"];
                if (cur != null)
                {
                    return Math.Clamp(Convert.ToInt32(cur), 0, 100);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"WMI GetBrightness failed: {ex.Message}");
        }

        // 2. Try DXVA2 (External monitors)
        try
        {
            IntPtr hDesktop = GetDesktopWindow();
            IntPtr hMonitor = MonitorFromWindow(hDesktop, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (hMonitor != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
            {
                var monitors = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                {
                    try
                    {
                        if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out _, out uint current, out _))
                        {
                            return Math.Clamp((int)current, 0, 100);
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, monitors);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"DXVA2 GetBrightness failed: {ex.Message}");
        }

        return 50;
    }

    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        // 1. Try WMI
        bool wmiSuccess = false;
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"), new SelectQuery("WmiMonitorBrightnessMethods"));
            using var collection = searcher.Get();
            foreach (ManagementObject obj in collection)
            {
                obj.InvokeMethod("WmiSetBrightness", new object[] { 1, (uint)percent });
                wmiSuccess = true;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"WMI SetBrightness failed: {ex.Message}");
        }

        // 2. Try DXVA2
        if (!wmiSuccess)
        {
            try
            {
                IntPtr hDesktop = GetDesktopWindow();
                IntPtr hMonitor = MonitorFromWindow(hDesktop, 2);
                if (hMonitor != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    {
                        try
                        {
                            SetMonitorBrightness(monitors[0].hPhysicalMonitor, (uint)percent);
                        }
                        finally
                        {
                            DestroyPhysicalMonitors(count, monitors);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Debug(LogTag, () => $"DXVA2 SetBrightness failed: {ex.Message}");
            }
        }
    }

    #endregion

    #region Wi-Fi & Network

    public string GetWifiSsid()
    {
        try
        {
            var profile = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile != null)
            {
                if (profile.IsWlanConnectionProfile)
                {
                    var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                    if (!string.IsNullOrWhiteSpace(ssid)) return ssid;
                }
                if (!string.IsNullOrWhiteSpace(profile.ProfileName))
                {
                    return profile.ProfileName;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"GetWifiSsid failed: {ex.Message}");
        }

        // Fallback: check network interfaces
        try
        {
            var wifiInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                                      ni.OperationalStatus == OperationalStatus.Up);
            if (wifiInterface != null)
            {
                return wifiInterface.Name;
            }
        }
        catch { }

        return "Disconnected";
    }

    public async Task<bool> IsWifiEnabledAsync()
    {
        try
        {
            var radios = await global::Windows.Devices.Radios.Radio.GetRadiosAsync();
            var wifiRadio = radios.FirstOrDefault(r => r.Kind == global::Windows.Devices.Radios.RadioKind.WiFi);
            if (wifiRadio != null)
            {
                return wifiRadio.State == global::Windows.Devices.Radios.RadioState.On;
            }
        }
        catch { }

        var wifiInterface = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        return wifiInterface != null && wifiInterface.OperationalStatus == OperationalStatus.Up;
    }

    public async Task ToggleWifiAsync()
    {
        try
        {
            var radios = await global::Windows.Devices.Radios.Radio.GetRadiosAsync();
            var wifiRadio = radios.FirstOrDefault(r => r.Kind == global::Windows.Devices.Radios.RadioKind.WiFi);
            if (wifiRadio != null)
            {
                var targetState = wifiRadio.State == global::Windows.Devices.Radios.RadioState.On 
                    ? global::Windows.Devices.Radios.RadioState.Off 
                    : global::Windows.Devices.Radios.RadioState.On;
                await wifiRadio.SetStateAsync(targetState);
                return;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"ToggleWifi failed: {ex.Message}");
        }

        OpenWifiSettings();
    }

    public void OpenWifiSettings()
    {
        LaunchUri("ms-settings:network-wifi");
    }

    #endregion

    #region Bluetooth

    public async Task<bool> IsBluetoothEnabledAsync()
    {
        try
        {
            var radios = await global::Windows.Devices.Radios.Radio.GetRadiosAsync();
            var btRadio = radios.FirstOrDefault(r => r.Kind == global::Windows.Devices.Radios.RadioKind.Bluetooth);
            if (btRadio != null)
            {
                return btRadio.State == global::Windows.Devices.Radios.RadioState.On;
            }
        }
        catch { }

        return false;
    }

    public async Task ToggleBluetoothAsync()
    {
        try
        {
            var radios = await global::Windows.Devices.Radios.Radio.GetRadiosAsync();
            var btRadio = radios.FirstOrDefault(r => r.Kind == global::Windows.Devices.Radios.RadioKind.Bluetooth);
            if (btRadio != null)
            {
                var targetState = btRadio.State == global::Windows.Devices.Radios.RadioState.On 
                    ? global::Windows.Devices.Radios.RadioState.Off 
                    : global::Windows.Devices.Radios.RadioState.On;
                await btRadio.SetStateAsync(targetState);
                return;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"ToggleBluetooth failed: {ex.Message}");
        }

        OpenBluetoothSettings();
    }

    public void OpenBluetoothSettings()
    {
        LaunchUri("ms-settings:bluetooth");
    }

    #endregion

    #region Night Light

    public bool IsNightLightActive()
    {
        try
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key != null)
            {
                var val = key.GetValue("Data") as byte[];
                if (val != null && val.Length > 18)
                {
                    return val[18] == 0x15;
                }
            }
        }
        catch { }

        return false;
    }

    public void OpenNightLightSettings()
    {
        LaunchUri("ms-settings:nightlight");
    }

    #endregion

    #region Energy Saver

    public void OpenEnergySaverSettings()
    {
        LaunchUri("ms-settings:batterysaver");
    }

    #endregion

    #region System Settings

    public void OpenWindowsSettings()
    {
        LaunchUri("ms-settings:");
    }

    public void OpenTaskbarSettings()
    {
        LaunchUri("ms-settings:taskbar");
    }

    private static void LaunchUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"Failed to launch URI '{uri}': {ex.Message}");
        }
    }

    #endregion
}
