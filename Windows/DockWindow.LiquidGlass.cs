using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch.Windows;

public partial class DockWindow
{
    private const double DockCornerRadiusDip = 22.0;
    private const double DockGlassPowerFactor = 2.6;
    private const double DockGlassBlurAmount = 0.72;

    private LiquidGlassController? _liquidGlass;
    private LiquidGlassRefractionEffect? _glassRefractionEffect;
    private BlurEffect? _glassHostBlur;
    private WindowScreenGeometry? _glassScreenGeometry;
    private IntPtr _hwnd;
    private bool _gpuRefractionConfigured;
    private bool _gpuRefractionFailed;
    private bool? _cachedSystemTransparency;
    private long _lastTransparencyCheckTicks;
    private Size _lastDockClipSize;
    private CornerRadius _lastDockClipCornerRadius;
    private LiquidGlassController.CaptureRegion? _lastDockCaptureRegion;
    private LiquidGlassController.GpuGeometry? _lastDockGpuGeometry;
    private double _lastDockDpiScale = 1.0;
    private bool _loggedFirstDockGlassFrame;

    private bool UseDockGpuRefraction =>
        !_gpuRefractionFailed && LiquidGlassRefractionEffect.IsAvailable;

    private IntPtr EnsureDockHwnd()
    {
        if (_hwnd == IntPtr.Zero)
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        return _hwnd;
    }

    internal void InitializeDockLiquidGlass()
    {
        if (_isCleanedUp || DockGlassBackdropHost == null) return;

        UpdateDockGlassClip();

        if (!IsDockSystemTransparencyEnabled())
        {
            ShowDockFallbackSurface();
            return;
        }

        if (DockGlassGrainOverlay != null)
            DockGlassGrainOverlay.Background = GlassGrainBrush.Instance;

        if (DockGlassBackdropHost.Visibility != Visibility.Visible)
        {
            DockGlassBackdropHost.Visibility = Visibility.Visible;
            DockGlassBackdropHost.Opacity = _liquidGlass?.HasPresentedFrame == true ? 1.0 : 0.0;
        }

        double dpiScale = GetDockDpiScale();
        EnsureDockHwnd();

        _liquidGlass ??= new LiquidGlassController(
            DockGlassBackdropImage,
            EnsureDockHwnd,
            GetDockGlassCaptureRegion,
            activeFps: 45,
            logTag: "DOCK",
            maxRegionWidth: (int)Math.Ceiling(1800 * dpiScale),
            maxRegionHeight: (int)Math.Ceiling(180 * dpiScale));

        _liquidGlass.HideFromScreenCapture = false;
        _liquidGlass.CropGpuPresentationToCapture = true;
        ConfigureDockGlassOptics(dpiScale);

        CompositionTarget.Rendering -= OnDockGlassFrameUpdate;
        CompositionTarget.Rendering += OnDockGlassFrameUpdate;

        _liquidGlass.Start();
        SetDockGlassPaused(_isDockSlidDown || _isFullscreenAppActive);
    }

    private void ConfigureDockGlassOptics(double dpiScale)
    {
        if (_liquidGlass == null) return;

        int gaussianSigma = (int)Math.Round(DockGlassBlurAmount * 26.0 * dpiScale);
        _liquidGlass.SetBlur(gaussianSigma);
        _liquidGlass.UpdateFps(45);

        _liquidGlass.SetParams(new LiquidGlassController.GlassParams
        {
            PowerFactor = DockGlassPowerFactor,
            RefractionA = 0.65,
            RefractionB = 1.9,
            RefractionC = 4.4,
            RefractionD = 6.0,
            FPower = 1.0,
            Noise = 0.05,
            GlowWeight = 0.22,
            GlowBias = 0.0,
            GlowEdge0 = 0.14,
            GlowEdge1 = 0.0,
            Refraction = 0.45,
            EdgeBend = 1.15,
            ChromaticAberration = 0.22,
            Distortion = 0.16,
            ZRadius = 0.20,
            Saturation = 0.18,
            Brightness = -0.02,
            BevelMode = 0,
            TopCornerRadius = DockCornerRadiusDip,
            BottomCornerRadius = DockCornerRadiusDip
        });

        if (UseDockGpuRefraction)
        {
            try
            {
                _glassRefractionEffect ??= new LiquidGlassRefractionEffect();
                DockGlassBackdropImage.Effect = _glassRefractionEffect;
                DockGlassBackdropImage.HorizontalAlignment = HorizontalAlignment.Left;
                DockGlassBackdropImage.VerticalAlignment = VerticalAlignment.Top;
                DockGlassBackdropImage.Width = _liquidGlass.SurfaceWidth / dpiScale;
                DockGlassBackdropImage.Height = _liquidGlass.SurfaceHeight / dpiScale;

                if (!_liquidGlass.SetGpuMode(true, g =>
                    {
                        _lastDockGpuGeometry = g;
                        UpdateDockShaderGeometry();
                    }, OnDockGpuRefractionFailure))
                {
                    FallbackDockToCpuBlur();
                    return;
                }

                LiquidGlassSurfaceBinder.ApplyGpuBlur(DockGlassBackdropHost, ref _glassHostBlur, DockGlassBlurAmount, true);
                _gpuRefractionConfigured = true;
                UpdateDockShaderGeometry();
                return;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("LIQUIDGLASS", $"Dock GPU refraction attach failed, using CPU fallback: {ex.Message}");
                FallbackDockToCpuBlur();
                return;
            }
        }

        FallbackDockToCpuBlur();
    }

