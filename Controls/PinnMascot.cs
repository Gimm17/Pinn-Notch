using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VNotch.Controls;

public enum PinnState
{
    Idle,
    LookAtCursor,
    Dancing,
    Sleeping,
    Happy,
    DragHover
}

/// <summary>
/// Pinn the Cat — An original, ultra-lightweight, vector-rendered cute chibi cat mascot
/// that sits adorably on the notch. Features eye gaze tracking, musical dancing with mini headphones,
/// sleeping animations, and drag-and-drop reactions.
/// </summary>
public sealed class PinnMascot : FrameworkElement
{
    private PinnState _state = PinnState.Idle;
    private double _gazeX = 0;
    private double _gazeY = 0;
    private double _targetGazeX = 0;
    private double _targetGazeY = 0;

    // Animation variables
    private double _blinkProgress = 0.0; // 0 = fully open, 1 = closed
    private bool _isBlinking = false;
    private double _danceTime = 0.0;
    private double _bounceY = 0.0;
    private double _heartOpacity = 0.0;
    private double _heartY = 0.0;
    private double _sleepZTime = 0.0;

    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _idleSleepTimer;
    private readonly Random _random = new();

    // Visual brushes & pens (frozen for zero GC allocations)
    private static readonly Brush CatBodyBrush = CreateFrozenBrush(Color.FromRgb(255, 252, 248)); // Warm cream
    private static readonly Brush CatInnerEarBrush = CreateFrozenBrush(Color.FromRgb(255, 182, 193)); // Light pink
    private static readonly Brush CatBlushBrush = CreateFrozenBrush(Color.FromArgb(160, 255, 160, 180)); // Soft cheek blush
    private static readonly Brush CatNoseBrush = CreateFrozenBrush(Color.FromRgb(255, 140, 160)); // Rosy nose
    private static readonly Brush CatEyeBrush = CreateFrozenBrush(Color.FromRgb(35, 30, 45)); // Deep midnight violet
    private static readonly Brush EyeGlossBrush = CreateFrozenBrush(Colors.White);
    private static readonly Brush HeadphoneBandBrush = CreateFrozenBrush(Color.FromRgb(255, 130, 170));
    private static readonly Brush HeadphoneCupBrush = CreateFrozenBrush(Color.FromRgb(240, 100, 150));
    private static readonly Brush MusicNoteBrush = CreateFrozenBrush(Color.FromRgb(255, 160, 200));
    private static readonly Brush SleepZBrush = CreateFrozenBrush(Color.FromArgb(200, 180, 200, 240));
    private static readonly Brush HeartBrush = CreateFrozenBrush(Color.FromRgb(255, 100, 130));

    private static readonly Pen LinePen = CreateFrozenPen(Color.FromRgb(50, 45, 60), 1.2);
    private static readonly Pen WhiskerPen = CreateFrozenPen(Color.FromArgb(160, 70, 65, 80), 0.9);
    private static readonly Pen HeadphoneBandPen = CreateFrozenPen(Color.FromRgb(240, 100, 150), 2.0);

    public static readonly DependencyProperty IsDancingProperty =
        DependencyProperty.Register(nameof(IsDancing), typeof(bool), typeof(PinnMascot),
            new PropertyMetadata(false, (d, e) => ((PinnMascot)d).OnDancingChanged((bool)e.NewValue)));

    public bool IsDancing
    {
        get => (bool)GetValue(IsDancingProperty);
        set => SetValue(IsDancingProperty, value);
    }

