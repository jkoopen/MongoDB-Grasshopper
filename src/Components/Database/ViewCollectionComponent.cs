using System.Drawing;
using Grasshopper.Kernel;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GenericMongoPlugin.Components;

public class ViewCollectionComponent : GH_Component
{
    public ViewCollectionComponent()
        : base(
            "View Collection",
            "ViewCol",
            "Shows basic statistics for a MongoDB collection.",
            "MongoDB",
            "Database")
    {
    }

    public override Guid ComponentGuid => new Guid("dd9939c4-e666-4b36-b2e2-f1672e11694d");

    protected override Bitmap Icon => PluginIcons.Get("component-view-collection.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "MongoDB database connection to be queried", GH_ParamAccess.item);
        p.AddTextParameter("Collection", "C", "Collection name", GH_ParamAccess.item);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddIntegerParameter("Entries", "N", "Number of documents in the collection", GH_ParamAccess.item);
        p.AddNumberParameter("Storage (kB)", "KB", "Storage size reported by MongoDB in kB", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        MongoDbConnectionGoo dbGoo = null!;
        string collectionName = string.Empty;

        if (!DA.GetData(0, ref dbGoo)) return;
        if (!DA.GetData(1, ref collectionName)) return;

        collectionName = (collectionName ?? string.Empty).Trim();

        var conn = dbGoo?.Value;
        if (conn == null || !conn.IsValid)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Database input is invalid.");
            return;
        }

        if (string.IsNullOrWhiteSpace(collectionName))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Collection name is required.");
            return;
        }

        try
        {
            var db = conn.CreateDatabase();

            // collStats returns sizes in bytes unless scale is set.
            // Use scale=1024 to request values in kilobytes.
            var cmd = new BsonDocument
            {
                { "collStats", collectionName },
                { "scale", 1024 }
            };

            var stats = db.RunCommand<BsonDocument>(cmd);

            var count = GetInt64(stats, "count");
            var storageKb = GetDouble(stats, "storageSize");

            DA.SetData(0, count);
            DA.SetData(1, storageKb);
        }
        catch (MongoCommandException mce)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "MongoDB command failed: " + mce.Message);
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Failed to get collection stats: " + ex.Message);
        }
    }

    private static long GetInt64(BsonDocument doc, string key)
    {
        if (doc == null) return 0;
        if (!doc.TryGetValue(key, out var v) || v.IsBsonNull) return 0;

        try
        {
            return v.ToInt64();
        }
        catch
        {
            return 0;
        }
    }

    private static double GetDouble(BsonDocument doc, string key)
    {
        if (doc == null) return 0;
        if (!doc.TryGetValue(key, out var v) || v.IsBsonNull) return 0;

        try
        {
            return v.ToDouble();
        }
        catch
        {
            // Some fields might be int64; try a safe fallback.
            try { return v.ToInt64(); } catch { return 0; }
        }
    }
}
