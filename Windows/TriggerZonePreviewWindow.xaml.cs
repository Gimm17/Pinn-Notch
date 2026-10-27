using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VNotch.Windows;

public partial class TriggerZonePreviewWindow : Window
{
    private static TriggerZonePreviewWindow? _instance;
    private readonly DispatcherTimer _fadeTimer;

    public TriggerZonePreviewWindow()
    {
        InitializeComponent();
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _fadeTimer.Tick += (_, _) =>
        {
            _fadeTimer.Stop();
            FadeOut();
        };
    }

    public static void ShowPreview(Rect screenRect, string label)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (_instance == null)
            {
                _instance = new TriggerZonePreviewWindow();
            }

            _instance.Left = Math.Max(0, screenRect.Left);
            _instance.Top = Math.Max(0, screenRect.Top);
            _instance.Width = Math.Max(40, screenRect.Width);
            _instance.Height = Math.Max(12, screenRect.Height);
            _instance.TagText.Text = label;

            _instance.Opacity = 1.0;
            if (!_instance.IsVisible)
            {
                _instance.Show();
            }

            _instance._fadeTimer.Stop();
            _instance._fadeTimer.Start();
        });
    }

    public static void HidePreview()
    {
        _instance?.FadeOut();
    }

    private void FadeOut()
    {
        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new QuadraticEase()
        };
        anim.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, anim);
    }
}
