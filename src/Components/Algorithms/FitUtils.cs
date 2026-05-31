using Rhino;
using Rhino.Geometry;

namespace GenericMongoPlugin.Components.Algorithms;

internal static class FitUtils
{
    public static List<Point3d> GetSamplePoints(Brep desired, int maxPoints)
    {
        // Sample points on the desired geometry (mesh vertices). Used for fast fit checks.
        // Deterministic downsampling for stable results.
        var pts = new List<Point3d>();
        if (desired == null || !desired.IsValid) return pts;

        try
        {
            var meshes = Mesh.CreateFromBrep(desired, MeshingParameters.FastRenderMesh);
            if (meshes == null || meshes.Length == 0) return pts;

            var m = new Mesh();
            foreach (var mm in meshes)
                if (mm != null) m.Append(mm);

            if (m.Vertices.Count == 0) return pts;

            int count = m.Vertices.Count;
            int step = Math.Max(1, count / Math.Max(1, maxPoints));

            for (int i = 0; i < count; i += step)
                pts.Add(m.Vertices.Point3dAt(i));

            return pts;
        }
        catch
        {
            return pts;
        }
    }

    public static double ComputeFitRatioSampled(Brep stockWorld, IReadOnlyList<Point3d> desiredSamplePointsWorld, Transform desiredToStockAlignedWorld, double tol)
    {
        // Returns fraction of sampled desired surface points that lie inside stock.
        // This is a fast proxy for volumetric fit.
        if (stockWorld == null || desiredSamplePointsWorld == null || desiredSamplePointsWorld.Count == 0)
            return 0.0;

        int inside = 0;
        int total = desiredSamplePointsWorld.Count;

        for (int i = 0; i < total; i++)
        {
            var p = desiredSamplePointsWorld[i];
            p.Transform(desiredToStockAlignedWorld);

            bool ok;
            // Sample points are on the desired surface, so treat boundary as inside.
            try { ok = stockWorld.IsPointInside(p, tol, strictlyIn: false); }
            catch { ok = false; }

            if (ok) inside++;
        }

        return MathUtils.Clamp((double)inside / total, 0.0, 1.0);
    }

    public static double ComputeFitRatioSampled(Mesh? stockMesh, Brep stockWorldFallback, IReadOnlyList<Point3d> desiredSamplePointsWorld, Transform desiredToStockAlignedWorld, double tol)
    {
        if (stockMesh == null)
            return ComputeFitRatioSampled(stockWorldFallback, desiredSamplePointsWorld, desiredToStockAlignedWorld, tol);

        if (desiredSamplePointsWorld == null || desiredSamplePointsWorld.Count == 0)
            return 0.0;

        int inside = 0;
        int total = desiredSamplePointsWorld.Count;

        for (int i = 0; i < total; i++)
        {
            var p = desiredSamplePointsWorld[i];
            p.Transform(desiredToStockAlignedWorld);

            bool ok;
            try { ok = stockMesh.IsPointInside(p, tol, strictlyIn: false); }
            catch { ok = false; }

            if (ok) inside++;
        }

        return MathUtils.Clamp((double)inside / total, 0.0, 1.0);
    }

    public static double ComputeFitRatioExactBoolean(Brep stockWorld, Brep desiredWorld, Transform desiredToStockAlignedWorld, double tol, double desiredVolume)
    {
        // Exact-ish: fraction of desired volume that intersects the stock: V(intersection) / V(desired).
        // Used sparingly due to cost.
        if (stockWorld == null || desiredWorld == null) return 0.0;
        if (desiredVolume <= RhinoMath.ZeroTolerance) return 0.0;

        try
        {
            var d = desiredWorld.DuplicateBrep();
            d.Transform(desiredToStockAlignedWorld);

            // Ensure both are reasonably solid for booleans.
            // (CapPlanarHoles is relatively cheap and often increases boolean robustness.)
            try { d = d.CapPlanarHoles(tol) ?? d; }
            catch { /* ignore */ }

            Brep s = stockWorld;
            try
            {
                var capped = stockWorld.CapPlanarHoles(tol);
                if (capped != null && capped.IsValid) s = capped;
            }
            catch
            {
                s = stockWorld;
            }

            var inter = Brep.CreateBooleanIntersection(s, d, tol);
            if (inter == null || inter.Length == 0)
                return 0.0;

            double v = 0.0;
            foreach (var b in inter)
            {
                if (b == null || !b.IsValid) continue;
                v += BeamGeometryUtils.SafeVolume(b);
            }

            if (v <= RhinoMath.ZeroTolerance)
                return 0.0;

            return MathUtils.Clamp(v / desiredVolume, 0.0, 1.0);
        }
        catch
        {
            return 0.0;
        }
    }
}
