using Rhino;
using Rhino.Geometry;

namespace GenericMongoPlugin.Components.Algorithms;

internal static class MathUtils
{
    public static double Clamp(double v, double min, double max)
        => v < min ? min : v > max ? max : v;

    public static bool Within(Interval inner, Interval outer, double tol)
        => inner.T0 >= outer.T0 - tol && inner.T1 <= outer.T1 + tol;

    public static double OverlapFraction(Interval desired, Interval stock)
    {
        // Fraction of the desired interval length that overlaps with the stock interval.
        // Returns 0 when desired length is zero.
        double desiredLen = desired.Length;
        if (desiredLen <= RhinoMath.ZeroTolerance)
            return 0.0;

        double a0 = System.Math.Max(desired.T0, stock.T0);
        double a1 = System.Math.Min(desired.T1, stock.T1);
        double inter = System.Math.Max(0.0, a1 - a0);
        return Clamp(inter / desiredLen, 0.0, 1.0);
    }

    public static int CutsNeeded(Interval desiredInBeam, Interval stockInBeam, double tol)
    {
        // For end-cuts: you need a cut wherever stock extends beyond the desired interval.
        int cuts = 0;
        if (desiredInBeam.T0 > stockInBeam.T0 + tol) cuts++;
        if (desiredInBeam.T1 < stockInBeam.T1 - tol) cuts++;
        return cuts;
    }
}
