using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Manages Windows Taskbar transparency (adapted from TranslucentTB) and freeze/suppression modes
/// synchronized with Pinn-Notch Dock.
/// </summary>
public sealed class TaskbarIntegrationService : IDisposable
{
    private static readonly object _syncLock = new();
    private static TaskbarIntegrationService? _instance;
    public static TaskbarIntegrationService? Instance => _instance;

    private static readonly bool _isWindows11 = Environment.OSVersion.Version.Build >= 22000;
    private static Process? _managedTranslucentTbProcess;

    private readonly DispatcherTimer _dynamicTimer;
    private NotchSettings? _currentSettings;
    private RECT? _savedOriginalWorkArea;
    private bool _isWorkAreaExpanded;
    private bool _isDisposed;
    private bool _lastMaximizedState;

    #region Win32 API Definitions

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_RETURN = 0x0D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;

    private const uint SPI_GETWORKAREA = 0x0030;
    private const uint SPI_SETWORKAREA = 0x002F;
    private const uint SPIF_SENDCHANGE = 0x0002;

    private const uint ABM_SETSTATE = 0x0000000A;
    private const int ABS_AUTOHIDE = 0x00000001;
    private const int ABS_ALWAYSONTOP = 0x00000002;

    private const int WCA_ACCENT_POLICY = 19;

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_NORMAL = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    #endregion

    public TaskbarIntegrationService()
    {
        _instance = this;
        _dynamicTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _dynamicTimer.Tick += OnDynamicCheckTick;
    }

    /// <summary>
    /// Applies taskbar transparency and freeze settings.
    /// </summary>
    public void ApplySettings(NotchSettings settings)
    {
        if (settings == null) return;
        lock (_syncLock)
        {
            _currentSettings = settings;

            // Handle Freeze Taskbar
            ApplyFreezeSettings(settings);

            // Handle Transparent Taskbar
            ApplyTransparencySettings(settings);

            // Manage dynamic watcher timer (runs for dynamic transparency or freeze taskbar suppression)
            bool needsTimer = (settings.EnableTransparentTaskbar && settings.TaskbarDynamicMaximized) ||
                              settings.EnableFreezeTaskbar;
            if (needsTimer)
            {
                if (!_dynamicTimer.IsEnabled) _dynamicTimer.Start();
            }
            else
            {
                if (_dynamicTimer.IsEnabled) _dynamicTimer.Stop();
            }
        }
    }

