using System.Drawing;
using Grasshopper.Kernel;
using System.Windows.Forms;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace GenericMongoPlugin.Components.Filters;

public sealed class CreateFilterComponent : GH_Component
{
    public enum FilterMode
    {
        SingleFilter = 0,
        OrFilter = 1,
        AndFilter = 2,
        SingleExcludeFilter = 3,
        OrExclude = 4,
        AndExclude = 5
    }

    private FilterMode _mode = FilterMode.SingleFilter;

    public CreateFilterComponent()
        : base("Create Filter", "Filter", "Builds a MongoDB filter from filter entries. Right-click to change filter mode. String values can be regex via re:<pattern> or /pattern/flags.", "MongoDB", "Filters")
    {
        UpdateMessage();
    }

    public override Guid ComponentGuid => new("9a11c282-2fc0-4f20-b1f1-8fe64d5b5355");

    protected override Bitmap Icon => PluginIcons.Get("component-create-filter.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoFilterEntryParam(), "Entries", "Ent", "Filter entries (connect multiple wires to create a list)", GH_ParamAccess.list);
        p[p.ParamCount - 1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddParameter(new MongoFilterParam(), "Filter", "Fil", "MongoDB filter", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var entries = new List<MongoFilterEntryGoo>();
        DA.GetDataList(0, entries);

        // Validate entry counts to avoid silently producing wrong filters.
        var validEntries = new List<MongoFilterEntryGoo>();
        foreach (var e in entries)
        {
            var key = e?.Value?.Key;
            if (!string.IsNullOrWhiteSpace(key))
                validEntries.Add(e!);
        }

        if (validEntries.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No entries provided.");
            DA.SetData(0, null);
            return;
        }

        if (_mode is FilterMode.SingleFilter or FilterMode.SingleExcludeFilter)
        {
            if (validEntries.Count > 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Single mode expects exactly 1 entry. Use OR/AND modes for multiple entries.");
                DA.SetData(0, null);
                return;
            }
        }
        else
        {
            if (validEntries.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "OR/AND modes expect 2 or more entries.");
                DA.SetData(0, null);
                return;
            }
        }

        var doc = BuildFilter(validEntries, _mode);
        DA.SetData(0, new MongoFilterGoo(new MongoFilter(doc)));
    }

    private void SetMode(FilterMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        UpdateMessage();
        ExpireSolution(true);
    }

#pragma warning disable CA1416 // Grasshopper's context menu APIs are ToolStrip-based.
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);

        Menu_AppendSeparator(menu);

        var modeMenu = new ToolStripMenuItem("Mode");
        menu.Items.Add(modeMenu);

        void AddModeItem(string label, FilterMode mode)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _mode == mode
            };

            item.Click += (_, _) => SetMode(mode);
            modeMenu.DropDownItems.Add(item);
        }

        AddModeItem("Single", FilterMode.SingleFilter);
        AddModeItem("OR", FilterMode.OrFilter);
        AddModeItem("AND", FilterMode.AndFilter);

        modeMenu.DropDownItems.Add(new ToolStripSeparator());

        AddModeItem("NOT Single", FilterMode.SingleExcludeFilter);
        AddModeItem("NOT OR", FilterMode.OrExclude);
        AddModeItem("NOT AND", FilterMode.AndExclude);
    }
