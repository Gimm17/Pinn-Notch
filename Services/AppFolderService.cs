using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using VNotch.Models;

namespace VNotch.Services;

public sealed class AppFolderService
{
    private static readonly object _lock = new();
    private static AppFolderService? _instance;
    public static AppFolderService Instance => _instance ??= new AppFolderService();

    private readonly string _configFilePath;
    private AppFoldersConfig _config = new();

    public event Action? FoldersChanged;

    public AppFolderService()
    {
        _instance = this;
        string dir = AppPaths.LocalAppDataRoot;
        Directory.CreateDirectory(dir);
        _configFilePath = Path.Combine(dir, "app-folders.json");
        Load();
    }

    public IReadOnlyList<AppFolder> Folders
    {
        get
        {
            lock (_lock)
            {
                return _config.Folders.ToArray();
            }
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath);
                    var loaded = JsonSerializer.Deserialize<AppFoldersConfig>(json);
                    if (loaded != null && loaded.Folders.Count > 0)
                    {
                        _config = loaded;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("APP_FOLDERS", $"Failed to load app folders: {ex.Message}");
            }

            // Fallback to default folders
            _config = CreateDefaultFolders();
            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_config, options);
                File.WriteAllText(_configFilePath, json);
                FoldersChanged?.Invoke();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("APP_FOLDERS", ex, "Failed to save app folders configuration");
            }
        }
    }

    public void AddFolder(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            _config.Folders.Add(new AppFolder { Name = name.Trim() });
            Save();
        }
    }

    public void DeleteFolder(string folderId)
    {
        lock (_lock)
        {
            _config.Folders.RemoveAll(f => f.Id == folderId);
            Save();
        }
    }

    public void RenameFolder(string folderId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        lock (_lock)
        {
            var folder = _config.Folders.Find(f => f.Id == folderId);
            if (folder != null)
            {
                folder.Name = newName.Trim();
                Save();
            }
        }
    }

    public void AddAppToFolder(string folderId, string name, string targetPath, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return;
        lock (_lock)
        {
            var folder = _config.Folders.Find(f => f.Id == folderId);
            if (folder != null)
            {
                folder.Apps.Add(new AppItem
                {
                    Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(targetPath) : name.Trim(),
                    TargetPath = targetPath.Trim(),
                    Arguments = arguments
                });
                Save();
            }
        }
    }

    public void RemoveAppFromFolder(string folderId, string appId)
    {
        lock (_lock)
        {
            var folder = _config.Folders.Find(f => f.Id == folderId);
            if (folder != null)
            {
                folder.Apps.RemoveAll(a => a.Id == appId);
                Save();
            }
        }
    }

    public static ImageSource? ResolveAppIcon(string targetPath, bool small = false)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;

        // If target is web URL, return null (XAML will use web glyph)
        if (targetPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            targetPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(targetPath);
            if (File.Exists(expanded))
            {
                return FileIconProvider.GetFileIcon(expanded) ?? FileIconProvider.GetAppIcon(expanded, small);
            }

            // Check system PATH
            string? inPath = FindInPath(expanded);
            if (!string.IsNullOrEmpty(inPath) && File.Exists(inPath))
            {
                return FileIconProvider.GetFileIcon(inPath) ?? FileIconProvider.GetAppIcon(inPath, small);
            }
        }
        catch { }

        return null;
    }

    public static void LaunchApp(AppItem app, double? anchorRight = null, double? anchorTop = null)
    {
        if (string.IsNullOrWhiteSpace(app.TargetPath)) return;
        try
        {
            string target = Environment.ExpandEnvironmentVariables(app.TargetPath);

            HashSet<IntPtr>? beforeWindows = null;
            if (anchorRight.HasValue && anchorTop.HasValue)
            {
                beforeWindows = new HashSet<IntPtr>(GetVisibleWindows());
            }

            var psi = new ProcessStartInfo
            {
                FileName = target,
                Arguments = app.Arguments ?? "",
                UseShellExecute = true
            };
            Process.Start(psi);
            RuntimeLog.Info("APP_FOLDERS", $"Launched app: {app.Name} ({target})");

            if (anchorRight.HasValue && anchorTop.HasValue && beforeWindows != null)
            {
                double ar = anchorRight.Value;
                double at = anchorTop.Value;
                string exeName = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();

                Task.Run(async () =>
                {
                    for (int i = 0; i < 35; i++)
                    {
                        await Task.Delay(80);
                        var currentWindows = GetVisibleWindows();
                        foreach (var hwnd in currentWindows)
                        {
                            if (!beforeWindows.Contains(hwnd))
                            {
                                Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                                bool isMatch = false;
                                if (pid != 0)
                                {
                                    try
                                    {
                                        using var p = Process.GetProcessById((int)pid);
                                        if (p.ProcessName.ToLowerInvariant().Contains(exeName)) isMatch = true;
                                    }
                                    catch { }
                                }

                                if (isMatch || string.IsNullOrEmpty(exeName))
                                {
                                    Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);

                                    double screenWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
                                    double screenHeight = System.Windows.SystemParameters.PrimaryScreenHeight;

                                    if (Win32Interop.GetWindowRect(hwnd, out var rect))
                                    {
                                        int w = rect.Right - rect.Left;
                                        int h = rect.Bottom - rect.Top;

                                        if (w <= 120 || w >= screenWidth) w = (int)Math.Min(1020, screenWidth * 0.72);
                                        if (h <= 120 || h >= screenHeight) h = (int)Math.Min(680, screenHeight * 0.72);

                                        int targetLeft = Math.Clamp((int)(ar - w), 16, (int)(screenWidth - w - 16));
                                        int targetTop = Math.Clamp((int)(at - h - 12), 16, (int)(screenHeight - h - 12));

                                        Win32Interop.SetWindowPos(hwnd, IntPtr.Zero, targetLeft, targetTop, w, h, Win32Interop.SWP_NOZORDER | Win32Interop.SWP_SHOWWINDOW);
                                        Win32Interop.SetForegroundWindow(hwnd);
                                    }
                                    return;
                                }
                            }
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("APP_FOLDERS", ex, $"Failed to launch {app.Name}");
        }
    }

    private static List<IntPtr> GetVisibleWindows()
    {
        var list = new List<IntPtr>();
        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (Win32Interop.IsWindowVisible(hwnd))
            {
                list.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string? FindInPath(string fileName)
    {
        if (File.Exists(fileName)) return fileName;
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
                if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(full + ".exe")) return full + ".exe";
                }
            }
            catch { }
        }
        return null;
    }

    private static AppFoldersConfig CreateDefaultFolders()
    {
        var config = new AppFoldersConfig();

        // 1. Browser & Web
        var fBrowser = new AppFolder { Name = "Browser & Web" };
        AddIfFoundOrCommand(fBrowser, "Google Chrome", "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        AddIfFoundOrCommand(fBrowser, "Microsoft Edge", "msedge.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
        AddIfFoundOrCommand(fBrowser, "Brave Browser", "brave.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe"));
        fBrowser.Apps.Add(new AppItem { Name = "YouTube", TargetPath = "https://youtube.com" });
        config.Folders.Add(fBrowser);

        // 2. Media & Musik
        var fMedia = new AppFolder { Name = "Media & Musik" };
        AddIfFoundOrCommand(fMedia, "Spotify", "spotify.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Spotify\Spotify.exe"));
        AddIfFoundOrCommand(fMedia, "VLC Player", "vlc.exe", @"C:\Program Files\VideoLAN\VLC\vlc.exe");
        fMedia.Apps.Add(new AppItem { Name = "Windows Media", TargetPath = "wmplayer.exe" });
        fMedia.Apps.Add(new AppItem { Name = "SoundCloud", TargetPath = "https://soundcloud.com" });
        config.Folders.Add(fMedia);

        // 3. Kerja & Dev
        var fDev = new AppFolder { Name = "Kerja & Dev" };
        AddIfFoundOrCommand(fDev, "VS Code", "code.cmd", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"));
        fDev.Apps.Add(new AppItem { Name = "Terminal", TargetPath = "wt.exe" });
        fDev.Apps.Add(new AppItem { Name = "Notepad", TargetPath = "notepad.exe" });
        fDev.Apps.Add(new AppItem { Name = "File Explorer", TargetPath = "explorer.exe" });
        config.Folders.Add(fDev);

        // 4. Alat & Utilitas
        var fTools = new AppFolder { Name = "Alat & Utilitas" };
        fTools.Apps.Add(new AppItem { Name = "Task Manager", TargetPath = "taskmgr.exe" });
        fTools.Apps.Add(new AppItem { Name = "Kalkulator", TargetPath = "calc.exe" });
        fTools.Apps.Add(new AppItem { Name = "Snipping Tool", TargetPath = "snippingtool.exe" });
        fTools.Apps.Add(new AppItem { Name = "Pengaturan", TargetPath = "ms-settings:" });
        config.Folders.Add(fTools);

        return config;
    }

    private static void AddIfFoundOrCommand(AppFolder folder, string name, string cmdFallback, string fullPath)
    {
        string target = File.Exists(fullPath) ? fullPath : cmdFallback;
        folder.Apps.Add(new AppItem { Name = name, TargetPath = target });
    }
}
