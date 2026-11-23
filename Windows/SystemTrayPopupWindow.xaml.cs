using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Windows;

public partial class SystemTrayPopupWindow : Window
{
    private bool _isClosing;
    private bool _isContextMenuOpen;
    private double _anchorCenterXDip;
    private double _dockPillTopDip;
    private int? _dockTopScreenPx;

    public event Action<bool>? TrayInteractionStateChanged;
    public event Action? PopupClosed;

    public SystemTrayPopupWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            if (!_isContextMenuOpen)
                CloseWithAnimation();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseWithAnimation();
            }
        };
        SizeChanged += (_, _) => UpdatePosition();
    }

    public async void ShowAt(double anchorCenterXDip, double dockPillTopDip, int? dockTopScreenPx, IReadOnlyList<TrayIconItem>? preloaded = null)
    {
        _isClosing = false;
        _anchorCenterXDip = anchorCenterXDip;
        _dockPillTopDip = dockPillTopDip;
        _dockTopScreenPx = dockTopScreenPx;

        if (preloaded != null)
            RenderIcons(preloaded);

        UpdatePosition();
        Opacity = 0.0;
        RootTranslate.Y = 12.0;
        Show();
        Activate();
        TrayInteractionStateChanged?.Invoke(true);

        var fadeAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() };
        var slideAnim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(OpacityProperty, fadeAnim);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var items = await SystemTrayService.EnumerateLiveTrayIconsAsync();
            if (_isClosing) return;
            RenderIcons(items);
            UpdatePosition();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("TRAY", $"RefreshAsync failed: {ex.Message}");
        }
    }

    public void CloseWithAnimation()
    {
        if (_isClosing || !IsVisible) return;
        _isClosing = true;

        var fadeAnim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase() };
        var slideAnim = new DoubleAnimation(10.0, TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase() };

        fadeAnim.Completed += (_, _) =>
        {
            Hide();
            _isClosing = false;
            PopupClosed?.Invoke();
            TrayInteractionStateChanged?.Invoke(false);
        };

        BeginAnimation(OpacityProperty, fadeAnim);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
    }

    private void UpdatePosition()
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 140;

        var workArea = SystemParameters.WorkArea;
        // RootGrid has Margin="14" around PopupCard. Align PopupCard center to anchor and bottom 10 DIPs above DockPill top.
        double left = Math.Clamp(_anchorCenterXDip - w * 0.5, workArea.Left + 8.0, workArea.Right - w - 8.0);
        double top = Math.Max(workArea.Top + 8.0, _dockPillTopDip - h + 14.0 - 10.0);

        Left = left;
        Top = top;
    }

    private void RenderIcons(IReadOnlyList<TrayIconItem> items)
    {
        IconsWrapPanel.Children.Clear();
        CountBadgeText.Text = $"{items.Count} aktif";

        if (items.Count == 0)
        {
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        EmptyStateText.Visibility = Visibility.Collapsed;
        foreach (var item in items)
        {
            IconsWrapPanel.Children.Add(CreateTrayTile(item));
        }
    }

    private UIElement CreateTrayTile(TrayIconItem item)
    {
        var scale = new ScaleTransform(1.0, 1.0, 21, 21);
        var tile = new Border
        {
            Width = 42,
            Height = 42,
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            RenderTransform = scale,
            ToolTip = $"{item.Tooltip}\nKlik kiri: Buka  •  Klik kanan: Menu & Exit"
        };

        if (item.Icon != null)
        {
            var img = new Image
            {
                Width = 22,
                Height = 22,
                Source = item.Icon,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            tile.Child = img;
        }
        else
        {
            string letter = string.IsNullOrWhiteSpace(item.DisplayName) ? "?" : item.DisplayName[0].ToString().ToUpperInvariant();
            tile.Child = new TextBlock
            {
                Text = letter,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        tile.MouseEnter += (_, _) =>
        {
            tile.Background = new SolidColorBrush(Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF));
            tile.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            var anim = new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };

        tile.MouseLeave += (_, _) =>
        {
            tile.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            tile.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };

        tile.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await HandleTileLeftClickAsync(tile, item);
        };

        tile.MouseRightButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await HandleTileRightClickAsync(tile, item);
        };

        return tile;
    }

    private async Task HandleTileLeftClickAsync(FrameworkElement tile, TrayIconItem item)
    {
        if (item.IsOwnProcess)
        {
            CloseWithAnimation();
            OpenPinnNotchSettings();
            return;
        }

        var anchor = GetTileScreenCenterPx(tile);
        CloseWithAnimation();

        bool invoked = await SystemTrayService.InteractWithTrayIconAsync(
            item, contextMenu: false, anchor.X, anchor.Y, _dockTopScreenPx);

        if (!invoked && !SystemTrayService.TryFocusProcessWindow(item.ExecutablePath, preferredHwnd: item.OwnerHwnd))
        {
            TryLaunchExecutable(item.ExecutablePath);
        }
    }

    private async Task HandleTileRightClickAsync(FrameworkElement tile, TrayIconItem item)
    {
        if (item.IsOwnProcess)
        {
            ShowOwnProcessContextMenu(tile);
            return;
        }

        var anchor = GetTileScreenCenterPx(tile);
        TrayInteractionStateChanged?.Invoke(true);
        try
        {
            Hide();
            PopupClosed?.Invoke();

            bool nativeMenuShown = await SystemTrayService.InteractWithTrayIconAsync(
                item, contextMenu: true, anchor.X, anchor.Y, _dockTopScreenPx);

            if (!nativeMenuShown)
            {
                ShowFallbackTrayMenu(item);
            }
        }
        finally
        {
            if (!_isContextMenuOpen)
                TrayInteractionStateChanged?.Invoke(false);
        }
    }

    private void ShowOwnProcessContextMenu(FrameworkElement placementTarget)
    {
        var menu = CreateStyledContextMenu();

        var settingsItem = CreateMenuItem("Pengaturan Pinn-Notch", "\uE713", () =>
        {
            CloseWithAnimation();
            OpenPinnNotchSettings();
        });
        var refreshItem = CreateMenuItem("Muat Ulang Tray", "\uE72C", () => _ = RefreshAsync());
        var restartItem = CreateMenuItem("Restart Pinn-Notch", "\uE777", App.RestartApplication);
        var exitItem = CreateMenuItem("Keluar dari Pinn-Notch (Exit)", "\uE7E8", () =>
        {
            CloseWithAnimation();
            _ = App.RequestShutdownAsync();
        }, isDestructive: true);

        menu.Items.Add(settingsItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkGlassSeparator") });
        menu.Items.Add(restartItem);
        menu.Items.Add(exitItem);

        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.Top;
        OpenContextMenu(menu);
    }

    private void ShowFallbackTrayMenu(TrayIconItem item)
    {
        var menu = CreateStyledContextMenu();

        menu.Items.Add(CreateMenuItem($"Buka {item.DisplayName}", "\uE8A7", () =>
        {
            if (!SystemTrayService.TryFocusProcessWindow(item.ExecutablePath, preferredHwnd: item.OwnerHwnd))
                TryLaunchExecutable(item.ExecutablePath);
        }));

        if (File.Exists(item.ExecutablePath))
        {
            menu.Items.Add(CreateMenuItem("Buka Lokasi File", "\uED25",
                () => SystemTrayService.OpenFileLocation(item.ExecutablePath)));
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkGlassSeparator") });
        menu.Items.Add(CreateMenuItem($"Tutup {item.DisplayName} (Exit)", "\uE8BB",
            () => SystemTrayService.CloseApplicationGracefully(item.ExecutablePath, preferredHwnd: item.OwnerHwnd)));
        menu.Items.Add(CreateMenuItem("Paksa Tutup (End Task)", "\uE711",
            () => SystemTrayService.ForceKillApplication(item.ExecutablePath, preferredHwnd: item.OwnerHwnd),
            isDestructive: true));

        menu.Placement = PlacementMode.MousePoint;
        OpenContextMenu(menu);
    }

    private ContextMenu CreateStyledContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkGlassContextMenu")
        };
        return menu;
    }

    private MenuItem CreateMenuItem(string header, string? glyph, Action onClick, bool isDestructive = false)
    {
        var item = new MenuItem
        {
            Header = header,
            Tag = glyph,
            Style = (Style)FindResource("DarkGlassMenuItem")
        };
        if (isDestructive)
            item.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OpenContextMenu(ContextMenu menu)
    {
        _isContextMenuOpen = true;
        TrayInteractionStateChanged?.Invoke(true);
        menu.Closed += (_, _) =>
        {
            _isContextMenuOpen = false;
            if (!IsVisible)
                TrayInteractionStateChanged?.Invoke(false);
        };
        menu.IsOpen = true;
    }

    private (int X, int Y) GetTileScreenCenterPx(FrameworkElement tile)
    {
        try
        {
            var pt = tile.PointToScreen(new Point(tile.ActualWidth * 0.5, tile.ActualHeight * 0.5));
            return ((int)Math.Round(pt.X), (int)Math.Round(pt.Y));
        }
        catch
        {
            if (Win32Interop.GetCursorPos(out var cur))
                return (cur.X, cur.Y);
            return (0, 0);
        }
    }

    private async void RefreshButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await RefreshAsync();
    }

    private static void OpenPinnNotchSettings()
    {
        if (App.Services?.GetService(typeof(MainWindow)) is MainWindow mainWindow)
            mainWindow.OpenAppSettings();
    }

    private static void TryLaunchExecutable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch { }
    }
}
