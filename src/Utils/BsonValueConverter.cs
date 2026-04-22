using Grasshopper.Kernel.Types;
using MongoDB.Bson;

namespace GenericMongoPlugin.Utils;

public static class BsonValueConverter
{
    public static bool TryConvert(IGH_Goo goo, out BsonValue value)
    {
        if (goo == null)
        {
            value = BsonNull.Value;
            return true;
        }

        // Grasshopper primitive goo
        if (goo is GH_String s)
        {
            value = s.Value ?? string.Empty;
            return true;
        }

        if (goo is GH_Boolean b)
        {
            value = b.Value;
            return true;
        }

        if (goo is GH_Integer i)
        {
            value = i.Value;
            return true;
        }

        if (goo is GH_Number n)
        {
            value = n.Value;
            return true;
        }

        // Fallback: treat unknown goo as string representation.
        value = goo.ToString() ?? string.Empty;
        return false;
    }
}
