using System.Drawing;
using Grasshopper.Kernel;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Driver;

namespace GenericMongoPlugin.Components;

public class ListCollectionsComponent : GH_Component
{
    public ListCollectionsComponent()
        : base(
            "List Collections",
            "ListCols",
            "Lists all collection names in the provided MongoDB database.",
            "MongoDB",
            "Database")
    {
    }

    public override Guid ComponentGuid => new Guid("93ad37a3-355d-468e-bf7d-254e816752bd");

    protected override Bitmap Icon => PluginIcons.Get("component-list-collections.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "MongoDB database connection to be queried", GH_ParamAccess.item);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("Collections", "C", "Collection names", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        MongoDbConnectionGoo dbGoo = null!;
        if (!DA.GetData(0, ref dbGoo)) return;

        var conn = dbGoo?.Value;
        if (conn == null || !conn.IsValid)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Database input is invalid.");
            return;
        }

        try
        {
            var db = conn.CreateDatabase();
            var cursor = db.ListCollectionNames();
            var names = cursor.ToList();
            DA.SetDataList(0, names);
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Failed to list collections: " + ex.Message);
        }
    }
}