    public PinnState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                InvalidateVisual();
            }
        }
    }

    public PinnMascot()
    {
        Width = 54;
        Height = 36;
        ClipToBounds = false;
        Cursor = Cursors.Hand;

        // Render loop timer (runs at ~40 FPS during active animations, stops when idle)
        _tickTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _tickTimer.Tick += OnTick;

        // Blink timer: triggers every 3-5 seconds
        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.5)
        };
        _blinkTimer.Tick += OnBlinkTrigger;
        _blinkTimer.Start();

        // Idle sleep timer: 60 seconds without interaction triggers sleep
        _idleSleepTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _idleSleepTimer.Tick += (s, e) =>
        {
            if (!IsDancing && _state == PinnState.Idle)
            {
                State = PinnState.Sleeping;
                EnsureActiveTimer();
            }
        };
        _idleSleepTimer.Start();

        Loaded += (s, e) => InvalidateVisual();
        Unloaded += (s, e) => StopAllTimers();
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var p = new Pen(CreateFrozenBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        p.Freeze();
        return p;
    }

    private void EnsureActiveTimer()
    {
        if (!_tickTimer.IsEnabled)
        {
            _tickTimer.Start();
        }
    }

    private void StopActiveTimerIfIdle()
    {
        if (_state == PinnState.Idle && !_isBlinking && _heartOpacity <= 0.01 && !IsDancing)
        {
            _tickTimer.Stop();
        }
    }

    private void StopAllTimers()
    {
        _tickTimer.Stop();
        _blinkTimer.Stop();
        _idleSleepTimer.Stop();
    }

    private void OnDancingChanged(bool dancing)
    {
        if (dancing)
        {
            State = PinnState.Dancing;
            WakeUp();
            EnsureActiveTimer();
        }
        else if (_state == PinnState.Dancing)
        {
            State = PinnState.Idle;
            StopActiveTimerIfIdle();
        }
    }

    public void WakeUp()
    {
        _idleSleepTimer.Stop();
        _idleSleepTimer.Start();
        if (_state == PinnState.Sleeping)
        {
            State = PinnState.Idle;
        }
    }

    public void TriggerHappyBounce()
    {
        WakeUp();
        State = PinnState.Happy;
        _bounceY = -7.0;
        _heartOpacity = 1.0;
        _heartY = -12.0;
        EnsureActiveTimer();
    }

    public void SetDragHover(bool isHovering)
    {
        WakeUp();
        State = isHovering ? PinnState.DragHover : (IsDancing ? PinnState.Dancing : PinnState.Idle);
        EnsureActiveTimer();
    }

    public void UpdateMousePosition(Point mousePosInNotch, Rect notchBounds)
    {
        if (_state == PinnState.Sleeping || _state == PinnState.DragHover) return;

        WakeUp();

        // Calculate relative angle/distance from mascot center to mouse
        Point mascotCenter = new(ActualWidth / 2.0, ActualHeight / 2.0);
        Point posInMascot;
        try
        {
            posInMascot = TranslatePoint(mascotCenter, Application.Current.MainWindow);
        }
        catch
        {
            posInMascot = new Point(notchBounds.Width / 2.0, notchBounds.Height / 2.0);
        }

        double dx = mousePosInNotch.X - posInMascot.X;
        double dy = mousePosInNotch.Y - posInMascot.Y;

        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist > 1.0)
        {
            _targetGazeX = Math.Clamp(dx / 40.0, -3.2, 3.2);
            _targetGazeY = Math.Clamp(dy / 40.0, -2.2, 2.2);
        }
        else
        {
            _targetGazeX = 0;
            _targetGazeY = 0;
        }

        if (_state == PinnState.Idle && (Math.Abs(_targetGazeX) > 0.5 || Math.Abs(_targetGazeY) > 0.5))
        {
            EnsureActiveTimer();
        }
    }

    private void OnBlinkTrigger(object? sender, EventArgs e)
    {
        if (_state == PinnState.Sleeping) return;

        _isBlinking = true;
        _blinkProgress = 0.0;
        EnsureActiveTimer();
        _blinkTimer.Interval = TimeSpan.FromSeconds(2.5 + _random.NextDouble() * 3.0);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        bool needsRedraw = false;

        // Smooth eye gaze tracking (Lerp)
        if (Math.Abs(_gazeX - _targetGazeX) > 0.05 || Math.Abs(_gazeY - _targetGazeY) > 0.05)
        {
            _gazeX += (_targetGazeX - _gazeX) * 0.25;
            _gazeY += (_targetGazeY - _gazeY) * 0.25;
            needsRedraw = true;
        }

        // Blinking animation
        if (_isBlinking)
        {
            _blinkProgress += 0.22;
            if (_blinkProgress >= 1.0)
            {
                _isBlinking = false;
                _blinkProgress = 0.0;
            }
            needsRedraw = true;
        }

        // Dancing (headbob sine wave)
        if (IsDancing || _state == PinnState.Dancing)
        {
            _danceTime += 0.16;
            _bounceY = Math.Sin(_danceTime * 3.5) * 2.5;
            needsRedraw = true;
        }
        else if (_bounceY < -0.1 || _bounceY > 0.1)
        {
            // Spring back from happy bounce
            _bounceY *= 0.75;
            if (Math.Abs(_bounceY) < 0.1)
            {
                _bounceY = 0;
                if (_state == PinnState.Happy) State = PinnState.Idle;
            }
            needsRedraw = true;
        }

        // Floating heart decay
        if (_heartOpacity > 0.01)
        {
            _heartOpacity *= 0.90;
            _heartY -= 0.6;
            needsRedraw = true;
        }

        // Sleeping Zzz animation
        if (_state == PinnState.Sleeping)
        {
            _sleepZTime += 0.06;
            needsRedraw = true;
        }

        if (needsRedraw)
        {
            InvalidateVisual();
        }
        else
        {
            StopActiveTimerIfIdle();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        TriggerHappyBounce();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double cx = ActualWidth / 2.0;
        double cy = ActualHeight / 2.0 + _bounceY + 3.0;

        // --- 1. EARS ---
        // Left Ear
        var leftEar = new PathGeometry();
        var leftEarFig = new PathFigure { StartPoint = new Point(cx - 15, cy - 7), IsClosed = true, IsFilled = true };
        leftEarFig.Segments.Add(new LineSegment(new Point(cx - 20, cy - 20), true)); // ear tip
        leftEarFig.Segments.Add(new LineSegment(new Point(cx - 8, cy - 13), true));
        leftEar.Figures.Add(leftEarFig);
        dc.DrawGeometry(CatBodyBrush, LinePen, leftEar);

        // Left Inner Ear (Pink)
        var leftInnerEar = new PathGeometry();
        var leftInnerFig = new PathFigure { StartPoint = new Point(cx - 14, cy - 8), IsClosed = true, IsFilled = true };
        leftInnerFig.Segments.Add(new LineSegment(new Point(cx - 18, cy - 17), true));
        leftInnerFig.Segments.Add(new LineSegment(new Point(cx - 9, cy - 12), true));
        leftInnerEar.Figures.Add(leftInnerFig);
        dc.DrawGeometry(CatInnerEarBrush, null, leftInnerEar);

        // Right Ear
        var rightEar = new PathGeometry();
        var rightEarFig = new PathFigure { StartPoint = new Point(cx + 8, cy - 13), IsClosed = true, IsFilled = true };
        rightEarFig.Segments.Add(new LineSegment(new Point(cx + 20, cy - 20), true)); // ear tip
        rightEarFig.Segments.Add(new LineSegment(new Point(cx + 15, cy - 7), true));
        rightEar.Figures.Add(rightEarFig);
        dc.DrawGeometry(CatBodyBrush, LinePen, rightEar);

        // Right Inner Ear (Pink)
        var rightInnerEar = new PathGeometry();
        var rightInnerFig = new PathFigure { StartPoint = new Point(cx + 9, cy - 12), IsClosed = true, IsFilled = true };
        rightInnerFig.Segments.Add(new LineSegment(new Point(cx + 18, cy - 17), true));
        rightInnerFig.Segments.Add(new LineSegment(new Point(cx + 14, cy - 8), true));
        rightInnerEar.Figures.Add(rightInnerFig);
        dc.DrawGeometry(CatInnerEarBrush, null, rightInnerEar);

        // --- 2. CHUBBY HEAD & BODY ---
        // Soft rounded chubby head
        dc.DrawEllipse(CatBodyBrush, LinePen, new Point(cx, cy), 20, 15);

        // --- 3. CHEEK BLUSH ---
        dc.DrawEllipse(CatBlushBrush, null, new Point(cx - 13, cy + 3.5), 3.5, 2.2);
        dc.DrawEllipse(CatBlushBrush, null, new Point(cx + 13, cy + 3.5), 3.5, 2.2);

        // --- 4. WHISKERS ---
        // Left whiskers
        dc.DrawLine(WhiskerPen, new Point(cx - 17, cy + 0.5), new Point(cx - 24, cy - 1.5));
        dc.DrawLine(WhiskerPen, new Point(cx - 17, cy + 3.5), new Point(cx - 25, cy + 4.5));

        // Right whiskers
        dc.DrawLine(WhiskerPen, new Point(cx + 17, cy + 0.5), new Point(cx + 24, cy - 1.5));
        dc.DrawLine(WhiskerPen, new Point(cx + 17, cy + 3.5), new Point(cx + 25, cy + 4.5));

        // --- 5. EYES & GAZE ---
        double eyeY = cy - 1.5;
        double leftEyeX = cx - 7.5;
        double rightEyeX = cx + 7.5;

        if (_state == PinnState.Sleeping)
        {
            // Sleeping eyes: gentle curved lines (˘  ˘)
            DrawSleepingEye(dc, leftEyeX, eyeY + 1.5);
            DrawSleepingEye(dc, rightEyeX, eyeY + 1.5);

            // Floating "Zzz"
            DrawSleepingZzz(dc, cx + 14, cy - 14);
        }
        else if (_isBlinking)
        {
            // Blink: curved closed line (⌒  ⌒)
            DrawBlinkEye(dc, leftEyeX, eyeY);
            DrawBlinkEye(dc, rightEyeX, eyeY);
        }
        else if (_state == PinnState.Happy)
        {
            // Happy eyes: joyful curves (^  ^)
            DrawHappyEye(dc, leftEyeX, eyeY);
            DrawHappyEye(dc, rightEyeX, eyeY);
        }
        else
        {
            // Normal / Gaze tracking open eyes
            DrawGazeEye(dc, leftEyeX, eyeY, _gazeX, _gazeY);
            DrawGazeEye(dc, rightEyeX, eyeY, _gazeX, _gazeY);
        }

        // --- 6. NOSE & MOUTH ---
        // Tiny cute pink nose
        dc.DrawEllipse(CatNoseBrush, null, new Point(cx, cy + 2.2), 1.5, 1.1);

        if (_state == PinnState.DragHover)
        {
            // Open excited mouth (ready to catch file!)
            dc.DrawEllipse(CatInnerEarBrush, LinePen, new Point(cx, cy + 5.5), 3.0, 3.5);
        }
        else
        {
            // Sweet cat 'ω' mouth
            DrawCatMouth(dc, cx, cy + 3.2);
        }

        // --- 7. PAWS (resting on the notch ledge) ---
        dc.DrawEllipse(CatBodyBrush, LinePen, new Point(cx - 9, cy + 14.0), 3.8, 2.6);
        dc.DrawEllipse(CatBodyBrush, LinePen, new Point(cx + 9, cy + 14.0), 3.8, 2.6);

        // --- 8. HEADPHONES & MUSIC NOTE (Dancing mode) ---
        if (IsDancing || _state == PinnState.Dancing)
        {
            // Headphone band over head
            var bandGeo = new PathGeometry();
            var bandFig = new PathFigure { StartPoint = new Point(cx - 19, cy - 3), IsClosed = false };
            bandFig.Segments.Add(new BezierSegment(
                new Point(cx - 16, cy - 23),
                new Point(cx + 16, cy - 23),
                new Point(cx + 19, cy - 3), true));
            bandGeo.Figures.Add(bandFig);
            dc.DrawGeometry(null, HeadphoneBandPen, bandGeo);

            // Left & right ear cups
            dc.DrawRoundedRectangle(HeadphoneCupBrush, LinePen, new Rect(cx - 23, cy - 6, 5, 10), 2.5, 2.5);
            dc.DrawRoundedRectangle(HeadphoneCupBrush, LinePen, new Rect(cx + 18, cy - 6, 5, 10), 2.5, 2.5);

            // Floating musical note (♪)
            DrawMusicNote(dc, cx + 22, cy - 14 + Math.Sin(_danceTime * 4.0) * 2.0);
        }

        // --- 9. FLOATING HEART (Happy emote) ---
        if (_heartOpacity > 0.02)
        {
            DrawFloatingHeart(dc, cx, cy + _heartY, _heartOpacity);
        }
    }

    private static void DrawGazeEye(DrawingContext dc, double x, double y, double gx, double gy)
    {
        // Eye socket
        dc.DrawEllipse(CatEyeBrush, null, new Point(x, y), 3.6, 4.4);

        // Specular glossy highlight (moves opposite to pupil for depth)
        dc.DrawEllipse(EyeGlossBrush, null, new Point(x + gx * 0.35 + 1.0, y + gy * 0.35 - 1.2), 1.4, 1.4);
        dc.DrawEllipse(EyeGlossBrush, null, new Point(x + gx * 0.35 - 0.9, y + gy * 0.35 + 1.3), 0.7, 0.7);
    }

    private static void DrawBlinkEye(DrawingContext dc, double x, double y)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(x - 3.2, y + 0.5), IsClosed = false };
        fig.Segments.Add(new BezierSegment(
            new Point(x - 1.5, y - 1.5),
            new Point(x + 1.5, y - 1.5),
            new Point(x + 3.2, y + 0.5), true));
        geo.Figures.Add(fig);
        dc.DrawGeometry(null, LinePen, geo);
    }

    private static void DrawSleepingEye(DrawingContext dc, double x, double y)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(x - 3.0, y - 0.5), IsClosed = false };
        fig.Segments.Add(new BezierSegment(
            new Point(x - 1.0, y + 1.5),
            new Point(x + 1.0, y + 1.5),
            new Point(x + 3.0, y - 0.5), true));
        geo.Figures.Add(fig);
        dc.DrawGeometry(null, LinePen, geo);
    }

    private static void DrawHappyEye(DrawingContext dc, double x, double y)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(x - 3.0, y + 1.0), IsClosed = false };
        fig.Segments.Add(new LineSegment(new Point(x, y - 2.5), true));
        fig.Segments.Add(new LineSegment(new Point(x + 3.0, y + 1.0), true));
        geo.Figures.Add(fig);
        dc.DrawGeometry(null, LinePen, geo);
    }

    private static void DrawCatMouth(DrawingContext dc, double cx, double cy)
    {
        // Left curve
        var leftGeo = new PathGeometry();
        var leftFig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = false };
        leftFig.Segments.Add(new BezierSegment(
            new Point(cx - 1.5, cy + 2.0),
            new Point(cx - 3.5, cy + 1.5),
            new Point(cx - 4.0, cy + 0.2), true));
        leftGeo.Figures.Add(leftFig);
        dc.DrawGeometry(null, LinePen, leftGeo);

        // Right curve
        var rightGeo = new PathGeometry();
        var rightFig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = false };
        rightFig.Segments.Add(new BezierSegment(
            new Point(cx + 1.5, cy + 2.0),
            new Point(cx + 3.5, cy + 1.5),
            new Point(cx + 4.0, cy + 0.2), true));
        rightGeo.Figures.Add(rightFig);
        dc.DrawGeometry(null, LinePen, rightGeo);
    }

    private void DrawSleepingZzz(DrawingContext dc, double x, double y)
    {
        double phase = _sleepZTime % 3.0;
        double z1Y = y - (phase * 3.5);
        double z2Y = y - 7 - (phase * 3.5);

        var formattedText1 = new FormattedText("z",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            9, SleepZBrush, 1.0);
        dc.DrawText(formattedText1, new Point(x, z1Y));

        var formattedText2 = new FormattedText("Z",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11, SleepZBrush, 1.0);
        dc.DrawText(formattedText2, new Point(x + 5, z2Y));
    }

    private static void DrawMusicNote(DrawingContext dc, double x, double y)
    {
        // Note head
        dc.DrawEllipse(MusicNoteBrush, null, new Point(x, y), 2.2, 1.8);
        // Note stem
        dc.DrawLine(new Pen(MusicNoteBrush, 1.2), new Point(x + 1.8, y), new Point(x + 1.8, y - 6));
        // Note flag
        dc.DrawLine(new Pen(MusicNoteBrush, 1.2), new Point(x + 1.8, y - 6), new Point(x + 4.5, y - 4));
    }

    private static void DrawFloatingHeart(DrawingContext dc, double cx, double cy, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 90, 130));
        brush.Freeze();

        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(cx, cy + 3.5), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new BezierSegment(
            new Point(cx - 5.0, cy - 1.5),
            new Point(cx - 3.5, cy - 5.5),
            new Point(cx, cy - 2.5), true));
        fig.Segments.Add(new BezierSegment(
            new Point(cx + 3.5, cy - 5.5),
            new Point(cx + 5.0, cy - 1.5),
            new Point(cx, cy + 3.5), true));
        geo.Figures.Add(fig);

        dc.DrawGeometry(brush, null, geo);
    }
}