    private void FallbackDockToCpuBlur()
    {
        _gpuRefractionFailed = true;
        _gpuRefractionConfigured = false;
        _lastDockGpuGeometry = null;
        if (DockGlassBackdropImage != null)
        {
            DockGlassBackdropImage.Effect = null;
            DockGlassBackdropImage.Width = double.NaN;
            DockGlassBackdropImage.Height = double.NaN;
        }
        if (DockGlassBackdropHost != null)
            DockGlassBackdropHost.Effect = null;
        _glassHostBlur = null;
        _liquidGlass?.SetGpuMode(false, null);
    }

    private void OnDockGpuRefractionFailure(Exception ex)
    {
        RuntimeLog.Warn("LIQUIDGLASS", $"Dock GPU render failure: {ex.Message}");
        Dispatcher.BeginInvoke(FallbackDockToCpuBlur);
    }

    private void ShowDockFallbackSurface()
    {
        CompositionTarget.Rendering -= OnDockGlassFrameUpdate;
        _liquidGlass?.Stop();
        if (DockGlassBackdropHost != null)
            DockGlassBackdropHost.Visibility = Visibility.Collapsed;
        if (DockGlassFallbackFill != null)
            DockGlassFallbackFill.Opacity = 1.0;
    }

    internal void SetDockGlassPaused(bool paused)
    {
        if (_liquidGlass == null) return;
        _liquidGlass.SetPresentationPaused(paused);
        _liquidGlass.SetIdle(paused);
        if (!paused)
        {
            _lastDockCaptureRegion = null;
            _liquidGlass.ForceRefresh();
        }
    }

    private void OnDockGlassFrameUpdate(object? sender, EventArgs e)
    {
        SyncDockLiveGlassFrame();
    }

    private void SyncDockLiveGlassFrame()
    {
        if (_isCleanedUp || _liquidGlass == null || DockGlassBackdropHost == null) return;

        if (!IsDockSystemTransparencyEnabled())
        {
            ShowDockFallbackSurface();
            return;
        }

        if (_liquidGlass.HasPresentedFrame)
        {
            if (DockGlassBackdropHost.Visibility != Visibility.Visible)
                DockGlassBackdropHost.Visibility = Visibility.Visible;
            if (DockGlassBackdropHost.Opacity < 1.0)
                DockGlassBackdropHost.Opacity = 1.0;
            if (DockGlassFallbackFill != null && DockGlassFallbackFill.Opacity > 0.0)
                DockGlassFallbackFill.Opacity = 0.0;
            if (!_loggedFirstDockGlassFrame && RuntimeLog.IsEnabled(LogLevel.Info))
            {
                _loggedFirstDockGlassFrame = true;
                RuntimeLog.Info("DOCK-GLASS", () => $"Dock live glass presented (gpu={_gpuRefractionConfigured})");
            }
        }

        if (_isDockSlidDown || _isFullscreenAppActive) return;

        var region = GetDockGlassCaptureRegion(out double dpiScale, out double exactW, out double exactH, out double screenLeft, out double screenTop);
        if (!DockCaptureRegionEquals(_lastDockCaptureRegion, region))
        {
            _lastDockCaptureRegion = region;
            _liquidGlass.SetLiveRegion(region);
            UpdateDockShaderGeometry(dpiScale, exactW, exactH, screenLeft, screenTop);
        }
    }

    private LiquidGlassController.CaptureRegion? GetDockGlassCaptureRegion() =>
        GetDockGlassCaptureRegion(out _, out _, out _, out _, out _);

