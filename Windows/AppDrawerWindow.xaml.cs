using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Windows;

public partial class AppDrawerWindow : Window
{
    private readonly AppFolderService _folderService = AppFolderService.Instance;
    private AppFolder? _activeFolder;
    private bool _isClosing;

    public AppDrawerWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => CloseWithAnimation();
        KeyDown += AppDrawerWindow_KeyDown;
        _folderService.FoldersChanged += () => Dispatcher.Invoke(RefreshCurrentView);
    }

    private void AppDrawerWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_activeFolder != null && string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                BackToFolders();
            }
            else
            {
                CloseWithAnimation();
            }
            e.Handled = true;
        }
    }

    public void ShowAt(double screenX, double screenY)
    {
        _isClosing = false;
        _activeFolder = null;
        SearchTextBox.Text = "";
        ShowFoldersView();

        // Ensure popup stays on screen horizontally, and floats strictly above dock vertically
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double left = Math.Clamp(screenX, 16.0, screenWidth - Width - 16.0);
        double top = Math.Max(16.0, screenY);

        Left = left;
        Top = top;

        Opacity = 0.0;
        RootTranslate.Y = 16.0;
        Show();
        Activate();

        // Animate appearance
        var fadeAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
        var slideAnim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        BeginAnimation(OpacityProperty, fadeAnim);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
    }

    public void CloseWithAnimation()
    {
        if (_isClosing || !IsVisible) return;
        _isClosing = true;

        var fadeAnim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        var slideAnim = new DoubleAnimation(12.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };

        fadeAnim.Completed += (_, _) =>
        {
            Hide();
            _isClosing = false;
        };

        BeginAnimation(OpacityProperty, fadeAnim);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
    }

    private void RefreshCurrentView()
    {
        if (_activeFolder != null)
        {
            var updated = _folderService.Folders.FirstOrDefault(f => f.Id == _activeFolder.Id);
            if (updated != null)
            {
                _activeFolder = updated;
                RenderActiveFolderApps();
            }
            else
            {
                ShowFoldersView();
            }
        }
        else
        {
            RenderFolders();
        }
    }

    private void ShowFoldersView()
    {
        _activeFolder = null;
        MainHeaderGrid.Visibility = Visibility.Visible;
        FolderHeaderGrid.Visibility = Visibility.Collapsed;
        FolderScrollViewer.Visibility = Visibility.Visible;
        AppsScrollViewer.Visibility = Visibility.Collapsed;
        SearchResultsScrollViewer.Visibility = Visibility.Collapsed;
        RenderFolders();
    }

    private void RenderFolders()
    {
        FoldersPanel.Children.Clear();
        var folders = _folderService.Folders;

        foreach (var folder in folders)
        {
            var tile = CreateFolderTile(folder);
            FoldersPanel.Children.Add(tile);
        }
    }

    private UIElement CreateFolderTile(AppFolder folder)
    {
        var container = new StackPanel
        {
            Width = 96,
            Height = 118,
            Margin = new Thickness(6, 6, 6, 6),
            Cursor = Cursors.Hand
        };

        var scale = new ScaleTransform(1.0, 1.0, 44, 44);
        container.RenderTransform = scale;

        // Squircle Box (iOS style)
        var squircle = new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10)
        };

        // 2x2 Mini Preview Grid of App Icons
        var grid = new UniformGrid { Rows = 2, Columns = 2 };
        int previewCount = Math.Min(4, folder.Apps.Count);

        for (int i = 0; i < 4; i++)
        {
            var slot = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(2)
            };

            if (i < previewCount)
            {
                var app = folder.Apps[i];
                var iconImg = AppFolderService.ResolveAppIcon(app.TargetPath, small: true);
                if (iconImg != null)
                {
                    slot.Background = new ImageBrush(iconImg) { Stretch = Stretch.UniformToFill };
                }
                else
                {
                    slot.Background = new SolidColorBrush(Color.FromArgb(0x50, 0x50, 0x60, 0x80));
                    slot.Child = new TextBlock
                    {
                        Text = app.Name.Length > 0 ? app.Name[0].ToString().ToUpper() : "•",
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
            }
            else
            {
                slot.Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
            }
            grid.Children.Add(slot);
        }

        squircle.Child = grid;
        container.Children.Add(squircle);

        // Folder Title
        var title = new TextBlock
        {
            Text = folder.Name,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0)
        };
        container.Children.Add(title);

        // Hover animations
        container.MouseEnter += (_, _) =>
        {
            var anim = new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(130)) { EasingFunction = new QuadraticEase() };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            squircle.Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            squircle.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        };

        container.MouseLeave += (_, _) =>
        {
            var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(130)) { EasingFunction = new QuadraticEase() };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            squircle.Background = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            squircle.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        };

        // Click to open
        container.MouseLeftButtonUp += (_, _) => OpenFolder(folder);

        // Context Menu
        var cm = new ContextMenu();
        var miOpen = new MenuItem { Header = "Buka Folder" };
        miOpen.Click += (_, _) => OpenFolder(folder);
        cm.Items.Add(miOpen);

        var miRename = new MenuItem { Header = "Ubah Nama..." };
        miRename.Click += (_, _) => PromptRenameFolder(folder);
        cm.Items.Add(miRename);

        var miDelete = new MenuItem { Header = "Hapus Folder" };
        miDelete.Click += (_, _) =>
        {
            if (MessageBox.Show($"Hapus folder '{folder.Name}'?", "Konfirmasi", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _folderService.DeleteFolder(folder.Id);
            }
        };
        cm.Items.Add(miDelete);
        container.ContextMenu = cm;

        return container;
    }

    private void OpenFolder(AppFolder folder)
    {
        _activeFolder = folder;
        MainHeaderGrid.Visibility = Visibility.Collapsed;
        FolderHeaderGrid.Visibility = Visibility.Visible;
        ActiveFolderTitle.Text = folder.Name;

        FolderScrollViewer.Visibility = Visibility.Collapsed;
        SearchResultsScrollViewer.Visibility = Visibility.Collapsed;
        AppsScrollViewer.Visibility = Visibility.Visible;

        RenderActiveFolderApps();
    }

    private void RenderActiveFolderApps()
    {
        if (_activeFolder == null) return;
        AppsPanel.Children.Clear();

        foreach (var app in _activeFolder.Apps)
        {
            var tile = CreateAppTile(app, _activeFolder);
            AppsPanel.Children.Add(tile);
        }

        // Add App Card [+]
        var addCard = CreateAddAppTile(_activeFolder);
        AppsPanel.Children.Add(addCard);
    }

    private UIElement CreateAppTile(AppItem app, AppFolder parentFolder)
    {
        var container = new Border
        {
            Width = 84,
            Height = 92,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var scale = new ScaleTransform(1.0, 1.0, 24, 24);
        stack.RenderTransform = scale;

        // App Icon
        var iconBorder = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x40, 0x48, 0x60)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var icon = AppFolderService.ResolveAppIcon(app.TargetPath, small: false);
        if (icon != null)
        {
            iconBorder.Child = new Image
            {
                Source = icon,
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            iconBorder.Child = new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[0].ToString().ToUpper() : "•",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        stack.Children.Add(iconBorder);

        // App Name
        var label = new TextBlock
        {
            Text = app.Name,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 30,
            Margin = new Thickness(0, 6, 0, 0)
        };
        stack.Children.Add(label);

        container.Child = stack;

        // Hover
        container.MouseEnter += (_, _) =>
        {
            var anim = new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            container.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        };

        container.MouseLeave += (_, _) =>
        {
            var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            container.Background = Brushes.Transparent;
        };

        // Click to launch
        container.MouseLeftButtonUp += (_, _) =>
        {
            double anchorRight = Left + Width - 16;
            double anchorTop = Top + Height - 16 + 10;
            AppFolderService.LaunchApp(app, anchorRight, anchorTop);
            CloseWithAnimation();
        };

        // Context Menu
        var cm = new ContextMenu();
        var miLaunch = new MenuItem { Header = "Buka Aplikasi" };
        miLaunch.Click += (_, _) =>
        {
            double anchorRight = Left + Width - 16;
            double anchorTop = Top + Height - 16 + 10;
            AppFolderService.LaunchApp(app, anchorRight, anchorTop);
            CloseWithAnimation();
        };
        cm.Items.Add(miLaunch);

        var miRemove = new MenuItem { Header = "Hapus dari Folder" };
        miRemove.Click += (_, _) => _folderService.RemoveAppFromFolder(parentFolder.Id, app.Id);
        cm.Items.Add(miRemove);

        container.ContextMenu = cm;

        return container;
    }

    private UIElement CreateAddAppTile(AppFolder folder)
    {
        var container = new Border
        {
            Width = 84,
            Height = 92,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconBorder = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        iconBorder.Child = new TextBlock
        {
            Text = "+",
            FontSize = 24,
            FontWeight = FontWeights.Light,
            Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0)
        };
        stack.Children.Add(iconBorder);

        stack.Children.Add(new TextBlock
        {
            Text = "Tambah",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        });

        container.Child = stack;

        container.MouseEnter += (_, _) =>
        {
            container.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            iconBorder.BorderBrush = Brushes.White;
        };
        container.MouseLeave += (_, _) =>
        {
            container.Background = Brushes.Transparent;
            iconBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
        };

        container.MouseLeftButtonUp += (_, _) => PromptAddApp(folder);

        return container;
    }

    private void BackToFolders_Click(object sender, RoutedEventArgs e) => BackToFolders();

    private void BackToFolders()
    {
        ShowFoldersView();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        string name = "Folder Baru " + (_folderService.Folders.Count + 1);
        _folderService.AddFolder(name);
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFolder != null)
        {
            PromptAddApp(_activeFolder);
        }
    }

    private void PromptAddApp(AppFolder folder)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Pilih Aplikasi atau Shortcut (.exe, .lnk)",
            Filter = "Aplikasi & Shortcut (*.exe;*.lnk;*.bat)|*.exe;*.lnk;*.bat|Semua File (*.*)|*.*",
            Multiselect = true
        };

        if (ofd.ShowDialog() == true)
        {
            foreach (var file in ofd.FileNames)
            {
                string appName = Path.GetFileNameWithoutExtension(file);
                _folderService.AddAppToFolder(folder.Id, appName, file);
            }
        }
    }

    private void PromptRenameFolder(AppFolder folder)
    {
        var prompt = new Window
        {
            Title = "Ubah Nama Folder",
            Width = 320,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
            Foreground = Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = "Nama Folder Baru:", Margin = new Thickness(0, 0, 0, 8), Foreground = Brushes.White, FontWeight = FontWeights.Medium });
        var tb = new TextBox { Text = folder.Name, Margin = new Thickness(0, 0, 0, 14), Height = 28, Padding = new Thickness(4) };
        stack.Children.Add(tb);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnOk = new Button { Content = "Simpan", Width = 75, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        btnOk.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(tb.Text))
            {
                _folderService.RenameFolder(folder.Id, tb.Text.Trim());
            }
            prompt.Close();
        };
        var btnCancel = new Button { Content = "Batal", Width = 75, Height = 28, IsCancel = true };
        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);
        stack.Children.Add(btnPanel);

        prompt.Content = stack;
        prompt.ShowDialog();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchTextBox.Text.Trim();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(query))
        {
            if (_activeFolder != null)
            {
                AppsScrollViewer.Visibility = Visibility.Visible;
                SearchResultsScrollViewer.Visibility = Visibility.Collapsed;
            }
            else
            {
                FolderScrollViewer.Visibility = Visibility.Visible;
                SearchResultsScrollViewer.Visibility = Visibility.Collapsed;
            }
            return;
        }

        // Show search results
        FolderScrollViewer.Visibility = Visibility.Collapsed;
        AppsScrollViewer.Visibility = Visibility.Collapsed;
        SearchResultsScrollViewer.Visibility = Visibility.Visible;
        SearchResultsPanel.Children.Clear();

        var matches = new List<AppItem>();
        foreach (var f in _folderService.Folders)
        {
            foreach (var a in f.Apps)
            {
                if (a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.TargetPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(a);
                }
            }
        }

        if (matches.Count == 0)
        {
            SearchResultsPanel.Children.Add(new TextBlock
            {
                Text = "Tidak ada aplikasi yang cocok.",
                Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                FontSize = 13,
                Margin = new Thickness(20),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var app in matches)
        {
            var tile = CreateAppTile(app, _activeFolder ?? _folderService.Folders.First());
            SearchResultsPanel.Children.Add(tile);
        }
    }

    private void RootContainer_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void RootContainer_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                var targetFolder = _activeFolder ?? _folderService.Folders.FirstOrDefault();
                if (targetFolder == null)
                {
                    _folderService.AddFolder("Favorit");
                    targetFolder = _folderService.Folders.FirstOrDefault();
                }

                if (targetFolder != null)
                {
                    foreach (var file in files)
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        _folderService.AddAppToFolder(targetFolder.Id, name, file);
                    }
                }
            }
        }
    }
}
