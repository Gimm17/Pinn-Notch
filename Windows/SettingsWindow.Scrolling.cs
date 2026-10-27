using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Smooth Scroll (60Hz / 120Hz / 144Hz Frame-Synced Spring Inertia)

    private double _scrollTarget;
    private bool _isScrollAnimating;
    private long _lastScrollTimestamp;
    private const double ScrollStepPerNotch = 86.0;

    private bool IsAnyComboBoxDropDownOpen()
    {
        if (WidgetCombo?.IsDropDownOpen == true ||
            MonitorCombo?.IsDropDownOpen == true ||
            LanguageCombo?.IsDropDownOpen == true ||
            CameraCombo?.IsDropDownOpen == true ||
            VisualizerAudioCombo?.IsDropDownOpen == true ||
            SkinCombo?.IsDropDownOpen == true ||
            GlassPresetCombo?.IsDropDownOpen == true ||
            ProcessPriorityCombo?.IsDropDownOpen == true ||
            GpuPreferenceCombo?.IsDropDownOpen == true ||
            PinnThemeCombo?.IsDropDownOpen == true ||
            DockAutoHideModeCombo?.IsDropDownOpen == true ||
            NotchAutoHideModeCombo?.IsDropDownOpen == true)
        {
            return true;
        }

        return FindVisualChildren<ComboBox>(this).Any(c => c.IsDropDownOpen);
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsAnyComboBoxDropDownOpen())
        {
            // A ComboBox popup is logically connected to this window
            var dropdownScrollViewer = FindVisualAncestor<ScrollViewer>(e.OriginalSource as DependencyObject);
            if (dropdownScrollViewer != null && !ReferenceEquals(dropdownScrollViewer, SettingsScrollViewer))
            {
                double target = CalculateDropDownWheelTarget(
                    dropdownScrollViewer.VerticalOffset,
                    dropdownScrollViewer.ScrollableHeight,
                    e.Delta);
                dropdownScrollViewer.ScrollToVerticalOffset(target);
            }

            // Never scroll the settings page behind an open dropdown.
            e.Handled = true;
            return;
        }

        e.Handled = true;

        double maxScroll = SettingsScrollViewer.ScrollableHeight;
        if (maxScroll <= 0) return;

        // Smooth normalized wheel delta
        double wheelSteps = (double)e.Delta / 120.0;
        double delta = -wheelSteps * ScrollStepPerNotch;

        if (!_isScrollAnimating)
        {
            _scrollTarget = SettingsScrollViewer.VerticalOffset;
        }

        _scrollTarget = Math.Clamp(_scrollTarget + delta, 0, maxScroll);

        if (!_isScrollAnimating)
        {
            _isScrollAnimating = true;
            _lastScrollTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += SmoothScroll_Tick;
        }
    }

    private void SettingsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // When user scrolls via scrollbar or mouse drag, keep _scrollTarget in sync
        if (!_isScrollAnimating)
        {
            _scrollTarget = SettingsScrollViewer.VerticalOffset;
        }
    }

    internal static double CalculateDropDownWheelTarget(double currentOffset, double scrollableHeight, int wheelDelta)
    {
        const double wheelScale = 0.35;
        return Math.Clamp(currentOffset - wheelDelta * wheelScale, 0, Math.Max(0, scrollableHeight));
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
                return match;

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SmoothScroll_Tick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = _lastScrollTimestamp == 0 ? 0.016 : (double)(now - _lastScrollTimestamp) / Stopwatch.Frequency;
        _lastScrollTimestamp = now;
        dt = Math.Clamp(dt, 0.001, 0.05);

        double maxScroll = SettingsScrollViewer.ScrollableHeight;
        _scrollTarget = Math.Clamp(_scrollTarget, 0, maxScroll);
        double current = SettingsScrollViewer.VerticalOffset;
        double diff = _scrollTarget - current;

        // Exponential ease-out critically damped curve (speed: 16.0 provides buttery smooth glide in ~140ms)
        double factor = 1.0 - Math.Exp(-16.0 * dt);
        double step = diff * factor;
        double newOffset = Math.Clamp(current + step, 0, maxScroll);
        SettingsScrollViewer.ScrollToVerticalOffset(newOffset);

        if (Math.Abs(_scrollTarget - newOffset) < 0.25)
        {
            SettingsScrollViewer.ScrollToVerticalOffset(_scrollTarget);
            StopSmoothScroll();
        }
    }

    public void StopSmoothScroll()
    {
        if (!_isScrollAnimating) return;
        _isScrollAnimating = false;
        _lastScrollTimestamp = 0;
        CompositionTarget.Rendering -= SmoothScroll_Tick;
    }

    #endregion
}
