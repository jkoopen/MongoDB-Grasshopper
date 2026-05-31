using Rhino;
using Rhino.Geometry;

namespace GenericMongoPlugin.Components.Algorithms;

internal static class SawingPlaneUtils
{
    public static bool TryGetEndFacePlanes(Brep target, Plane targetFrame, double tol, out Plane start, out Plane end)
    {
        start = Plane.Unset;
        end = Plane.Unset;
        if (target == null || !target.IsValid) return false;

        double bestPos = -1.0;
        double bestNeg = -1.0;
        Plane bestPosPlane = Plane.Unset;
        Plane bestNegPlane = Plane.Unset;

        try
        {
            for (int i = 0; i < target.Faces.Count; i++)
            {
                var face = target.Faces[i];
                if (!face.IsPlanar(tol)) continue;

                if (!face.TryGetPlane(out Plane fp, tol)) continue;

                Vector3d n = fp.Normal;
                if (!n.Unitize()) continue;

                double d = n * targetFrame.XAxis; // [-1..1]
                double ad = Math.Abs(d);

                // Ignore side faces.
                if (ad < 0.5) continue;

                if (d > 0 && ad > bestPos)
                {
                    bestPos = ad;
                    bestPosPlane = fp;
                }
                else if (d < 0 && ad > bestNeg)
                {
                    bestNeg = ad;
                    bestNegPlane = fp;
                }
            }
        }
        catch
        {
            return false;
        }

        if (!bestPosPlane.IsValid || !bestNegPlane.IsValid)
            return false;

        start = bestNegPlane;
        end = bestPosPlane;
        return true;
    }

    public static bool WithinSawAngles(Plane cutPlaneWorld, Plane beamFrameWorld, double hMaxDeg, double vMaxDeg)
    {
        Vector3d n = cutPlaneWorld.Normal;
        if (!n.Unitize()) return false;

        Vector3d x = beamFrameWorld.XAxis;
        Vector3d y = beamFrameWorld.YAxis;
        Vector3d z = beamFrameWorld.ZAxis;
        x.Unitize(); y.Unitize(); z.Unitize();

        double nx = n * x;
        double ny = n * y;
        double nz = n * z;

        nx = Math.Abs(nx);

        double hDeg = RhinoMath.ToDegrees(Math.Atan2(Math.Abs(ny), Math.Max(RhinoMath.ZeroTolerance, nx)));
        double vDeg = RhinoMath.ToDegrees(Math.Atan2(Math.Abs(nz), Math.Max(RhinoMath.ZeroTolerance, nx)));

        return hDeg <= hMaxDeg + 1e-9 && vDeg <= vMaxDeg + 1e-9;
    }
}
