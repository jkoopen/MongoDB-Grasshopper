using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace GenericMongoPlugin.Components.Algorithms;

internal static class BrepConversion
{
    public static Brep? ToBrep(IGH_GeometricGoo? goo)
    {
        if (goo == null) return null;

        object? v = null;
        try { v = goo.ScriptVariable(); }
        catch { v = null; }

        if (v == null) return null;

        try
        {
            switch (v)
            {
                case Brep b:
                    return b;
                case Extrusion ex:
                    return ex.ToBrep();
                case Surface s:
                    return s.ToBrep();
                case Mesh m:
                    return Brep.CreateFromMesh(m, true);
                case Box box:
                    return box.ToBrep();
                case GeometryBase gb:
                {
                    var converted = Brep.TryConvertBrep(gb);
                    if (converted != null) return converted;
                    break;
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }
}