    private void ApplyFreezeSettings(NotchSettings settings)
    {
        var taskbars = GetAllTaskbarWindows();
        if (taskbars.Count == 0) return;

        if (settings.EnableFreezeTaskbar)
        {
            switch (settings.TaskbarFreezeMode)
            {
                case "HideSuppress":
                    // Expand work area to full display height so apps and dock own the whole screen
                    ExpandDesktopWorkArea();

                    // Hide taskbars
                    foreach (var hwnd in taskbars)
                    {
                        if (IsWindowVisible(hwnd))
                        {
                            ShowWindow(hwnd, SW_HIDE);
                        }
                    }
                    break;

                case "ClickThrough":
                    // Restore visibility if previously hidden
                    RestoreDesktopWorkArea();
                    foreach (var hwnd in taskbars)
                    {
                        if (!IsWindowVisible(hwnd))
                        {
                            ShowWindow(hwnd, SW_SHOW);
                        }
                        // Add WS_EX_TRANSPARENT so clicks pass straight to Dock / Desktop
                        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                        long val = exStyle.ToInt64();
                        if ((val & WS_EX_TRANSPARENT) == 0)
                        {
                            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(val | WS_EX_TRANSPARENT));
                        }
                    }
                    break;

                case "FreezeAutoHide":
                    RestoreDesktopWorkArea();
                    foreach (var hwnd in taskbars)
                    {
                        if (!IsWindowVisible(hwnd))
                        {
                            ShowWindow(hwnd, SW_SHOW);
                        }
                        ClearClickThrough(hwnd);
                        SetAutoHideState(hwnd, true);
                    }
                    break;
            }
        }
        else
        {
            // Fully unfreeze and restore
            RestoreDesktopWorkArea();
            foreach (var hwnd in taskbars)
            {
                ClearClickThrough(hwnd);
                if (!IsWindowVisible(hwnd))
                {
                    ShowWindow(hwnd, SW_SHOW);
                }
            }
        }
    }

    private void ApplyTransparencySettings(NotchSettings settings, bool forceMaximizedStyle = false)
    {
        var taskbars = GetAllTaskbarWindows();
        if (taskbars.Count == 0) return;

        // If taskbar is hidden by freeze mode, skip composition updates
        if (settings.EnableFreezeTaskbar && settings.TaskbarFreezeMode == "HideSuppress")
        {
            return;
        }

        if (!settings.EnableTransparentTaskbar)
        {
            if (_isWindows11)
            {
                StopTranslucentTb();
            }

            // Reset to default Windows composition
            foreach (var hwnd in taskbars)
            {
                SetTaskbarAccent(hwnd, AccentState.ACCENT_NORMAL, 0, 0);
                SendMessage(hwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);
            }
            return;
        }

        if (_isWindows11)
        {
            EnsureTranslucentTbRunning(settings);
        }

        string style = settings.TaskbarStyle;
        if (forceMaximizedStyle)
        {
            // When a window is maximized, switch to Blur/Tinted so icons/clock remain legible
            style = style == "Clear" ? "Acrylic" : style;
        }

        AccentState accent;
        int flags = 2;
        int color = 0;

        switch (style)
        {
            case "Clear":
                accent = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                flags = 2;
                color = 0x00000000; // Fully transparent
                break;

            case "Blur":
                accent = AccentState.ACCENT_ENABLE_BLURBEHIND;
                flags = 2;
                color = ParseColorToABGR(settings.TaskbarColor, settings.TaskbarOpacity);
                break;

            case "Acrylic":
                accent = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                flags = 0;
                color = ParseColorToABGR(settings.TaskbarColor, Math.Max(0.01, settings.TaskbarOpacity));
                break;

            case "Tinted":
            default:
                accent = AccentState.ACCENT_ENABLE_GRADIENT;
                flags = 2;
                color = ParseColorToABGR(settings.TaskbarColor, settings.TaskbarOpacity);
                break;
        }

        foreach (var hwnd in taskbars)
        {
            SetTaskbarAccent(hwnd, accent, flags, color);
        }
    }

    private void OnDynamicCheckTick(object? sender, EventArgs e)
    {
        if (_currentSettings == null) return;

        // Keep Freeze Taskbar active against Windows Shell or app redraws
        if (_currentSettings.EnableFreezeTaskbar)
        {
            ApplyFreezeSettings(_currentSettings);
        }

        if (_currentSettings.EnableTransparentTaskbar && _currentSettings.TaskbarDynamicMaximized && !_currentSettings.EnableFreezeTaskbar)
        {
            IntPtr fg = GetForegroundWindow();
            bool isMaximized = fg != IntPtr.Zero && IsZoomed(fg);

            if (isMaximized != _lastMaximizedState)
            {
                _lastMaximizedState = isMaximized;
                ApplyTransparencySettings(_currentSettings, forceMaximizedStyle: isMaximized);
            }
        }
    }

    private static void SetTaskbarAccent(IntPtr hwnd, AccentState state, int flags, int color)
    {
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = (int)state,
                AccentFlags = flags,
                GradientColor = color,
                AnimationId = 0
            };

            int size = Marshal.SizeOf(policy);
            IntPtr pPolicy = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(policy, pPolicy, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = pPolicy,
                SizeOfData = size
            };

            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(pPolicy);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("TASKBAR", $"SetTaskbarAccent failed on 0x{hwnd:X}: {ex.Message}");
        }
    }

    private static void ClearClickThrough(IntPtr hwnd)
    {
        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long val = exStyle.ToInt64();
        if ((val & WS_EX_TRANSPARENT) != 0)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(val & ~WS_EX_TRANSPARENT));
        }
    }

    private static void SetAutoHideState(IntPtr hwnd, bool autoHide)
    {
        try
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                hWnd = hwnd,
                lParam = (IntPtr)(autoHide ? ABS_AUTOHIDE : ABS_ALWAYSONTOP)
            };
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("TASKBAR", $"SetAutoHideState failed: {ex.Message}");
        }
    }

    private void ExpandDesktopWorkArea()
    {
        if (_isWorkAreaExpanded) return;
        try
        {
            var currentWork = new RECT();
            if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref currentWork, 0))
            {
                _savedOriginalWorkArea ??= currentWork;

                // Expand work area to full primary screen bounds
                int screenWidth = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                int screenHeight = (int)System.Windows.SystemParameters.PrimaryScreenHeight;

                var fullScreen = new RECT
                {
                    Left = 0,
                    Top = 0,
                    Right = screenWidth,
                    Bottom = screenHeight
                };

                SystemParametersInfo(SPI_SETWORKAREA, 0, ref fullScreen, SPIF_SENDCHANGE);
                _isWorkAreaExpanded = true;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("TASKBAR", $"ExpandDesktopWorkArea failed: {ex.Message}");
        }
    }

    private void RestoreDesktopWorkArea()
    {
        if (!_isWorkAreaExpanded || _savedOriginalWorkArea == null) return;
        try
        {
            var original = _savedOriginalWorkArea.Value;
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref original, SPIF_SENDCHANGE);
            _isWorkAreaExpanded = false;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("TASKBAR", $"RestoreDesktopWorkArea failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts a hex color string (#RRGGBB or #AARRGGBB) and opacity into an ABGR integer required by SetWindowCompositionAttribute.
    /// </summary>
    public static int ParseColorToABGR(string? hexColor, double opacity)
    {
        byte a = (byte)Math.Clamp((int)(opacity * 255.0), 0, 255);
        byte r = 0;
        byte g = 0;
        byte b = 0;

        if (!string.IsNullOrWhiteSpace(hexColor))
        {
            string hex = hexColor.Trim().TrimStart('#');
            try
            {
                if (hex.Length == 6)
                {
                    r = Convert.ToByte(hex.Substring(0, 2), 16);
                    g = Convert.ToByte(hex.Substring(2, 2), 16);
                    b = Convert.ToByte(hex.Substring(4, 2), 16);
                }
                else if (hex.Length == 8)
                {
                    byte customA = Convert.ToByte(hex.Substring(0, 2), 16);
                    r = Convert.ToByte(hex.Substring(2, 2), 16);
                    g = Convert.ToByte(hex.Substring(4, 2), 16);
                    b = Convert.ToByte(hex.Substring(6, 2), 16);
                    if (opacity >= 0.99) a = customA;
                }
            }
            catch
            {
                // Fallback to black
            }
        }

        // Format is ABGR: (A << 24) | (B << 16) | (G << 8) | R
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>
    /// Finds primary taskbar (Shell_TrayWnd) and all secondary taskbars (Shell_SecondaryTrayWnd).
    /// </summary>
    public static List<IntPtr> GetAllTaskbarWindows()
    {
        var list = new List<IntPtr>();
        IntPtr primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
        {
            list.Add(primary);
        }

        EnumWindows((hWnd, lParam) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(hWnd, sb, 64);
            if (sb.ToString() == "Shell_SecondaryTrayWnd")
            {
                list.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// Restores all taskbar windows to standard visible and default composition state.
    /// </summary>
    public void RestoreTaskbars()
    {
        try
        {
            _dynamicTimer.Stop();
            RestoreDesktopWorkArea();

            if (_isWindows11)
            {
                StopTranslucentTb();
            }

            var taskbars = GetAllTaskbarWindows();
            foreach (var hwnd in taskbars)
            {
                ClearClickThrough(hwnd);

                if (!IsWindowVisible(hwnd))
                {
                    ShowWindow(hwnd, SW_SHOW);
                }

                SetTaskbarAccent(hwnd, AccentState.ACCENT_NORMAL, 0, 0);
                SendMessage(hwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("TASKBAR", ex, "Failed to restore taskbars on exit");
        }
    }

    #region TranslucentTB Engine Integration

    /// <summary>
    /// Locates the bundled or installed TranslucentTB executable.
    /// </summary>
    public static string? GetTranslucentTbExePath()
    {
        // 1. Output directory Tools\TranslucentTB\TranslucentTB.exe
        string candidate1 = Path.Combine(AppContext.BaseDirectory, "Tools", "TranslucentTB", "TranslucentTB.exe");
        if (File.Exists(candidate1)) return candidate1;

        // 2. Development source folder Tools\TranslucentTB\TranslucentTB.exe
        try
        {
            string candidate2 = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Tools", "TranslucentTB", "TranslucentTB.exe"));
            if (File.Exists(candidate2)) return candidate2;
        }
        catch { }

        // 3. Current working directory Tools\TranslucentTB\TranslucentTB.exe
        try
        {
            string candidate3 = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Tools", "TranslucentTB", "TranslucentTB.exe"));
            if (File.Exists(candidate3)) return candidate3;
        }
        catch { }

        // 4. Installed via Store / WindowsApps
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string storeCandidate = Path.Combine(localAppData, "Microsoft", "WindowsApps", "TranslucentTB.exe");
            if (File.Exists(storeCandidate)) return storeCandidate;
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Writes or updates settings.json for TranslucentTB matching Pinn-Notch settings.
    /// </summary>
    public static void UpdateTranslucentTbConfig(string exePath, NotchSettings settings)
    {
        try
        {
            string dir = Path.GetDirectoryName(exePath) ?? "";
            string configPath = Path.Combine(dir, "settings.json");

            string accent = settings.TaskbarStyle switch
            {
                "Clear" => "clear",
                "Blur" => "blur",
                "Acrylic" => "acrylic",
                "Tinted" => "opaque",
                _ => "clear"
            };

            byte a = (byte)Math.Clamp((int)(settings.TaskbarOpacity * 255.0), 0, 255);
            string colorHex = "#00000000";
            if (!string.IsNullOrWhiteSpace(settings.TaskbarColor))
            {
                string hex = settings.TaskbarColor.Trim().TrimStart('#');
                if (hex.Length == 6)
                {
                    colorHex = $"#{a:X2}{hex}";
                }
                else if (hex.Length == 8)
                {
                    colorHex = $"#{hex}";
                }
            }
            else if (accent != "clear")
            {
                colorHex = $"#{a:X2}000000";
            }

            var configDict = new Dictionary<string, object>
            {
                ["$schema"] = "https://TranslucentTB.github.io/settings.schema.json",
                ["desktop_appearance"] = new Dictionary<string, object>
                {
                    ["accent"] = accent,
                    ["color"] = colorHex,
                    ["show_peek"] = false,
                    ["show_line"] = false
                },
                ["maximized_window_appearance"] = new Dictionary<string, object>
                {
                    ["enabled"] = settings.TaskbarDynamicMaximized,
                    ["accent"] = settings.TaskbarDynamicMaximized ? (accent == "clear" ? "blur" : accent) : accent,
                    ["color"] = settings.TaskbarDynamicMaximized ? "#33000000" : colorHex,
                    ["show_peek"] = false,
                    ["show_line"] = false
                },
                ["start_opened_appearance"] = new Dictionary<string, object>
                {
                    ["enabled"] = false
                },
                ["search_opened_appearance"] = new Dictionary<string, object>
                {
                    ["enabled"] = false
                },
                ["task_view_opened_appearance"] = new Dictionary<string, object>
                {
                    ["enabled"] = false
                },
                ["battery_saver_appearance"] = new Dictionary<string, object>
                {
                    ["enabled"] = false
                },
                ["hide_tray"] = false,
                ["disable_saving"] = false,
                ["verbosity"] = "warn"
            };

            string json = JsonSerializer.Serialize(configDict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json, Encoding.UTF8);
            RuntimeLog.Info("TASKBAR", $"Updated TranslucentTB configuration: accent={accent}, color={colorHex}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("TASKBAR", $"Failed to update TranslucentTB config: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures TranslucentTB is running and configured for Windows 11.
    /// </summary>
    private static void EnsureTranslucentTbRunning(NotchSettings settings)
    {
        string? exePath = GetTranslucentTbExePath();
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            RuntimeLog.Warn("TASKBAR", "TranslucentTB binary not found on disk.");
            return;
        }

        UpdateTranslucentTbConfig(exePath, settings);

        var existing = Process.GetProcessesByName("TranslucentTB");
        if (existing.Length > 0)
        {
            _managedTranslucentTbProcess = existing[0];
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute = true,
                CreateNoWindow = false
            };
            _managedTranslucentTbProcess = Process.Start(psi);
            RuntimeLog.Info("TASKBAR", $"Started TranslucentTB engine (PID: {_managedTranslucentTbProcess?.Id})");

            // Auto-dismiss the one-time welcome dialog if it appears
            AutoDismissWelcomeDialog();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("TASKBAR", ex, "Failed to launch TranslucentTB engine");
        }
    }

    /// <summary>
    /// Dismisses TranslucentTB first-run welcome dialog if present.
    /// </summary>
    private static void AutoDismissWelcomeDialog()
    {
        Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(350);
                IntPtr w = FindWindow(null, "Welcome to TranslucentTB!");
                if (w != IntPtr.Zero)
                {
                    IntPtr core = FindWindowEx(w, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                    if (core != IntPtr.Zero)
                    {
                        PostMessage(core, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
                        PostMessage(core, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
                        RuntimeLog.Info("TASKBAR", "Auto-dismissed TranslucentTB welcome dialog.");
                    }
                    break;
                }
            }
        });
    }

    /// <summary>
    /// Stops TranslucentTB if it is running from our managed directory.
    /// </summary>
    public static void StopTranslucentTb()
    {
        try
        {
            var procs = Process.GetProcessesByName("TranslucentTB");
            foreach (var p in procs)
            {
                try
                {
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { }

                    // Terminate if managed by Pinn-Notch
                    if (string.IsNullOrEmpty(path) ||
                        path.IndexOf("Pinn-Notch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf("V-Notch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf("TranslucentTB", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        p.Kill();
                        RuntimeLog.Info("TASKBAR", $"Stopped TranslucentTB PID {p.Id}");
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("TASKBAR", $"Could not stop TranslucentTB PID {p.Id}: {ex.Message}");
                }
            }
            _managedTranslucentTbProcess = null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("TASKBAR", $"StopTranslucentTb failed: {ex.Message}");
        }
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        RestoreTaskbars();
    }
}
