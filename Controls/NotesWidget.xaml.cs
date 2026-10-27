using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

public class TodoTaskItem
{
    public string Text { get; set; } = "";
    public bool IsDone { get; set; }
}

public class NotesStorageModel
{
    public string NoteText { get; set; } = "";
    public List<TodoTaskItem> Tasks { get; set; } = new();
}

public partial class NotesWidget : UserControl
{
    private readonly DispatcherTimer _debounceSaveTimer;
    private readonly List<TodoTaskItem> _tasks = new();
    private bool _isLoaded = false;

    private static string StorageFilePath => Path.Combine(AppPaths.RoamingAppDataRoot, "notes.json");

    public NotesWidget()
    {
        InitializeComponent();

        _debounceSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _debounceSaveTimer.Tick += (s, e) =>
        {
            _debounceSaveTimer.Stop();
            SaveData();
        };

        Loaded += NotesWidget_Loaded;
    }

    private void NotesWidget_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) return;
        _isLoaded = true;
        LoadData();
    }

    private void LoadData()
    {
        try
        {
            string path = StorageFilePath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<NotesStorageModel>(json);
                if (data != null)
                {
                    NotesTextBox.Text = data.NoteText ?? "";
                    NotesPlaceholder.Visibility = string.IsNullOrEmpty(NotesTextBox.Text)
                        ? Visibility.Visible : Visibility.Collapsed;

                    _tasks.Clear();
                    if (data.Tasks != null)
                    {
                        _tasks.AddRange(data.Tasks);
                    }
                    RebuildTasksUI();
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("NOTES", ex, "Failed to load notes data");
        }
    }

    private void SaveData()
    {
        try
        {
            string path = StorageFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var model = new NotesStorageModel
            {
                NoteText = NotesTextBox.Text ?? "",
                Tasks = _tasks
            };

            string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("NOTES", ex, "Failed to save notes data");
        }
    }

    private void TriggerDebouncedSave()
    {
        _debounceSaveTimer.Stop();
        _debounceSaveTimer.Start();
    }

    private void NotesTabBtn_Click(object sender, MouseButtonEventArgs e)
    {
        NotesPanel.Visibility = Visibility.Visible;
        TodoPanel.Visibility = Visibility.Collapsed;

        NotesTabBtn.Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
        NotesTabText.Foreground = UiPalette.PrimaryBrush;
        NotesTabText.FontWeight = FontWeights.SemiBold;

        TodoTabBtn.Background = Brushes.Transparent;
        TodoTabText.Foreground = UiPalette.SecondaryBrush;
        TodoTabText.FontWeight = FontWeights.Normal;
    }

    private void TodoTabBtn_Click(object sender, MouseButtonEventArgs e)
    {
        NotesPanel.Visibility = Visibility.Collapsed;
        TodoPanel.Visibility = Visibility.Visible;

        TodoTabBtn.Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
        TodoTabText.Foreground = UiPalette.PrimaryBrush;
        TodoTabText.FontWeight = FontWeights.SemiBold;

        NotesTabBtn.Background = Brushes.Transparent;
        NotesTabText.Foreground = UiPalette.SecondaryBrush;
        NotesTabText.FontWeight = FontWeights.Normal;
    }

    private void NotesTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        NotesPlaceholder.Visibility = string.IsNullOrEmpty(NotesTextBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        TriggerDebouncedSave();
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        TaskPlaceholder.Visibility = string.IsNullOrEmpty(NewTaskBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;

        if (e.Key == Key.Enter)
        {
            AddTask();
        }
    }

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        AddTask();
    }

    private void AddTask()
    {
        string text = (NewTaskBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        var task = new TodoTaskItem { Text = text, IsDone = false };
        _tasks.Add(task);
        NewTaskBox.Text = "";
        TaskPlaceholder.Visibility = Visibility.Visible;

        RebuildTasksUI();
        TriggerDebouncedSave();
    }

    private void RebuildTasksUI()
    {
        TasksListStackPanel.Children.Clear();

        for (int i = 0; i < _tasks.Count; i++)
        {
            int index = i;
            var item = _tasks[i];

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new CheckBox
            {
                IsChecked = item.IsDone,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            check.Click += (s, e) =>
            {
                item.IsDone = check.IsChecked == true;
                RebuildTasksUI();
                TriggerDebouncedSave();
            };

            var text = new TextBlock
            {
                Text = item.Text,
                FontSize = 12,
                Foreground = item.IsDone ? UiPalette.TertiaryBrush : UiPalette.PrimaryBrush,
                TextDecorations = item.IsDone ? TextDecorations.Strikethrough : null,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var delBtn = new Button
            {
                Content = "✕",
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 100, 100)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2)
            };
            delBtn.Click += (s, e) =>
            {
                _tasks.RemoveAt(index);
                RebuildTasksUI();
                TriggerDebouncedSave();
            };

            Grid.SetColumn(check, 0);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(delBtn, 2);

            row.Children.Add(check);
            row.Children.Add(text);
            row.Children.Add(delBtn);

            TasksListStackPanel.Children.Add(row);
        }
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (NotesPanel.Visibility == Visibility.Visible)
            {
                Clipboard.SetText(NotesTextBox.Text ?? "");
            }
            else
            {
                var lines = new List<string>();
                foreach (var t in _tasks)
                {
                    lines.Add($"[{(t.IsDone ? "x" : " ")}] {t.Text}");
                }
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("NOTES", ex, "Failed to copy to clipboard");
        }
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        if (NotesPanel.Visibility == Visibility.Visible)
        {
            NotesTextBox.Text = "";
            NotesPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            _tasks.Clear();
            RebuildTasksUI();
        }
        TriggerDebouncedSave();
    }
}
