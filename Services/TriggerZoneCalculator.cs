using System;
using System.Windows;

namespace VNotch.Services;

public static class TriggerZoneCalculator
{
    public static Rect CalculateNotchTriggerZone(double windowLeft, double windowWidth, double notchWidth, double revealHeight, double extraWidth)
    {
        double width = Math.Max(10, notchWidth + Math.Max(0, extraWidth) * 2);
        double left = windowLeft + (windowWidth - width) / 2.0;
        double height = Math.Max(1, revealHeight);
        return new Rect(left, 0, width, height);
    }

    public static Rect CalculateDockTriggerZone(double dockLeft, double dockWidth, double screenBottom, double revealHeight, double extraWidth)
    {
        double width = Math.Max(10, dockWidth + Math.Max(0, extraWidth) * 2);
        double left = dockLeft - Math.Max(0, extraWidth);
        double height = Math.Max(1, revealHeight);
        double top = Math.Max(0, screenBottom - height);
        return new Rect(left, top, width, height);
    }

    public static bool IsPointInZone(Rect zone, Point point)
    {
        return point.X >= zone.Left && point.X <= zone.Right &&
               point.Y >= zone.Top && point.Y <= zone.Bottom;
    }
}
