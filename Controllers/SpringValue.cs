using System;

namespace VNotch.Controllers;

public struct SpringValue
{
    public double Current;
    public double Target;

    public SpringValue(double initial)
    {
        Current = initial;
        Target = initial;
    }

    /// <summary>
    /// Exponential smooth interpolation towards target, independent of frame rate.
    /// Returns true if the value is still animating, false if settled.
    /// </summary>
    public bool Update(double dt, double speed = 26.0)
    {
        double diff = Target - Current;
        if (Math.Abs(diff) < 0.0005)
        {
            Current = Target;
            return false;
        }

        double factor = 1.0 - Math.Exp(-speed * Math.Max(0.001, dt));
        Current += diff * factor;
        return true;
    }

    public void SnapTo(double val)
    {
        Current = val;
        Target = val;
    }
}
