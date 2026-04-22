using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;

namespace GenericMongoPlugin.Components;

public sealed class MongoCustomQueryComponent : GH_Component
{
    public MongoCustomQueryComponent()
        : base(
            "Custom Query",
            "CustomQ",
            "Runs a custom query and returns decoded Grasshopper data (including geometry) with decoded attributes.",
            "MongoDB",
            "Database")
    {
    }

    public override Guid ComponentGuid => new Guid("be94cf86-54fc-4787-a14a-8f7f68056d9c");

    protected override Bitmap Icon => PluginIcons.Get("component-custom-query.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "MongoDB connection", GH_ParamAccess.item);
        p.AddTextParameter(
            "Query",
            "Q",
            "Mongo query JSON. Expected format:\n" +
            "{ collection: 'name', filter: { ... }, limit: 100 }\n" +
            "Fields: collection (required), filter (optional), limit (optional).",
            GH_ParamAccess.item,
            "{ collection: '', filter: {}, limit: 100 }");
        p.AddBooleanParameter("Run", "R", "Trigger - Avoids querying directly", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("Log", "L", "Logs/errors", GH_ParamAccess.item);
        p.AddParameter(new Param_GenericObject(), "Data", "D", "Decoded data, any datatype is returned", GH_ParamAccess.list);
        p.AddParameter(new MongoAttributesParam(), "Attributes", "A", "Attributes per item", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        MongoDbConnectionGoo connGoo = null!;
        string queryText = string.Empty;
        bool run = false;

        if (!DA.GetData(0, ref connGoo) || connGoo?.Value == null) return;
        if (!DA.GetData(1, ref queryText)) return;
        DA.GetData(2, ref run);

        if (!run) return;

        if (!connGoo.Value.IsValid)
        {
            DA.SetData(0, "Error: Invalid connection.");
            return;
        }

        queryText = (queryText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(queryText))
        {
            DA.SetData(0, "Error: Query JSON is required.");
            return;
        }

        try
        {
            if (!TryParseQuery(queryText, out var collectionName, out var filterDoc, out var limit, out var parseError))
            {
                DA.SetData(0, "Error: " + parseError);
                return;
            }

            var (log, data, attributes) = MongoOperations.QueryAny(connGoo.Value, collectionName, filterDoc, limit);
            DA.SetData(0, log);
            DA.SetDataList(1, data);
            DA.SetDataList(2, attributes);
        }
        catch (Exception ex)
        {
            DA.SetData(0, "Error: " + ex.Message);
        }
    }

    private static bool TryParseQuery(
        string queryText,
        out string collectionName,
        out BsonDocument filter,
        out int limit,
        out string error)
    {
        collectionName = string.Empty;
        filter = new BsonDocument();
        limit = 100;
        error = string.Empty;

        BsonDocument doc;
        try
        {
            doc = BsonDocument.Parse(queryText);
        }
        catch (Exception ex)
        {
            error = "Query is not valid JSON: " + ex.Message;
            return false;
        }

        if (!doc.TryGetValue("collection", out var colVal) || !colVal.IsString)
        {
            error = "Query must contain a string field 'collection'.";
            return false;
        }

        collectionName = (colVal.AsString ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            error = "Query field 'collection' cannot be empty.";
            return false;
        }

        if (doc.TryGetValue("filter", out var filterVal))
        {
            if (filterVal.IsBsonDocument)
                filter = filterVal.AsBsonDocument;
            else
            {
                error = "Query field 'filter' must be an object.";
                return false;
            }
        }

        if (doc.TryGetValue("limit", out var limitVal) && !limitVal.IsBsonNull)
        {
            try
            {
                limit = (int)limitVal.ToInt64();
            }
            catch
            {
                error = "Query field 'limit' must be a number.";
                return false;
            }
        }

        if (limit <= 0) limit = 1;
        return true;
    }
}