#pragma warning restore CA1416

    private void UpdateMessage()
    {
        Message = _mode switch
        {
            FilterMode.SingleFilter => "Single",
            FilterMode.OrFilter => "OR",
            FilterMode.AndFilter => "AND",
            FilterMode.SingleExcludeFilter => "NOT Single",
            FilterMode.OrExclude => "NOT OR",
            FilterMode.AndExclude => "NOT AND",
            _ => _mode.ToString()
        };
    }

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetInt32("FilterMode", (int)_mode);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("FilterMode"))
        {
            var v = reader.GetInt32("FilterMode");
            if (v >= 0 && v <= (int)FilterMode.AndExclude)
                _mode = (FilterMode)v;
        }

        UpdateMessage();
        return base.Read(reader);
    }

    private static BsonDocument BuildFilter(List<MongoFilterEntryGoo> entries, FilterMode mode)
    {
        var equalityDocs = new List<BsonDocument>();

        foreach (var e in entries)
        {
            var entry = e?.Value;
            var key = entry?.Key;
            if (string.IsNullOrWhiteSpace(key)) continue;

            var val = entry!.Value;

            // Special virtual key: $id maps to MongoDB's document _id.
            if (key == "$id")
            {
                equalityDocs.Add(new BsonDocument("_id", CoerceObjectIdValue(val)));
                continue;
            }

            // Regex support for string values:
            // - re:<pattern>
            // - /pattern/flags (flags like i,m,s,x)
            if (val is BsonString bs && TryParseRegexString(bs.AsString, out var regex))
                equalityDocs.Add(new BsonDocument($"attrs.{key}", regex));
            else
                equalityDocs.Add(new BsonDocument($"attrs.{key}", val));
        }

        BsonDocument include;

        switch (mode)
        {
            case FilterMode.SingleFilter:
            case FilterMode.SingleExcludeFilter:
                if (equalityDocs.Count == 0) return new BsonDocument();
                include = equalityDocs[0];
                break;

            case FilterMode.OrFilter:
            case FilterMode.OrExclude:
                if (equalityDocs.Count == 0) return new BsonDocument();
                if (equalityDocs.Count == 1) { include = equalityDocs[0]; break; }
                include = new BsonDocument("$or", new BsonArray(equalityDocs));
                break;

            case FilterMode.AndFilter:
            case FilterMode.AndExclude:
                if (equalityDocs.Count == 0) return new BsonDocument();
                if (equalityDocs.Count == 1) { include = equalityDocs[0]; break; }
                include = new BsonDocument("$and", new BsonArray(equalityDocs));
                break;

            default:
                return new BsonDocument();
        }

        if (mode is FilterMode.SingleExcludeFilter or FilterMode.OrExclude or FilterMode.AndExclude)
        {
            // Exclude = NOT(include). Using $nor makes this easy and works for composite filters.
            return new BsonDocument("$nor", new BsonArray { include });
        }

        return include;
    }

    private static BsonValue CoerceObjectIdValue(BsonValue value)
    {
        if (value == null || value.IsBsonNull) return BsonNull.Value;

        // Handle range docs too ({$gte:..., $lte:...}).
        if (value.IsBsonDocument)
        {
            var doc = value.AsBsonDocument;
            if (doc.Contains("$gte") || doc.Contains("$lte"))
            {
                var outDoc = new BsonDocument();
                if (doc.TryGetValue("$gte", out var gte)) outDoc["$gte"] = CoerceObjectIdValue(gte);
                if (doc.TryGetValue("$lte", out var lte)) outDoc["$lte"] = CoerceObjectIdValue(lte);
                return outDoc;
            }
        }

        if (!value.IsString) return value;

        var s = (value.AsString ?? string.Empty).Trim();
        if (s.Length == 0) return value;

        // Accept common string forms:
        // - "507f1f77bcf86cd799439011"
        // - "ObjectId(\"507f...\")"
        // - "ObjectId('507f...')"
        if (s.StartsWith("ObjectId(", StringComparison.OrdinalIgnoreCase) && s.EndsWith(")"))
        {
            s = s.Substring("ObjectId(".Length, s.Length - "ObjectId(".Length - 1).Trim();
            if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
                s = s[1..^1];
            s = s.Trim();
        }

        if (ObjectId.TryParse(s, out var oid))
            return new BsonObjectId(oid);

        // Also accept extended JSON like {"$oid":"..."} if user pasted it as a string.
        if (s.StartsWith("{") && s.Contains("$oid", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parsed = BsonSerializer.Deserialize<BsonValue>(s);
                if (parsed != null && parsed.IsBsonDocument)
                {
                    var pd = parsed.AsBsonDocument;
                    if (pd.TryGetValue("$oid", out var ov) && ov.IsString && ObjectId.TryParse(ov.AsString, out var oid2))
                        return new BsonObjectId(oid2);
                }
            }
            catch
            {
                // ignore
            }
        }

        return value;
    }

    private static bool TryParseRegexString(string? text, out BsonRegularExpression regex)
    {
        regex = default!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Explicit prefix to avoid accidental interpretation.
        if (text.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = text.Substring(3);
            if (string.IsNullOrEmpty(pattern)) return false;
            regex = new BsonRegularExpression(pattern);
            return true;
        }

        // JavaScript-style /pattern/flags
        if (text.Length < 2 || text[0] != '/') return false;

        var closingSlash = FindLastUnescapedSlash(text);
        if (closingSlash <= 0) return false;
        if (closingSlash == 0 || closingSlash == text.Length - 1)
        {
            // "/pattern/" is allowed (no flags). "/" or "/pattern" is not.
            if (closingSlash <= 1) return false;
        }

        var rawPattern = text.Substring(1, closingSlash - 1);
        var options = closingSlash < text.Length - 1 ? text.Substring(closingSlash + 1) : string.Empty;

        // Unescape \/ so users can type / in the pattern while still using /.../ syntax.
        rawPattern = rawPattern.Replace("\\/", "/");

        if (string.IsNullOrEmpty(options))
            regex = new BsonRegularExpression(rawPattern);
        else
            regex = new BsonRegularExpression(rawPattern, options);

        return true;
    }

    private static int FindLastUnescapedSlash(string text)
    {
        // Finds the last '/' that is not escaped as '\/'.
        for (var i = text.Length - 1; i >= 1; i--)
        {
            if (text[i] != '/') continue;
            if (text[i - 1] == '\\') continue;
            return i;
        }

        return -1;
    }
}
