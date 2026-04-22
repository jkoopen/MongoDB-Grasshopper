using MongoDB.Bson;
using Rhino.Geometry;

namespace GenericMongoPlugin.Utils;

public static class PlaneBsonConverter
{
    // Stored as:
    // { o:[x,y,z], x:[x,y,z], y:[x,y,z] }
    public static BsonDocument ToBson(Plane plane)
    {
        var o = plane.Origin;
        var x = plane.XAxis;
        var y = plane.YAxis;

        return new BsonDocument
        {
            { "o", new BsonArray { o.X, o.Y, o.Z } },
            { "x", new BsonArray { x.X, x.Y, x.Z } },
            { "y", new BsonArray { y.X, y.Y, y.Z } },
        };
    }

    public static bool TryFromBson(BsonValue value, out Plane plane)
    {
        plane = Plane.WorldXY;

        if (value == null || value.IsBsonNull) return false;
        if (!value.IsBsonDocument) return false;

        var doc = value.AsBsonDocument;
        if (!TryReadPoint3(doc, "o", out var o)) return false;
        if (!TryReadVector3(doc, "x", out var x)) return false;
        if (!TryReadVector3(doc, "y", out var y)) return false;

        plane = new Plane(o, x, y);
        return plane.IsValid;
    }

    private static bool TryReadVector3(BsonDocument doc, string key, out Vector3d vec)
    {
        vec = Vector3d.Unset;
        if (!doc.TryGetValue(key, out var v) || v == null || v.IsBsonNull) return false;
        if (!v.IsBsonArray) return false;

        var a = v.AsBsonArray;
        if (a.Count < 3) return false;

        if (!TryToDouble(a[0], out var x)) return false;
        if (!TryToDouble(a[1], out var y)) return false;
        if (!TryToDouble(a[2], out var z)) return false;

        vec = new Vector3d(x, y, z);
        return true;
    }

    private static bool TryReadPoint3(BsonDocument doc, string key, out Point3d pt)
    {
        pt = Point3d.Unset;
        if (!doc.TryGetValue(key, out var v) || v == null || v.IsBsonNull) return false;
        if (!v.IsBsonArray) return false;

        var a = v.AsBsonArray;
        if (a.Count < 3) return false;

        if (!TryToDouble(a[0], out var x)) return false;
        if (!TryToDouble(a[1], out var y)) return false;
        if (!TryToDouble(a[2], out var z)) return false;

        pt = new Point3d(x, y, z);
        return true;
    }

    private static bool TryToDouble(BsonValue v, out double d)
    {
        d = 0;
        if (v == null || v.IsBsonNull) return false;

        if (v.IsDouble)
        {
            d = v.AsDouble;
            return true;
        }

        if (v.IsInt32)
        {
            d = v.AsInt32;
            return true;
        }

        if (v.IsInt64)
        {
            d = v.AsInt64;
            return true;
        }

        if (v.IsDecimal128)
        {
            d = (double)v.AsDecimal128;
            return true;
        }

        return false;
    }
}