    private LiquidGlassController.CaptureRegion? GetDockGlassCaptureRegion(
        out double dpiScale,
        out double exactW,
        out double exactH,
        out double screenLeft,
        out double screenTop)
    {
        dpiScale = 1.0;
        exactW = 0;
        exactH = 0;
        screenLeft = 0;
        screenTop = 0;

        if (_isCleanedUp || Visibility != Visibility.Visible || DockPill == null || DockPill.Visibility != Visibility.Visible)
            return null;

        double pillW = DockPill.ActualWidth;
        double pillH = DockPill.ActualHeight;
        if (!double.IsFinite(pillW) || !double.IsFinite(pillH) || pillW <= 4 || pillH <= 4)
            return null;

        dpiScale = GetDockDpiScale();
        if (Math.Abs(dpiScale - _lastDockDpiScale) > 0.01 && _liquidGlass != null)
        {
            _lastDockDpiScale = dpiScale;
            if (UseDockGpuRefraction && _gpuRefractionConfigured)
            {
                DockGlassBackdropImage.Width = _liquidGlass.SurfaceWidth / dpiScale;
                DockGlassBackdropImage.Height = _liquidGlass.SurfaceHeight / dpiScale;
            }
        }

        exactW = pillW * dpiScale;
        exactH = pillH * dpiScale;
        int physW = Math.Max(1, (int)Math.Round(exactW));
        int physH = Math.Max(1, (int)Math.Round(exactH));

        try
        {
            if (PresentationSource.FromVisual(DockPill) != null &&
                (_glassScreenGeometry ??= new(this)).TryGetPosition(DockPill, out var pt))
            {
                screenLeft = pt.X;
                screenTop = pt.Y;
            }
            else
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        int physLeft = (int)Math.Round(screenLeft, MidpointRounding.AwayFromZero);
        int physTop = (int)Math.Round(screenTop, MidpointRounding.AwayFromZero);
        double subX = screenLeft - physLeft;
        double subY = screenTop - physTop;

        return new LiquidGlassController.CaptureRegion(
            physLeft, physTop, physW, physH,
            DockCornerRadiusDip, DockCornerRadiusDip,
            subX, subY);
    }

    private void UpdateDockShaderGeometry()
    {
        if (!_gpuRefractionConfigured || _glassRefractionEffect == null || _liquidGlass == null || DockPill == null)
            return;

        GetDockGlassCaptureRegion(out double dpiScale, out double exactW, out double exactH, out double screenLeft, out double screenTop);
        if (exactW > 0 && exactH > 0)
            UpdateDockShaderGeometry(dpiScale, exactW, exactH, screenLeft, screenTop);
    }

    private void UpdateDockShaderGeometry(double dpiScale, double exactW, double exactH, double screenLeft, double screenTop)
    {
        var fx = _glassRefractionEffect;
        var lg = _liquidGlass;
        if (!_gpuRefractionConfigured || fx == null || lg == null || DockGlassBackdropImage == null)
            return;

        double sourceW = _lastDockGpuGeometry?.SrcW ?? lg.SurfaceWidth;
        double sourceH = _lastDockGpuGeometry?.SrcH ?? lg.SurfaceHeight;
        if (Math.Abs(DockGlassBackdropImage.Width - sourceW / dpiScale) > 0.01)
            DockGlassBackdropImage.Width = sourceW / dpiScale;
        if (Math.Abs(DockGlassBackdropImage.Height - sourceH / dpiScale) > 0.01)
            DockGlassBackdropImage.Height = sourceH / dpiScale;

        int capX = lg.LastPresentedCaptureOriginX;
        int capY = lg.LastPresentedCaptureOriginY;
        double offX;
        double offY;
        if (capX != int.MinValue && capY != int.MinValue)
        {
            offX = screenLeft - capX;
            offY = screenTop - capY;
        }
        else if (_lastDockGpuGeometry is { } lastGeom)
        {
            offX = screenLeft - lastGeom.CaptureOriginX;
            offY = screenTop - lastGeom.CaptureOriginY;
        }
        else
        {
            offX = _lastDockGpuGeometry?.OffX ?? 0;
            offY = _lastDockGpuGeometry?.OffY ?? 0;
        }

        fx.SrcW = sourceW;
        fx.SrcH = sourceH;
        fx.NotchW = exactW;
        fx.NotchH = exactH;
        fx.OffX = offX;
        fx.OffY = offY;
        fx.TopCornerR = DockCornerRadiusDip * dpiScale;
        fx.BottomCornerR = DockCornerRadiusDip * dpiScale;
        fx.PowerFactor = DockGlassPowerFactor;
        fx.A = 0.65;
        fx.B = 1.9;
        fx.C = 4.4;
        fx.D = 6.0;
        fx.FPower = 1.0;
        fx.Noise = 0.05;
        fx.GlowWeight = 0.22;
        fx.GlowBias = 0.0;
        fx.GlowEdge0 = 0.14;
        fx.GlowEdge1 = 0.0;
        fx.EdgeBend = 1.15;
        fx.BevelMode = 0;
        fx.Chroma = 0.22;
        fx.SatFactor = 1.18;
        fx.BrightAdd = -0.02;
        fx.HighlightStrength = 0.35;
    }

    private void UpdateDockGlassClip()
    {
        if (DockGlassClipHost == null || DockPill == null) return;

        double w = DockGlassClipHost.ActualWidth;
        double h = DockGlassClipHost.ActualHeight;
        if (!double.IsFinite(w) || w <= 0) w = DockPill.ActualWidth;
        if (!double.IsFinite(h) || h <= 0) h = DockPill.ActualHeight;
        if (!double.IsFinite(w) || w <= 0 || !double.IsFinite(h) || h <= 0) return;

        var size = new Size(w, h);
        var cr = DockPill.CornerRadius;
        if (DockGlassClipHost.Clip != null &&
            Math.Abs(_lastDockClipSize.Width - size.Width) <= 0.01 &&
            Math.Abs(_lastDockClipSize.Height - size.Height) <= 0.01 &&
            _lastDockClipCornerRadius == cr)
        {
            return;
        }

        _lastDockClipSize = size;
        _lastDockClipCornerRadius = cr;
        DockGlassClipHost.Clip = GlassClipBuilder.CreateClip(size, cr, DockGlassPowerFactor);
    }

    private void DockGlassClipHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDockGlassClip();
        UpdateDockShaderGeometry();
    }

