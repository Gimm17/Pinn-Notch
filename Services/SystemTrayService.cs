#pragma warning disable S101, CA1707, IDE1006 // Win32/COM struct and API naming conventions

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Win32;

namespace VNotch.Services;

public sealed class TrayIconItem
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public bool IsPromoted { get; init; }
    public bool IsOwnProcess { get; init; }
    public Guid? IconGuid { get; init; }
    public uint? Uid { get; init; }
    public IntPtr OwnerHwnd { get; init; }
    public ImageSource? Icon { get; init; }
    public int OrderIndex { get; init; } = int.MaxValue;
}

internal static unsafe class SystemTrayService
{
    private const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint COINIT_MULTITHREADED = 0x0;
    private const uint TreeScope_Descendants = 4;
    private const int UIA_InvokePatternId = 10000;
    private const int UIA_ButtonControlTypeId = 50000;
    private const uint LWA_ALPHA = 0x00000002;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_RESTORE = 9;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WM_CLOSE = 0x0010;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static readonly Guid CLSID_CUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid IID_IUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    private static readonly Guid IID_IUIAutomation2 = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");
    private static readonly Guid IID_IUIAutomationElement = new("d22108aa-8ac5-49a5-837b-37bbb3d7591e");
    private static readonly Guid IID_IUIAutomationElement3 = new("8471df34-aee0-4a01-a7de-7db9af12c296");
    private static readonly Guid IID_IUIAutomationElementArray = new("14314595-b4bc-4055-95f2-58f2e42c9855");
    private static readonly Guid IID_IUIAutomationTreeWalker = new("4042c624-389c-4afc-a630-9df854a541fc");
    private static readonly Guid IID_IUIAutomationInvokePattern = new("fb377fbe-8ea6-46d5-9c73-6499642d3059");

