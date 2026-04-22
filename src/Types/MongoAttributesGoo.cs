using Grasshopper.Kernel.Types;
using MongoDB.Bson;

namespace GenericMongoPlugin.Types;

public sealed class MongoAttributesGoo : GH_Goo<MongoAttributes>
{
    public MongoAttributesGoo() { }

    public MongoAttributesGoo(MongoAttributes native) : base(native) { }

    public override bool IsValid => Value != null;

    public override string TypeName => "MongoAttributes";

    public override string TypeDescription => "A set of typed MongoDB attributes (stored under attrs)";

    public override IGH_Goo Duplicate() => Value == null ? new MongoAttributesGoo() : new MongoAttributesGoo(Value.Clone());

    public override string ToString()
    {
        var value = Value;
        if (value is null)
            return "<null attrs>";

        var doc = value.Document;
        var count = doc.ElementCount;

        if (count == 0)
            return "0 Attribute(s)";

        var parts = new List<string>(Math.Min(count, 16));
        foreach (var el in doc)
        {
            parts.Add($"{el.Name}: {FormatBsonValue(el.Value)}");
            if (parts.Count >= 16)
                break;
        }

        var summary = string.Join(", ", parts);
        if (count > parts.Count)
            summary += $", … (+{count - parts.Count})";

        // Keep tooltips readable.
        const int maxLen = 220;
        if (summary.Length > maxLen)
            summary = summary[..(maxLen - 3)] + "...";

        return $"{count} Attribute(s): {summary}";
    }

    private static string FormatBsonValue(BsonValue v)
    {
        if (v is null) return "null";
        if (v.IsBsonNull) return "null";
        if (v.IsString) return v.AsString;
        if (v.IsBoolean) return v.AsBoolean ? "true" : "false";
        if (v.IsInt32) return v.AsInt32.ToString();
        if (v.IsInt64) return v.AsInt64.ToString();
        if (v.IsDouble) return v.AsDouble.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        if (v.IsDecimal128) return v.AsDecimal128.ToString();
        if (v.IsGuid) return v.AsGuid.ToString();
        if (v.IsObjectId) return v.AsObjectId.ToString();
        if (v.IsValidDateTime)
        {
            var dt = v.ToUniversalTime();
            return dt.ToString("O");
        }

        // For arrays/documents and everything else.
        var s = v.ToString() ?? string.Empty;
        if (s.Length > 120) s = s[..117] + "...";
        return s;
    }

    public override object ScriptVariable() => Value;
}
