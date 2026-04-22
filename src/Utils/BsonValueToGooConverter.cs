using Grasshopper.Kernel.Types;
using MongoDB.Bson;

namespace GenericMongoPlugin.Utils;

public static class BsonValueToGooConverter
{
    public static IGH_Goo Convert(BsonValue value)
    {
        if (value is null || value.IsBsonNull) return new GH_ObjectWrapper(null);

        if (value.IsString) return new GH_String(value.AsString);
        if (value.IsBoolean) return new GH_Boolean(value.AsBoolean);
        if (value.IsInt32) return new GH_Integer(value.AsInt32);
        if (value.IsInt64) return new GH_Number(value.AsInt64);
        if (value.IsDouble) return new GH_Number(value.AsDouble);
        if (value.IsDecimal128)
        {
            // GH doesn't have decimal; keep precision by storing as text.
            return new GH_String(value.AsDecimal128.ToString());
        }

        if (value.IsGuid) return new GH_Guid(value.AsGuid);
        if (value.IsValidDateTime)
        {
            var dt = value.ToUniversalTime();
            return new GH_String(dt.ToString("O"));
        }

        // Fallback: preserve something meaningful.
        return new GH_String(value.ToString());
    }
}