    private static readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private static int _interactionBusy;

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct W32RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public W32RECT rcMonitor;
        public W32RECT rcWork;
        public uint dwFlags;
    }

    private sealed class LiveTrayRecord
    {
        public string Key = "";
        public string ExecutablePath = "";
        public string Tooltip = "";
        public bool IsPromoted;
        public bool IsExplorerHosted;
        public Guid? IconGuid;
        public uint? Uid;
        public IntPtr OwnerHwnd;
        public NOTIFYICONIDENTIFIER Identifier;
        public int OrderIndex = int.MaxValue;
    }

    #region P/Invoke

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out W32RECT iconLocation);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out W32RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Win32Interop.POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    #endregion

    public static Task<IReadOnlyList<TrayIconItem>> EnumerateLiveTrayIconsAsync() =>
        Task.Run(() => (IReadOnlyList<TrayIconItem>)EnumerateLiveTrayIcons());

    public static List<TrayIconItem> EnumerateLiveTrayIcons()
    {
        var live = DiscoverLiveRecords(includeExplorerHosted: true);
        string? ownExe = Environment.ProcessPath;
        var result = new List<TrayIconItem>(live.Count);
        var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rec in live)
        {
            // Keep Explorer icons ONLY if they have an explicit tooltip (e.g. Bluetooth / Safely Remove Hardware);
            // skip unlabelled internal Explorer shell hosts so File Explorer windows don't duplicate.
            if (rec.IsExplorerHosted && string.IsNullOrWhiteSpace(rec.Tooltip))
                continue;

            string dedupKey = rec.Key;
            if (!seenApps.Add(dedupKey))
                continue;

            bool isOwn = !string.IsNullOrEmpty(ownExe) &&
                         string.Equals(rec.ExecutablePath, ownExe, StringComparison.OrdinalIgnoreCase);

            string displayName = DeriveDisplayName(rec.Tooltip, rec.ExecutablePath, isOwn);
            ImageSource? icon = GetCachedAppIcon(rec.ExecutablePath);

            result.Add(new TrayIconItem
            {
                Key = rec.Key,
                DisplayName = displayName,
                Tooltip = string.IsNullOrWhiteSpace(rec.Tooltip) ? displayName : rec.Tooltip,
                ExecutablePath = rec.ExecutablePath,
                IsPromoted = rec.IsPromoted,
                IsOwnProcess = isOwn,
                IconGuid = rec.IconGuid,
                Uid = rec.Uid,
                OwnerHwnd = rec.OwnerHwnd,
                Icon = icon,
                OrderIndex = rec.OrderIndex
            });
        }

        // Promoted icons appear first, then overflow icons in Windows' UIOrderList order.
        result.Sort((a, b) =>
        {
            int promoCmp = b.IsPromoted.CompareTo(a.IsPromoted);
            if (promoCmp != 0) return promoCmp;
            int orderCmp = a.OrderIndex.CompareTo(b.OrderIndex);
            if (orderCmp != 0) return orderCmp;
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    public static TrayIconItem? FindMatchingTrayIcon(IReadOnlyList<TrayIconItem> icons, string? exePath, string[]? processNames)
    {
        if (icons == null || icons.Count == 0) return null;
        if (!string.IsNullOrEmpty(exePath))
        {
            var exact = icons.FirstOrDefault(i => string.Equals(i.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        if (processNames != null && processNames.Length > 0)
        {
            foreach (var pn in processNames)
            {
                if (string.IsNullOrWhiteSpace(pn)) continue;
                var match = icons.FirstOrDefault(i =>
                {
                    string stem = Path.GetFileNameWithoutExtension(i.ExecutablePath);
                    return string.Equals(stem, pn, StringComparison.OrdinalIgnoreCase);
                });
                if (match != null) return match;
            }
        }
        return null;
    }

    internal static Dictionary<string, int> ParseOrderList(byte[]? bytes)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (bytes == null || bytes.Length < 8) return map;
        int count = bytes.Length / 8;
        for (int i = 0; i < count; i++)
        {
            ulong val = BitConverter.ToUInt64(bytes, i * 8);
            string key = val.ToString();
            map.TryAdd(key, i);
        }
        return map;
    }

    internal static string? ResolveKnownFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.StartsWith('{')) return path;
        int end = path.IndexOf('}');
        if (end <= 1) return null;
        if (!Guid.TryParse(path.AsSpan(0, end + 1), out Guid folderId))
            return null;

        if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out IntPtr pszPath) != 0 || pszPath == IntPtr.Zero)
            return null;

        try
        {
            string? basePath = Marshal.PtrToStringUni(pszPath);
            if (string.IsNullOrEmpty(basePath)) return null;
            return basePath + path[(end + 1)..];
        }
        finally
        {
            Marshal.FreeCoTaskMem(pszPath);
        }
    }

    internal static bool IsTrayMenuWindow(string className, uint style)
    {
        if (string.Equals(className, "#32768", StringComparison.Ordinal)) return true;
        return (style & WS_POPUP) != 0 && (style & WS_CAPTION) != WS_CAPTION;
    }

    internal static bool IsSameAppFamily(string processPath, string appPath)
    {
        if (string.IsNullOrEmpty(processPath) || string.IsNullOrEmpty(appPath)) return false;
        if (string.Equals(processPath, appPath, StringComparison.OrdinalIgnoreCase)) return true;

        string? dir = Path.GetDirectoryName(appPath);
        if (string.IsNullOrEmpty(dir) || dir.Length <= 3) return false;
        string dirPrefix = dir.EndsWith('\\') ? dir : dir + "\\";

        string sysRoot = (Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows").TrimEnd('\\') + "\\";
        if (dirPrefix.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (string envKey in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            string? root = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(dirPrefix, root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return processPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string DeriveDisplayName(string? tooltip, string exePath, bool isOwnProcess)
    {
        if (isOwnProcess) return "Pinn-Notch";
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            string firstLine = tooltip
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                // Strip BiDi isolate markers (\u202A..\u202C) occasionally present in Windows tooltips
                firstLine = firstLine.Trim('\u202A', '\u202B', '\u202C', '\u200E', '\u200F', ' ');
                if (firstLine.Length > 0)
                    return firstLine.Length > 42 ? firstLine[..42].TrimEnd() + "…" : firstLine;
            }
        }

        try
        {
            if (File.Exists(exePath))
            {
                var ver = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(ver.FileDescription))
                    return ver.FileDescription.Trim();
            }
        }
        catch { }

        return Path.GetFileNameWithoutExtension(exePath);
    }

    private static ImageSource? GetCachedAppIcon(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return _iconCache.GetOrAdd(exePath, static path =>
        {
            try
            {
                var icon = FileIconProvider.GetAppIcon(path, small: false);
                if (icon != null && !icon.IsFrozen && icon.CanFreeze)
                    icon.Freeze();
                return icon;
            }
            catch
            {
                return null;
            }
        });
    }

    private static List<(IntPtr Hwnd, uint Pid, string ExePath)> CollectProcessWindows()
    {
        var pidPathCache = new Dictionary<uint, string>();
        var list = new List<(IntPtr, uint, string)>(128);

        void RecordWindow(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            if (!pidPathCache.TryGetValue(pid, out string? exePath))
            {
                exePath = TryGetProcessPath(pid) ?? "";
                pidPathCache[pid] = exePath;
            }
            if (!string.IsNullOrEmpty(exePath))
                list.Add((hwnd, pid, exePath));
        }

        EnumWindows((hwnd, _) =>
        {
            RecordWindow(hwnd);
            return true;
        }, IntPtr.Zero);

        IntPtr prev = IntPtr.Zero;
        for (int safety = 0; safety < 2048; safety++)
        {
            IntPtr msgHwnd = FindWindowExW(HWND_MESSAGE, prev, null, null);
            if (msgHwnd == IntPtr.Zero) break;
            RecordWindow(msgHwnd);
            prev = msgHwnd;
        }

        return list;
    }

    private static string? TryGetProcessPath(uint pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return null;
        try
        {
            char* buf = stackalloc char[512];
            uint size = 512;
            if (QueryFullProcessImageNameW(hProc, 0, buf, ref size) && size > 0)
                return new string(buf, 0, (int)size);
        }
        finally
        {
            CloseHandle(hProc);
        }
        return null;
    }

    private static List<LiveTrayRecord> DiscoverLiveRecords(bool includeExplorerHosted)
    {
        var records = new List<LiveTrayRecord>();
        var windows = CollectProcessWindows();

        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey, writable: false);
        if (root == null) return records;

        var orderMap = ParseOrderList(root.GetValue("UIOrderList") as byte[]);
        string[] subKeys = root.GetSubKeyNames();

        foreach (string subName in subKeys.Take(4096))
        {
            using RegistryKey? sub = root.OpenSubKey(subName, writable: false);
            if (sub == null) continue;

            string? rawPath = sub.GetValue("ExecutablePath") as string;
            string? resolvedPath = ResolveKnownFolderPath(rawPath);
            if (string.IsNullOrWhiteSpace(resolvedPath)) continue;

            bool isExplorer = string.Equals(Path.GetFileName(resolvedPath), "explorer.exe", StringComparison.OrdinalIgnoreCase);
            if (isExplorer && !includeExplorerHosted) continue;

            var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>() };
            Guid? iconGuid = null;
            uint? uid = null;
            IntPtr ownerHwnd = IntPtr.Zero;

            if (sub.GetValue("IconGuid") is string guidStr && Guid.TryParse(guidStr, out Guid g))
            {
                iconGuid = g;
                id.guidItem = g;
                bool hasProc = windows.Any(w => string.Equals(w.ExePath, resolvedPath, StringComparison.OrdinalIgnoreCase));
                if (!hasProc || Shell_NotifyIconGetRect(ref id, out _) != 0)
                    continue;
            }
            else if (sub.GetValue("UID") is int rawUidInt)
            {
                uint parsedUid = unchecked((uint)rawUidInt);
                uid = parsedUid;
                id.uID = parsedUid;

                bool matched = false;
                foreach (var (hwnd, _, exe) in windows)
                {
                    if (!string.Equals(exe, resolvedPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    id.hWnd = hwnd;
                    if (Shell_NotifyIconGetRect(ref id, out _) == 0)
                    {
                        ownerHwnd = hwnd;
                        matched = true;
                        break;
                    }
                }
                if (!matched) continue;
            }
            else
            {
                continue;
            }

            bool isPromoted = sub.GetValue("IsPromoted") is int promoInt && promoInt == 1;
            string tooltip = (sub.GetValue("InitialTooltip") as string) ?? "";

            records.Add(new LiveTrayRecord
            {
                Key = subName,
                ExecutablePath = resolvedPath,
                Tooltip = tooltip,
                IsPromoted = isPromoted,
                IsExplorerHosted = isExplorer,
                IconGuid = iconGuid,
                Uid = uid,
                OwnerHwnd = ownerHwnd,
                Identifier = id,
                OrderIndex = orderMap.TryGetValue(subName, out int idx) ? idx : int.MaxValue
            });
        }

        return records;
    }

    #region UI Automation Interaction

    public static Task<bool> InteractWithTrayIconAsync(TrayIconItem item, bool contextMenu, int anchorScreenX, int anchorScreenY, int? dockTopScreenY = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                bool ok = InteractCore(item, contextMenu, anchorScreenX, anchorScreenY, dockTopScreenY);
                tcs.SetResult(ok);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("TRAY", $"Tray UIA interaction failed: {ex.Message}");
                tcs.SetResult(false);
            }
        })
        {
            IsBackground = true,
            Name = "PinnNotch-TrayUIA"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }

    private sealed class TrayExposureScope : IDisposable
    {
        public IntPtr TaskbarHwnd { get; }
        private readonly int _savedExStyle;
        private readonly W32RECT _savedRect;
        private readonly bool _wasTaskbarHidden;
        private IntPtr _overflowToHide;
        private bool _disposed;

        public TrayExposureScope()
        {
            TaskbarIntegrationService.BeginTrayInteraction();
            TaskbarHwnd = FindWindowW("Shell_TrayWnd", null);
            _wasTaskbarHidden = TaskbarHwnd != IntPtr.Zero &&
                                (!IsWindowVisible(TaskbarHwnd) || TaskbarIntegrationService.IsTaskbarHiddenByFreeze);

            if (_wasTaskbarHidden && TaskbarHwnd != IntPtr.Zero)
            {
                _savedExStyle = GetWindowLongW(TaskbarHwnd, GWL_EXSTYLE);
                GetWindowRect(TaskbarHwnd, out _savedRect);

                // Keep taskbar visually invisible (alpha=1 avoids XAML Island UIA teardown) & click-through
                SetWindowLongW(TaskbarHwnd, GWL_EXSTYLE, _savedExStyle | (int)(WS_EX_LAYERED | WS_EX_TRANSPARENT));
                SetLayeredWindowAttributes(TaskbarHwnd, 0, 1, LWA_ALPHA);
                SetWindowPos(TaskbarHwnd, IntPtr.Zero, _savedRect.Left, _savedRect.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
                ShowWindow(TaskbarHwnd, SW_SHOWNOACTIVATE);
            }
        }

        public IntPtr ExposeOverflow(nint uia)
        {
            static IntPtr FindOverflow()
            {
                IntPtr h = FindWindowW("TopLevelWindowForOverflowXamlIsland", null);
                if (h == IntPtr.Zero) h = FindWindowW("NotifyIconOverflowWindow", null);
                return h;
            }

            bool HasNotifyIcons(IntPtr overflowHwnd)
            {
                if (overflowHwnd == IntPtr.Zero) return false;
                IntPtr bridge = FindOverflowBridge(overflowHwnd);
                var buttons = FindDescendantButtons(uia, bridge, id => id == "NotifyItemIcon");
                bool any = buttons.Count > 0;
                ReleaseAll(buttons);
                return any;
            }

            IntPtr existing = FindOverflow();
            if (existing != IntPtr.Zero)
            {
                if (!IsWindowVisible(existing))
                {
                    _overflowToHide = existing;
                    ShowWindow(existing, SW_SHOWNOACTIVATE);
                }
                if (HasNotifyIcons(existing))
                    return existing;
            }

            // Invoke the overflow chevron inside Shell_TrayWnd's XAML DesktopWindowContentBridge
            IntPtr taskbarBridge = FindTaskbarBridge(TaskbarHwnd);
            nint root = UiaElementFromHandle(uia, taskbarBridge);
            if (root != 0)
            {
                try
                {
                    var all = FindDescendants(uia, root);
                    try
                    {
                        foreach (nint el in all)
                        {
                            string autoId = UiaGetStringProp(el, 29);
                            string cls = UiaGetStringProp(el, 30);
                            if (autoId == "SystemTrayIcon" && cls == "SystemTray.NormalButton")
                            {
                                string name = UiaGetStringProp(el, 23);
                                nint pattern = UiaGetPattern(el, UIA_InvokePatternId);
                                if (pattern != 0)
                                {
                                    try
                                    {
                                        if (name.EndsWith("Hide", StringComparison.OrdinalIgnoreCase))
                                        {
                                            UiaInvoke(pattern);
                                            Thread.Sleep(40);
                                        }

                                        for (int attempt = 0; attempt < 2; attempt++)
                                        {
                                            UiaInvoke(pattern);
                                            for (int poll = 0; poll < 20; poll++)
                                            {
                                                IntPtr ov = FindOverflow();
                                                if (ov != IntPtr.Zero)
                                                {
                                                    _overflowToHide = ov;
                                                    if (IsWindowVisible(ov) && HasNotifyIcons(ov))
                                                        return ov;
                                                }
                                                Thread.Sleep(30);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.Release(pattern);
                                    }
                                }
                                break;
                            }
                        }
                    }
                    finally
                    {
                        ReleaseAll(all);
                    }
                }
                finally
                {
                    Marshal.Release(root);
                }
            }

            return FindOverflow();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_overflowToHide != IntPtr.Zero && IsWindow(_overflowToHide))
                    ShowWindow(_overflowToHide, SW_HIDE);

                if (_wasTaskbarHidden && TaskbarHwnd != IntPtr.Zero && IsWindow(TaskbarHwnd))
                {
                    if (TaskbarIntegrationService.IsTaskbarHiddenByFreeze)
                    {
                        ShowWindow(TaskbarHwnd, SW_HIDE);
                        SetWindowLongW(TaskbarHwnd, GWL_EXSTYLE, _savedExStyle);
                        SetWindowPos(TaskbarHwnd, IntPtr.Zero, _savedRect.Left, _savedRect.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
                    }
                    else
                    {
                        SetWindowLongW(TaskbarHwnd, GWL_EXSTYLE, _savedExStyle);
                        ShowWindow(TaskbarHwnd, SW_SHOWNOACTIVATE);
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("TRAY", $"TrayExposure restore warning: {ex.Message}");
            }
            finally
            {
                TaskbarIntegrationService.EndTrayInteraction();
            }
        }
    }

    private static bool InteractCore(TrayIconItem item, bool contextMenu, int anchorScreenX, int anchorScreenY, int? dockTopScreenY)
    {
        if (Interlocked.CompareExchange(ref _interactionBusy, 1, 0) != 0)
            return false;

        bool comInitialized = false;
        nint uia = 0;
        nint targetEl = 0;
        try
        {
            int hrInit = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            if (hrInit >= 0) comInitialized = true;

            int hrCreate = CoCreateInstance(CLSID_CUIAutomation8, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IUIAutomation, out IntPtr pUia);
            if (hrCreate != 0 || pUia == IntPtr.Zero) return false;
            uia = pUia;

            // Configure UIA2 connection & transaction timeouts so hanging tray providers don't freeze
            if (Marshal.QueryInterface(uia, in IID_IUIAutomation2, out IntPtr pUia2) == 0 && pUia2 != IntPtr.Zero)
            {
                try
                {
                    Uia2SetTimeouts(pUia2, 1000, 2000);
                }
                finally
                {
                    Marshal.Release(pUia2);
                }
            }

            var liveAll = DiscoverLiveRecords(includeExplorerHosted: true);
            var targetRec = liveAll.FirstOrDefault(r => string.Equals(r.Key, item.Key, StringComparison.Ordinal));
            if (targetRec == null) return false;

            using var exposure = new TrayExposureScope();
            if (exposure.TaskbarHwnd == IntPtr.Zero) return false;

            targetEl = targetRec.IsPromoted
                ? FindPromotedElement(uia, exposure.TaskbarHwnd, targetRec)
                : FindOverflowElement(uia, exposure, liveAll, targetRec);

            if (targetEl == 0) return false;

            var idCheck = targetRec.Identifier;
            if (Shell_NotifyIconGetRect(ref idCheck, out _) != 0)
                return false;

            if (contextMenu)
            {
                var before = GetVisibleWindowSet();
                if (Marshal.QueryInterface(targetEl, in IID_IUIAutomationElement3, out IntPtr pEl3) != 0 || pEl3 == IntPtr.Zero)
                    return false;

                int hrMenu;
                try
                {
                    hrMenu = UiaShowContextMenu(pEl3);
                }
                finally
                {
                    Marshal.Release(pEl3);
                }
                if (hrMenu != 0) return false;

                var moved = MoveNewTrayMenuTo(item.ExecutablePath, before, anchorScreenX, anchorScreenY, dockTopScreenY);
                exposure.Dispose();

                if (moved.MenuHwnd != IntPtr.Zero)
                {
                    WaitForMenuClose(moved.MenuHwnd, moved.BottomScreenY);
                }
                return true;
            }
            else
            {
                nint pattern = UiaGetPattern(targetEl, UIA_InvokePatternId);
                if (pattern == 0) return false;
                try
                {
                    return UiaInvoke(pattern) == 0;
                }
                finally
                {
                    Marshal.Release(pattern);
                }
            }
        }
        finally
        {
            if (targetEl != 0) Marshal.Release(targetEl);
            if (uia != 0) Marshal.Release(uia);
            if (comInitialized) CoUninitialize();
            Interlocked.Exchange(ref _interactionBusy, 0);
        }
    }

    private static nint FindPromotedElement(nint uia, IntPtr taskbar, LiveTrayRecord target)
    {
        var id = target.Identifier;
        if (Shell_NotifyIconGetRect(ref id, out var iconRect) != 0)
            return 0;

        IntPtr bridge = FindTaskbarBridge(taskbar);
        nint root = UiaElementFromHandle(uia, bridge);
        if (root == 0) return 0;

        nint walker = UiaGetRawViewWalker(uia);
        if (walker == 0)
        {
            Marshal.Release(root);
            return 0;
        }

        var elements = FindDescendants(uia, root);
        var matches = new List<nint>();
        try
        {
            uint dpi = Math.Max(96u, GetDpiForWindow(taskbar));
            double cx = (iconRect.Left + iconRect.Right) * 0.5;
            double cy = (iconRect.Top + iconRect.Bottom) * 0.5;
            double scaledCx = cx * 96.0 / dpi;
            double scaledCy = cy * 96.0 / dpi;

            foreach (nint el in elements)
            {
                if (!UiaGetBoundingRect(el, out var bounds)) continue;
                if (bounds.Right - bounds.Left > 128 || bounds.Bottom - bounds.Top > 128) continue;

                bool Hit(double x, double y) =>
                    x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;

                if (!Hit(cx, cy) && !Hit(scaledCx, scaledCy)) continue;

                nint btn = FindOwningTrayButton(walker, el);
                if (btn == 0) continue;

                bool duplicate = false;
                foreach (nint known in matches)
                {
                    if (UiaCompareElements(uia, known, btn))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                    Marshal.Release(btn);
                else
                    matches.Add(btn);
            }

            if (matches.Count == 1)
            {
                nint chosen = matches[0];
                matches.Clear();
                return chosen;
            }
            return 0;
        }
        finally
        {
            ReleaseAll(matches);
            ReleaseAll(elements);
            Marshal.Release(walker);
            Marshal.Release(root);
        }
    }

    private static nint FindOverflowElement(nint uia, TrayExposureScope exposure, List<LiveTrayRecord> liveAll, LiveTrayRecord target)
    {
        IntPtr overflow = exposure.ExposeOverflow(uia);
        if (overflow == IntPtr.Zero) return 0;

        var hidden = liveAll
            .Where(r => !r.IsPromoted)
            .OrderBy(r => r.OrderIndex)
            .ToList();

        int pos = hidden.FindIndex(r => string.Equals(r.Key, target.Key, StringComparison.Ordinal));
        if (pos < 0) return 0;

        IntPtr bridge = FindOverflowBridge(overflow);
        var buttons = FindDescendantButtons(uia, bridge, autoId => autoId == "NotifyItemIcon");
        try
        {
            if (buttons.Count == hidden.Count && pos < buttons.Count)
            {
                // Cross-check labels where available
                for (int i = 0; i < buttons.Count; i++)
                {
                    string lbl = UiaGetStringProp(buttons[i], 23);
                    if (string.IsNullOrWhiteSpace(lbl)) continue;
                    var matchingIndices = hidden
                        .Select((r, idx) => (r, idx))
                        .Where(t => MatchesIconLabel(lbl, t.r.ExecutablePath))
                        .Select(t => t.idx)
                        .ToList();
                    if (matchingIndices.Count == 1 && matchingIndices[0] != i)
                        goto LabelFallback;
                }

                nint exact = buttons[pos];
                buttons.RemoveAt(pos);
                return exact;
            }

        LabelFallback:
            var labelled = new List<nint>();
            foreach (nint btn in buttons)
            {
                string lbl = UiaGetStringProp(btn, 23);
                if (MatchesIconLabel(lbl, target.ExecutablePath) &&
                    !hidden.Any(h => h.Key != target.Key && MatchesIconLabel(lbl, h.ExecutablePath)))
                {
                    labelled.Add(btn);
                }
            }
            if (labelled.Count == 1)
            {
                nint chosen = labelled[0];
                buttons.Remove(chosen);
                return chosen;
            }
            return 0;
        }
        finally
        {
            ReleaseAll(buttons);
        }
    }

    private static bool MatchesIconLabel(string label, string exePath)
    {
        static string Normalize(string s) =>
            new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        string normLabel = Normalize(label);
        if (string.IsNullOrEmpty(normLabel)) return false;

        string stem = Normalize(Path.GetFileNameWithoutExtension(exePath));
        if (!string.IsNullOrEmpty(stem) && normLabel.Contains(stem, StringComparison.Ordinal))
            return true;

        try
        {
            var ver = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(ver.FileDescription))
            {
                string friendly = Normalize(ver.FileDescription);
                if (!string.IsNullOrEmpty(friendly) && normLabel.Contains(friendly, StringComparison.Ordinal))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static nint FindOwningTrayButton(nint walker, nint element)
    {
        nint current = element;
        Marshal.AddRef(current);
        for (int depth = 0; depth < 3; depth++)
        {
            string autoId = UiaGetStringProp(current, 29);
            if (autoId is "NotifyItemIcon" or "SystemTrayIcon")
                return current;

            nint parent = UiaWalkerGetParent(walker, current);
            Marshal.Release(current);
            if (parent == 0) return 0;
            current = parent;
        }
        Marshal.Release(current);
        return 0;
    }

    private static IntPtr FindTaskbarBridge(IntPtr taskbar)
    {
        IntPtr b = FindWindowExW(taskbar, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
        return b != IntPtr.Zero ? b : taskbar;
    }

    private static IntPtr FindOverflowBridge(IntPtr overflow)
    {
        IntPtr b = FindWindowExW(overflow, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
        if (b == IntPtr.Zero) b = FindWindowExW(overflow, IntPtr.Zero, "ToolbarWindow32", null);
        return b != IntPtr.Zero ? b : overflow;
    }

    private static HashSet<IntPtr> GetVisibleWindowSet()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd)) set.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    private static (IntPtr MenuHwnd, int BottomScreenY) MoveNewTrayMenuTo(
        string appExePath, HashSet<IntPtr> before, int anchorX, int anchorY, int? dockTopScreenY)
    {
        long deadline = Environment.TickCount64 + 1200;
        char* clsBuf = stackalloc char[256];

        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(25);
            IntPtr found = IntPtr.Zero;

            EnumWindows((hwnd, _) =>
            {
                if (before.Contains(hwnd) || !IsWindowVisible(hwnd)) return true;
                int len = GetClassNameW(hwnd, clsBuf, 256);
                string cls = len > 0 ? new string(clsBuf, 0, len) : "";
                uint style = unchecked((uint)GetWindowLongW(hwnd, GWL_STYLE));
                if (!IsTrayMenuWindow(cls, style)) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                string? procPath = pid > 0 ? TryGetProcessPath(pid) : null;
                if (!string.IsNullOrEmpty(procPath) && IsSameAppFamily(procPath, appExePath))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero) continue;
            if (!GetWindowRect(found, out var rect)) return default;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            IntPtr mon = MonitorFromPoint(new Win32Interop.POINT { X = anchorX, Y = anchorY }, MONITOR_DEFAULTTONEAREST);
            if (!GetMonitorInfoW(mon, ref mi)) return (found, rect.Bottom);

            var area = mi.rcMonitor;
            int x = Math.Clamp(anchorX - width / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - width - 4));
            int bottomTarget = dockTopScreenY.HasValue ? Math.Min(dockTopScreenY.Value - 6, anchorY) : anchorY;
            int y = Math.Clamp(bottomTarget - height, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - height - 4));

            SetWindowPos(found, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return (found, y + height);
        }

        return default;
    }

    private static void WaitForMenuClose(IntPtr menuHwnd, int bottom)
    {
        long deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline && IsWindow(menuHwnd) && IsWindowVisible(menuHwnd))
        {
            if (GetWindowRect(menuHwnd, out var rect) && rect.Bottom > bottom)
            {
                SetWindowPos(menuHwnd, IntPtr.Zero, rect.Left, bottom - (rect.Bottom - rect.Top), 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            Thread.Sleep(50);
        }
    }

    #endregion

    #region Low-Level UIA COM VTable Helpers

    private static nint UiaElementFromHandle(nint uia, IntPtr hwnd)
    {
        void** vtbl = *(void***)uia;
        var fn = (delegate* unmanaged[Stdcall]<nint, IntPtr, nint*, int>)vtbl[6];
        nint el = 0;
        return fn(uia, hwnd, &el) == 0 ? el : 0;
    }

    private static void Uia2SetTimeouts(nint uia2, uint connMs, uint txMs)
    {
        void** vtbl = *(void***)uia2;
        var fnConn = (delegate* unmanaged[Stdcall]<nint, uint, int>)vtbl[61];
        var fnTx = (delegate* unmanaged[Stdcall]<nint, uint, int>)vtbl[63];
        _ = fnConn(uia2, connMs);
        _ = fnTx(uia2, txMs);
    }

    private static nint UiaGetRawViewWalker(nint uia)
    {
        void** vtbl = *(void***)uia;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtbl[16];
        nint w = 0;
        return fn(uia, &w) == 0 ? w : 0;
    }

    private static nint UiaCreateTrueCondition(nint uia)
    {
        void** vtbl = *(void***)uia;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtbl[21];
        nint c = 0;
        return fn(uia, &c) == 0 ? c : 0;
    }

    private static bool UiaCompareElements(nint uia, nint el1, nint el2)
    {
        void** vtbl = *(void***)uia;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, int*, int>)vtbl[3];
        int same = 0;
        return fn(uia, el1, el2, &same) == 0 && same != 0;
    }

    private static List<nint> FindDescendants(nint uia, nint root)
    {
        var list = new List<nint>();
        if (root == 0) return list;

        nint cond = UiaCreateTrueCondition(uia);
        if (cond == 0) return list;

        try
        {
            void** vtbl = *(void***)root;
            var fnFind = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)vtbl[6];
            nint arr = 0;
            if (fnFind(root, TreeScope_Descendants, cond, &arr) != 0 || arr == 0)
                return list;

            try
            {
                void** arrVtbl = *(void***)arr;
                var fnLen = (delegate* unmanaged[Stdcall]<nint, int*, int>)arrVtbl[3];
                var fnGet = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)arrVtbl[4];
                int len = 0;
                if (fnLen(arr, &len) == 0 && len > 0)
                {
                    for (int i = 0; i < len; i++)
                    {
                        nint item = 0;
                        if (fnGet(arr, i, &item) == 0 && item != 0)
                            list.Add(item);
                    }
                }
            }
            finally
            {
                Marshal.Release(arr);
            }
        }
        finally
        {
            Marshal.Release(cond);
        }

        return list;
    }

    private static List<nint> FindDescendantButtons(nint uia, IntPtr bridgeHwnd, Func<string, bool> autoIdPredicate)
    {
        var buttons = new List<nint>();
        nint root = UiaElementFromHandle(uia, bridgeHwnd);
        if (root == 0) return buttons;

        var all = FindDescendants(uia, root);
        try
        {
            foreach (nint el in all)
            {
                string autoId = UiaGetStringProp(el, 29);
                int ctrlType = UiaGetIntProp(el, 21);
                if (autoIdPredicate(autoId) && ctrlType == UIA_ButtonControlTypeId)
                {
                    Marshal.AddRef(el);
                    buttons.Add(el);
                }
            }
        }
        finally
        {
            ReleaseAll(all);
            Marshal.Release(root);
        }
        return buttons;
    }

    private static int UiaGetIntProp(nint el, int slot)
    {
        void** vtbl = *(void***)el;
        var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)vtbl[slot];
        int val = 0;
        return fn(el, &val) == 0 ? val : 0;
    }

    private static string UiaGetStringProp(nint el, int slot)
    {
        void** vtbl = *(void***)el;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtbl[slot];
        nint bstr = 0;
        if (fn(el, &bstr) != 0 || bstr == 0) return "";
        try
        {
            return Marshal.PtrToStringBSTR(bstr) ?? "";
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static bool UiaGetBoundingRect(nint el, out W32RECT rect)
    {
        void** vtbl = *(void***)el;
        var fn = (delegate* unmanaged[Stdcall]<nint, W32RECT*, int>)vtbl[43];
        fixed (W32RECT* pRect = &rect)
        {
            return fn(el, pRect) == 0;
        }
    }

    private static nint UiaGetPattern(nint el, int patternId)
    {
        void** vtbl = *(void***)el;
        var fn = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)vtbl[16];
        nint pat = 0;
        return fn(el, patternId, &pat) == 0 ? pat : 0;
    }

    private static nint UiaWalkerGetParent(nint walker, nint el)
    {
        void** vtbl = *(void***)walker;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtbl[3];
        nint parent = 0;
        return fn(walker, el, &parent) == 0 ? parent : 0;
    }

    private static int UiaInvoke(nint invokePattern)
    {
        void** vtbl = *(void***)invokePattern;
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)vtbl[3];
        return fn(invokePattern);
    }

    private static int UiaShowContextMenu(nint el3)
    {
        void** vtbl = *(void***)el3;
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)vtbl[91];
        return fn(el3);
    }

    private static void ReleaseAll(List<nint> ptrs)
    {
        foreach (nint p in ptrs)
        {
            if (p != 0) Marshal.Release(p);
        }
        ptrs.Clear();
    }

    #endregion

    #region Process & Fallback Actions

    public static bool TryFocusProcessWindow(string? executablePath, string[]? processNames = null, IntPtr preferredHwnd = default)
    {
        if (preferredHwnd != IntPtr.Zero && IsWindow(preferredHwnd))
        {
            if (IsIconic(preferredHwnd)) ShowWindow(preferredHwnd, SW_RESTORE);
            SetForegroundWindow(preferredHwnd);
            return true;
        }

        IntPtr target = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left <= 60 || r.Bottom - r.Top <= 60) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return true;
            if (MatchesTargetProcess(pid, executablePath, processNames))
            {
                target = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (target != IntPtr.Zero)
        {
            if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
            SetForegroundWindow(target);
            return true;
        }
        return false;
    }

    public static void CloseApplicationGracefully(string? executablePath, string[]? processNames = null, IntPtr preferredHwnd = default)
    {
        if (preferredHwnd != IntPtr.Zero && IsWindow(preferredHwnd))
        {
            PostMessageW(preferredHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        var closedPids = new HashSet<uint>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return true;
            if (MatchesTargetProcess(pid, executablePath, processNames))
            {
                PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closedPids.Add(pid);
            }
            return true;
        }, IntPtr.Zero);

        // Also send CloseMainWindow on any matching background processes if no visible window existed
        foreach (var proc in FindMatchingProcesses(executablePath, processNames))
        {
            try
            {
                if (!closedPids.Contains((uint)proc.Id) && !proc.HasExited)
                    proc.CloseMainWindow();
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
    }

    public static void ForceKillApplication(string? executablePath, string[]? processNames = null, IntPtr preferredHwnd = default)
    {
        if (preferredHwnd != IntPtr.Zero && IsWindow(preferredHwnd))
        {
            GetWindowThreadProcessId(preferredHwnd, out uint pid);
            if (pid != 0 && pid != (uint)Environment.ProcessId)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    p.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        foreach (var proc in FindMatchingProcesses(executablePath, processNames))
        {
            try
            {
                if (proc.Id != Environment.ProcessId && !proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
    }

    public static void OpenFileLocation(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{executablePath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private static bool MatchesTargetProcess(uint pid, string? exePath, string[]? processNames)
    {
        if (!string.IsNullOrEmpty(exePath))
        {
            string? procPath = TryGetProcessPath(pid);
            if (!string.IsNullOrEmpty(procPath) && string.Equals(procPath, exePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (processNames != null && processNames.Length > 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;
                if (processNames.Any(pn => string.Equals(pn, name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static List<Process> FindMatchingProcesses(string? exePath, string[]? processNames)
    {
        var list = new List<Process>();
        string? stem = !string.IsNullOrEmpty(exePath) ? Path.GetFileNameWithoutExtension(exePath) : null;
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(stem)) candidateNames.Add(stem);
        if (processNames != null)
        {
            foreach (var p in processNames)
            {
                if (!string.IsNullOrWhiteSpace(p)) candidateNames.Add(p);
            }
        }

        foreach (string name in candidateNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        string? pPath = TryGetProcessPath((uint)proc.Id);
                        if (!string.IsNullOrEmpty(pPath) && !string.Equals(pPath, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            proc.Dispose();
                            continue;
                        }
                    }
                    list.Add(proc);
                }
            }
            catch { }
        }
        return list;
    }

    #endregion
}
