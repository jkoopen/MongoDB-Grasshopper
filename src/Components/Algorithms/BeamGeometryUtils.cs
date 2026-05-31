using Rhino.Geometry;

namespace GenericMongoPlugin.Components.Algorithms;

internal static class BeamGeometryUtils
{
    public static IEnumerable<Point3d> SamplePoints(Brep b)
    {
        if (b == null) yield break;

        Point3d[]? verts = null;
        try { verts = b.DuplicateVertices(); }
        catch { verts = null; }

        if (verts != null && verts.Length > 0)
        {
            foreach (var pt in verts) yield return pt;
            yield break;
        }

        BoundingBox bb = b.GetBoundingBox(true);
        if (!bb.IsValid) yield break;

        foreach (var c in bb.GetCorners())
            yield return c;
    }

    public static Extents GetLocalExtents(Brep b, Plane localPlane, Transform preTransform)
    {
        bool any = false;
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;

        foreach (Point3d p0 in SamplePoints(b))
        {
            Point3d p = p0;
            if (!preTransform.IsIdentity) p.Transform(preTransform);

            Vector3d v = p - localPlane.Origin;
            double x = v * localPlane.XAxis;
            double y = v * localPlane.YAxis;
            double z = v * localPlane.ZAxis;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
            any = true;
        }

        if (!any)
        {
            return new Extents
            {
                X = new Interval(0, 0),
                Y = new Interval(0, 0),
                Z = new Interval(0, 0)
            };
        }

        return new Extents
        {
            X = new Interval(minX, maxX),
            Y = new Interval(minY, maxY),
            Z = new Interval(minZ, maxZ)
        };
    }

    public static double SafeVolume(Brep b)
    {
        try
        {
            return (b != null && b.IsValid) ? Math.Abs(b.GetVolume()) : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    public static double SafeOrApproxVolume(Brep b, Extents extents)
    {
        double v = SafeVolume(b);
        if (v > 0) return v;
        return Math.Abs(extents.X.Length * extents.Y.Length * extents.Z.Length);
    }

    public static Plane GetOrthoBeamPlane(Brep b)
    {
        if (b == null)
            return Plane.WorldXY;

        VolumeMassProperties? vmp = null;
        try { vmp = VolumeMassProperties.Compute(b); }
        catch { vmp = null; }

        Point3d center = (vmp != null) ? vmp.Centroid : b.GetBoundingBox(true).Center;

        var samples = new List<Vector3d>();
        foreach (Point3d p in SamplePoints(b))
        {
            Vector3d v = p - center;
            if (v.IsTiny()) continue;
            samples.Add(v);
        }

        Vector3d xDir = Vector3d.XAxis;
        if (samples.Count >= 3)
        {
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var v in samples)
            {
                xx += v.X * v.X;
                xy += v.X * v.Y;
                xz += v.X * v.Z;
                yy += v.Y * v.Y;
                yz += v.Y * v.Z;
                zz += v.Z * v.Z;
            }

            // Power iteration for dominant eigenvector.
            Vector3d v0 = new Vector3d(1, 0.3, 0.1);
            v0.Unitize();
            for (int k = 0; k < 20; k++)
            {
                Vector3d v1 = new Vector3d(
                    xx * v0.X + xy * v0.Y + xz * v0.Z,
                    xy * v0.X + yy * v0.Y + yz * v0.Z,
                    xz * v0.X + yz * v0.Y + zz * v0.Z
                );

                if (v1.IsTiny()) break;
                v1.Unitize();
                v0 = v1;
            }

            xDir = v0;
        }

        if (!xDir.Unitize()) xDir = Vector3d.XAxis;

        Vector3d yDir = Vector3d.Zero;
        try
        {
            foreach (var face in b.Faces)
            {
                Vector3d n = face.NormalAt(0.5, 0.5);
                if (!n.Unitize()) continue;
                if (Math.Abs(n * xDir) < 0.1)
                {
                    yDir = n;
                    break;
                }
            }
        }
        catch
        {
            yDir = Vector3d.Zero;
        }

        if (yDir.IsZero)
        {
            yDir = Vector3d.CrossProduct(xDir, Vector3d.ZAxis);
            if (yDir.IsTiny()) yDir = Vector3d.CrossProduct(xDir, Vector3d.YAxis);
        }

        // Orthonormalize.
        yDir = yDir - (yDir * xDir) * xDir;
        if (!yDir.Unitize()) yDir = Vector3d.YAxis;
        Vector3d zDir = Vector3d.CrossProduct(xDir, yDir);
        if (!zDir.Unitize()) zDir = Vector3d.ZAxis;
        yDir = Vector3d.CrossProduct(zDir, xDir);
        yDir.Unitize();

        return new Plane(center, xDir, yDir);
    }
}
