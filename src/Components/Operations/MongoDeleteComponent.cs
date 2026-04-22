using System.Drawing;
using Grasshopper.Kernel;
using System.Windows.Forms;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GenericMongoPlugin.Components;

public sealed class MongoDeleteComponent : GH_Component
{
    public enum DeleteMode
    {
        Geometry = 0,
        Generic = 1
    }

    private DeleteMode _mode = DeleteMode.Geometry;

    public MongoDeleteComponent()
        : base("Mongo Delete", "MongoDelete", "Deletes documents matching filters (switch mode via right-click). Deletion is only possible if filters are provided.", "MongoDB", "Operations")
    {
        UpdateMessage();
    }

    public override Guid ComponentGuid => new("0c5f7e9a-6514-4743-b4db-e6f04cc4053a");

    protected override Bitmap Icon => PluginIcons.Get("component-delete-mongo.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "MongoDB connection to use for the query", GH_ParamAccess.item);
        p.AddTextParameter("Collection", "Col", "Collection name", GH_ParamAccess.item);
        p.AddParameter(new MongoFilterParam(), "Filters", "Fil", "Filters (connect multiple wires to create a list)", GH_ParamAccess.list);
        p[p.ParamCount - 1].Optional = true;
        p.AddIntegerParameter("Limit", "Lim", "Max documents to delete. 0 = no limit (delete all matches).", GH_ParamAccess.item, 0);
        p.AddBooleanParameter("Run", "Run", "Trigger - Avoids querying before the user added all parameters.", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("Log", "Log", "Logs/errors", GH_ParamAccess.item);
        p.AddIntegerParameter("Deleted", "DelN", "Number of deleted documents", GH_ParamAccess.item);
    }

    private void UpdateMessage()
    {
        Message = _mode switch
        {
            DeleteMode.Geometry => "Geometry",
            DeleteMode.Generic => "Generic",
            _ => _mode.ToString()
        };
    }

    private void SetMode(DeleteMode mode)
    {
        if (_mode == mode) return;
        RecordUndoEvent("Change Mongo Delete Mode");
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

        void AddModeItem(string label, DeleteMode mode)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _mode == mode
            };
            item.Click += (_, _) => SetMode(mode);
            modeMenu.DropDownItems.Add(item);
        }

        AddModeItem("Geometry", DeleteMode.Geometry);
        AddModeItem("Generic", DeleteMode.Generic);
    }
#pragma warning restore CA1416

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetInt32("DeleteMode", (int)_mode);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("DeleteMode"))
        {
            var v = reader.GetInt32("DeleteMode");
            if (v >= 0 && v <= (int)DeleteMode.Generic)
                _mode = (DeleteMode)v;
        }

        UpdateMessage();
        return base.Read(reader);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        MongoDbConnectionGoo connGoo = null!;
        string collectionName = string.Empty;
        var filters = new List<MongoFilterGoo>();
        int limit = 0;
        bool run = false;

        if (!DA.GetData(0, ref connGoo) || connGoo?.Value == null) return;
        if (!DA.GetData(1, ref collectionName)) return;
        DA.GetDataList(2, filters);
        DA.GetData(3, ref limit);
        DA.GetData(4, ref run);

        if (!run) return;

        if (!connGoo.Value.IsValid)
        {
            DA.SetData(0, "Error: Invalid connection.");
            return;
        }

        collectionName = (collectionName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            DA.SetData(0, "Error: Collection name is required.");
            return;
        }

        // Safety: block deletes without user filters.
        var nonEmptyFilters = 0;
        foreach (var f in filters)
        {
            var d = f?.Value?.Document;
            if (d == null) continue;
            if (d.ElementCount == 0) continue;
            nonEmptyFilters++;
        }

        if (nonEmptyFilters == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No filters provided. Deletion is blocked to prevent accidental mass deletion.");
            DA.SetData(0, null);
            DA.SetData(1, 0);
            return;
        }

        try
        {
            var db = connGoo.Value.CreateDatabase();
            var col = db.GetCollection<BsonDocument>(collectionName);

            var (type, requiredField) = _mode == DeleteMode.Geometry
                ? ("geometry", "geom")
                : ("data", "data");

            var filterDoc = MongoOperations.BuildTypedQueryFilter(type, requiredField, filters);
            var filter = new BsonDocumentFilterDefinition<BsonDocument>(filterDoc);

            if (limit <= 0)
            {
                var res = col.DeleteMany(filter);
                DA.SetData(0, $"Success: Deleted {res.DeletedCount} document(s).");
                DA.SetData(1, (int)res.DeletedCount);
                return;
            }

            if (limit == 1)
            {
                var res = col.DeleteOne(filter);
                DA.SetData(0, $"Success: Deleted {res.DeletedCount} document(s).");
                DA.SetData(1, (int)res.DeletedCount);
                return;
            }

            // MongoDB DeleteMany has no limit, so do a limited find and delete by _id.
            var ids = col.Find(filter)
                .Project(Builders<BsonDocument>.Projection.Include("_id"))
                .Limit(limit)
                .ToList();

            var idValues = new BsonArray();
            foreach (var d in ids)
            {
                if (d.TryGetValue("_id", out var idVal) && !idVal.IsBsonNull)
                    idValues.Add(idVal);
            }

            if (idValues.Count == 0)
            {
                DA.SetData(0, "Success: Deleted 0 document(s). (No matches)");
                DA.SetData(1, 0);
                return;
            }

            var limitedFilter = new BsonDocument("_id", new BsonDocument("$in", idValues));
            var res2 = col.DeleteMany(new BsonDocumentFilterDefinition<BsonDocument>(limitedFilter));
            DA.SetData(0, $"Success: Deleted {res2.DeletedCount} document(s).");
            DA.SetData(1, (int)res2.DeletedCount);
        }
        catch (Exception ex)
        {
            DA.SetData(0, "Error: " + ex.Message);
            DA.SetData(1, 0);
        }
     }
}
