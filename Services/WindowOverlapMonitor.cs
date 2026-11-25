using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace VNotch.Services;

public sealed class WindowOverlapMonitor : IDisposable
{
    private const long BoundsRefreshIntervalMs = 2500;
    private readonly Timer _evalTimer;
    private readonly Dispatcher _dispatcher;
    private readonly object _boundsLock = new();
    private int _isEvaluating;
    private int _boundsRefreshQueued;
    private long _lastBoundsRefreshTicks;
    private bool _hasCachedBounds;
    private bool _disposed;

    private Func<Rect?>? _getDockRect;
    private Func<Rect?>? _getNotchRect;
    private Rect? _cachedDockRect;
    private Rect? _cachedNotchRect;

    private bool _lastDockOverlapped;
    private bool _lastNotchOverlapped;
    private bool _hasEvaluatedDock;
    private bool _hasEvaluatedNotch;

    public event Action<bool>? DockOverlapChanged;
    public event Action<bool>? NotchOverlapChanged;

    public bool IsDockOverlapped => _lastDockOverlapped;
    public bool IsNotchOverlapped => _lastNotchOverlapped;

    public WindowOverlapMonitor(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _evalTimer = new Timer(OnTimerTick, null, 650, 650);
    }

    public void RegisterDockRegionProvider(Func<Rect?> provider)
    {
        _getDockRect = provider;
        QueueBoundsRefresh();
        TriggerRecheck();
    }

    public void RegisterNotchRegionProvider(Func<Rect?> provider)
    {
        _getNotchRect = provider;
        QueueBoundsRefresh();
        TriggerRecheck();
    }

    public void TriggerRecheck()
    {
        if (_disposed) return;
        QueueBoundsRefresh();
        ThreadPool.QueueUserWorkItem(_ => Evaluate());
    }

    private void QueueBoundsRefresh()
    {
        if (_disposed || Interlocked.CompareExchange(ref _boundsRefreshQueued, 1, 0) != 0)
            return;

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshBoundsOnUi);
        }
        catch
        {
            Interlocked.Exchange(ref _boundsRefreshQueued, 0);
        }
    }

    private void RefreshBoundsOnUi()
    {
        Interlocked.Exchange(ref _boundsRefreshQueued, 0);
        if (_disposed) return;

        Rect? dockRect = _getDockRect?.Invoke();
        Rect? notchRect = _getNotchRect?.Invoke();
        lock (_boundsLock)
        {
            _cachedDockRect = dockRect;
            _cachedNotchRect = notchRect;
            _hasCachedBounds = true;
            Volatile.Write(ref _lastBoundsRefreshTicks, Environment.TickCount64);
        }
    }

    private void OnTimerTick(object? state)
    {
        if (_disposed) return;
        Evaluate();
    }

    private void Evaluate()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _isEvaluating, 1, 0) != 0)
            return;

        try
        {
            Rect? dockRect;
            Rect? notchRect;
            bool needSyncFirstFetch;

            lock (_boundsLock)
            {
                needSyncFirstFetch = !_hasCachedBounds;
                dockRect = _cachedDockRect;
                notchRect = _cachedNotchRect;
            }

            if (needSyncFirstFetch)
            {
                if (_dispatcher.CheckAccess())
                {
                    RefreshBoundsOnUi();
                }
                else
                {
                    try
                    {
                        _dispatcher.Invoke(RefreshBoundsOnUi, DispatcherPriority.Normal);
                    }
                    catch
                    {
                        return;
                    }
                }

                lock (_boundsLock)
                {
                    dockRect = _cachedDockRect;
                    notchRect = _cachedNotchRect;
                }
            }
            else
            {
                long nowTicks = Environment.TickCount64;
                if (nowTicks - Volatile.Read(ref _lastBoundsRefreshTicks) >= BoundsRefreshIntervalMs)
                {
                    QueueBoundsRefresh();
                }
            }

            bool dockCheckNeeded = dockRect.HasValue && dockRect.Value.Width > 0 && dockRect.Value.Height > 0;
            bool notchCheckNeeded = notchRect.HasValue && notchRect.Value.Width > 0 && notchRect.Value.Height > 0;

            if (!dockCheckNeeded && !notchCheckNeeded)
                return;

            uint currentPid = (uint)Environment.ProcessId;
            bool dockOverlapped = false;
            bool notchOverlapped = false;

            Win32Interop.EnumWindows((hWnd, lParam) =>
            {
                // Ignore our own process windows
                Win32Interop.GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid) return true;

                // Ignore invisible, minimized, cloaked, or blocked shell windows
                if (!Win32Interop.IsWindowVisible(hWnd) || Win32Interop.IsIconic(hWnd)) return true;
                if (FullscreenDetector.IsWindowCloaked(hWnd)) return true;
                if (FullscreenDetector.IsBlockedClass(hWnd)) return true;

                if (FullscreenDetector.TryGetWindowBounds(hWnd, out var wr))
                {
                    int w = wr.Right - wr.Left;
                    int h = wr.Bottom - wr.Top;
                    if (w < 80 || h < 80) return true;

                    if (dockCheckNeeded && !dockOverlapped)
                    {
                        var dr = dockRect!.Value;
                        if (wr.Right > dr.Left && wr.Left < dr.Right &&
                            wr.Bottom > dr.Top && wr.Top < dr.Bottom)
                        {
                            dockOverlapped = true;
                        }
                    }

                    if (notchCheckNeeded && !notchOverlapped)
                    {
                        var nr = notchRect!.Value;
                        if (wr.Right > nr.Left && wr.Left < nr.Right &&
                            wr.Bottom > nr.Top && wr.Top < nr.Bottom)
                        {
                            notchOverlapped = true;
                        }
                    }

                    // Stop early if both are found
                    if ((!dockCheckNeeded || dockOverlapped) && (!notchCheckNeeded || notchOverlapped))
                        return false;
                }

                return true;
            }, IntPtr.Zero);

            // Notify Dock if state changed
            if (dockCheckNeeded && (!_hasEvaluatedDock || dockOverlapped != _lastDockOverlapped))
            {
                _hasEvaluatedDock = true;
                _lastDockOverlapped = dockOverlapped;
                _dispatcher.BeginInvoke(() => DockOverlapChanged?.Invoke(dockOverlapped));
            }

            // Notify Notch if state changed
            if (notchCheckNeeded && (!_hasEvaluatedNotch || notchOverlapped != _lastNotchOverlapped))
            {
                _hasEvaluatedNotch = true;
                _lastNotchOverlapped = notchOverlapped;
                _dispatcher.BeginInvoke(() => NotchOverlapChanged?.Invoke(notchOverlapped));
            }
        }
        catch
        {
            // Best effort overlap scan
        }
        finally
        {
            Interlocked.Exchange(ref _isEvaluating, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _evalTimer.Dispose();
    }
}
