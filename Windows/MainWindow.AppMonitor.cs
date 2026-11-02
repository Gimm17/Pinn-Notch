using System;
using System.Windows;
using VNotch.Windows;

namespace VNotch;

public partial class MainWindow
{
    private AppMonitorWindow? _appMonitorWindow;

    public void ToggleAppMonitor(bool? forceState = null)
    {
        bool show = forceState ?? (_appMonitorWindow == null || !_appMonitorWindow.IsVisible);
        if (show)
        {
            if (_appMonitorWindow == null)
            {
                _appMonitorWindow = new AppMonitorWindow();
                _appMonitorWindow.Closed += (_, _) => _appMonitorWindow = null;
            }
            if (_appMonitorWindow.WindowState == WindowState.Minimized)
            {
                _appMonitorWindow.WindowState = WindowState.Normal;
            }
            _appMonitorWindow.Show();
            _appMonitorWindow.Activate();
            _appMonitorWindow.Focus();
        }
        else
        {
            _appMonitorWindow?.Hide();
        }
    }

    private void OpenAppMonitor_Click(object sender, RoutedEventArgs e)
    {
        ToggleAppMonitor(true);
    }
}