    private void DockGlassClipHost_LayoutUpdated(object? sender, EventArgs e)
    {
        SyncDockLiveGlassFrame();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _glassScreenGeometry?.Invalidate();
        if (_liquidGlass == null) return;

        _lastDockCaptureRegion = null;
        if (_liquidGlass.MaxRegionWidth < Math.Ceiling(1800 * newDpi.DpiScaleX) ||
            _liquidGlass.MaxRegionHeight < Math.Ceiling(180 * newDpi.DpiScaleY))
        {
            DisposeDockLiquidGlass();
            InitializeDockLiquidGlass();
            return;
        }
        ConfigureDockGlassOptics(newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1.0);
    }

    private double GetDockDpiScale()
    {
        try
        {
            double scale = (_glassScreenGeometry ??= new(this)).DpiScaleX;
            return scale > 0 ? scale : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private bool IsDockSystemTransparencyEnabled()
    {
        long now = Environment.TickCount64;
        if (_cachedSystemTransparency.HasValue && now - _lastTransparencyCheckTicks < 5000)
            return _cachedSystemTransparency.Value;

        _lastTransparencyCheckTicks = now;
        bool enabled = LiquidGlassSurfaceBinder.ReadSystemTransparencySetting();
        _cachedSystemTransparency = enabled;
        return enabled;
    }

    private static bool DockCaptureRegionEquals(LiquidGlassController.CaptureRegion? a, LiquidGlassController.CaptureRegion? b)
    {
        if (a.HasValue != b.HasValue) return false;
        if (!a.HasValue) return true;
        var r1 = a.Value;
        var r2 = b!.Value;
        return r1.X == r2.X && r1.Y == r2.Y && r1.Width == r2.Width && r1.Height == r2.Height
            && Math.Abs(r1.SubX - r2.SubX) <= 1e-4 && Math.Abs(r1.SubY - r2.SubY) <= 1e-4;
    }

    private void DisposeDockLiquidGlass()
    {
        CompositionTarget.Rendering -= OnDockGlassFrameUpdate;
        _gpuRefractionConfigured = false;
        _lastDockGpuGeometry = null;
        try { _liquidGlass?.Stop(); } catch { }
        try { _liquidGlass?.Dispose(); } catch { }
        _liquidGlass = null;

        if (DockGlassBackdropImage != null)
        {
            DockGlassBackdropImage.Effect = null;
            DockGlassBackdropImage.Width = double.NaN;
            DockGlassBackdropImage.Height = double.NaN;
        }
        if (DockGlassBackdropHost != null)
            DockGlassBackdropHost.Effect = null;
        _glassHostBlur = null;
        _glassRefractionEffect = null;
    }
}
