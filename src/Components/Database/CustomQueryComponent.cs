using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Text;
using System.Text.RegularExpressions;

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

    public override Guid ComponentGuid => new("be94cf86-54fc-4787-a14a-8f7f68056d9c");

    protected override Bitmap Icon => PluginIcons.Get("component-custom-query.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "MongoDB connection to be used for the query", GH_ParamAccess.item);
        p.AddTextParameter(
            "Query",
            "Qry",
            "Mongo query text. Use either db.<name>.<method>(...) or db.getCollection(\"<name>\").<method>(...). Supported formats:\n" +
            "db.collection.find({ ... })\n" +
            "db.collection.findOne({ ... })\n" +
            "db.collection.aggregate([ ... ])\n" +
            "db.collection.countDocuments({ ... })\n" +
            "db.collection.distinct(\"field\", { ... })",
            GH_ParamAccess.item,
            "db.collection.aggregate([{ $sort: { createdAt: -1 } }, { $limit: 100 }])");
        p.AddBooleanParameter("Run", "Run", "Trigger - Avoids querying before the user added all parameters.", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("Log", "Log", "Logs/errors", GH_ParamAccess.item);
        p.AddParameter(new Param_GenericObject(), "Data", "Data", "Decoded data, any datatype is returned", GH_ParamAccess.list);
        p.AddParameter(new MongoAttributesParam(), "Attributes", "Attr", "Attributes per item", GH_ParamAccess.list);
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
            DA.SetData(0, "Error: Query text is required.");
            return;
        }

        try
        {
            if (!TryParseQuery(queryText, out var parsedQuery, out var parseError))
            {
                DA.SetData(0, "Error: " + parseError);
                return;
            }

            var (log, data, attributes) = ExecuteQuery(connGoo.Value, parsedQuery);
            DA.SetData(0, log);
            DA.SetDataList(1, data);
            DA.SetDataList(2, attributes);
        }
        catch (Exception ex)
        {
            DA.SetData(0, "Error: " + ex.Message);
        }
    }

    private enum CustomQueryKind
    {
        Find,
        FindOne,
        Aggregate,
        CountDocuments,
        Distinct
    }

    private sealed record ParsedCustomQuery(
        CustomQueryKind Kind,
        string CollectionName,
        BsonDocument Filter,
        BsonArray Pipeline,
        int Limit,
        string DistinctField);

    private static (string log, List<IGH_Goo> data, List<MongoAttributesGoo> attributes) ExecuteQuery(
        MongoDbConnection connection,
        ParsedCustomQuery query)
    {
        return query.Kind switch
        {
            CustomQueryKind.Aggregate => MongoOperations.QueryAnyAggregate(connection, query.CollectionName, query.Pipeline),
            CustomQueryKind.FindOne => MongoOperations.QueryAny(connection, query.CollectionName, query.Filter, 1),
            CustomQueryKind.CountDocuments => MongoOperations.CountDocuments(connection, query.CollectionName, query.Filter),
            CustomQueryKind.Distinct => MongoOperations.QueryDistinct(connection, query.CollectionName, query.DistinctField, query.Filter),
            _ => MongoOperations.QueryAny(connection, query.CollectionName, query.Filter, query.Limit)
        };
    }

    private static bool TryParseQuery(
        string queryText,
        out ParsedCustomQuery parsedQuery,
        out string error)
    {
        parsedQuery = new ParsedCustomQuery(CustomQueryKind.Find, string.Empty, new BsonDocument(), new BsonArray(), 100, string.Empty);
        error = string.Empty;

        var normalizedText = StripLineComments(queryText).Trim();

        if (TryParseShellQuery(normalizedText, out parsedQuery, out error))
            return true;

        if (string.IsNullOrEmpty(error))
            error = "Query must be one of db.collection.find(...), db.collection.findOne(...), db.collection.aggregate(...), db.collection.countDocuments(...), or db.collection.distinct(...).";

        return false;
    }

    private static bool TryParseShellQuery(
        string queryText,
        out ParsedCustomQuery parsedQuery,
        out string error)
    {
        parsedQuery = new ParsedCustomQuery(CustomQueryKind.Find, string.Empty, new BsonDocument(), new BsonArray(), 100, string.Empty);
        error = string.Empty;

        var directMatch = Regex.Match(
            queryText,
            @"^db\.(?<collection>[^\s.(]+)\.(?<method>find|findOne|aggregate|countDocuments|distinct)\s*\((?<args>[\s\S]*)\)\s*;?\s*$",
            RegexOptions.IgnoreCase);

        var getCollectionMatch = Regex.Match(
            queryText,
            @"^db\.getCollection\s*\(\s*(['""])(?<collection>.*?)\1\s*\)\.(?<method>find|findOne|aggregate|countDocuments|distinct)\s*\((?<args>[\s\S]*)\)\s*;?\s*$",
            RegexOptions.IgnoreCase);

        var match = directMatch.Success ? directMatch : getCollectionMatch;
        if (!match.Success)
            return false;

        var collectionName = match.Groups["collection"].Value.Trim();
        var method = match.Groups["method"].Value.Trim().ToLowerInvariant();
        var argsText = match.Groups["args"].Value.Trim();

        if (string.IsNullOrWhiteSpace(collectionName))
        {
            error = "Collection name cannot be empty.";
            return false;
        }

        BsonArray args;
        try
        {
            args = BsonSerializer.Deserialize<BsonArray>("[" + argsText + "]");
        }
        catch (Exception ex)
        {
            error = "Could not parse query arguments: " + ex.Message;
            return false;
        }

        if (method == "aggregate")
        {
            if (args.Count == 0 || !args[0].IsBsonArray)
            {
                error = "Aggregate queries must pass a pipeline array, for example db.collection.aggregate([{ $sort: { createdAt: -1 } }]).";
                return false;
            }

            parsedQuery = new ParsedCustomQuery(
                CustomQueryKind.Aggregate,
                collectionName,
                new BsonDocument(),
                args[0].AsBsonArray,
                0,
                string.Empty);
            return true;
        }

        if (method == "countdocuments")
        {
            var countFilter = new BsonDocument();
            if (args.Count > 0 && !args[0].IsBsonNull)
            {
                if (!args[0].IsBsonDocument)
                {
                    error = "countDocuments queries must use an object as the first argument when provided.";
                    return false;
                }

                countFilter = args[0].AsBsonDocument;
            }

            parsedQuery = new ParsedCustomQuery(
                CustomQueryKind.CountDocuments,
                collectionName,
                countFilter,
                new BsonArray(),
                0,
                string.Empty);
            return true;
        }

        if (method == "distinct")
        {
            if (args.Count == 0 || !args[0].IsString)
            {
                error = "distinct queries must use the field name string as the first argument.";
                return false;
            }

            var distinctFilter = new BsonDocument();
            if (args.Count > 1 && !args[1].IsBsonNull)
            {
                if (!args[1].IsBsonDocument)
                {
                    error = "distinct queries must use an object as the second argument when a filter is provided.";
                    return false;
                }

                distinctFilter = args[1].AsBsonDocument;
            }

            parsedQuery = new ParsedCustomQuery(
                CustomQueryKind.Distinct,
                collectionName,
                distinctFilter,
                new BsonArray(),
                0,
                args[0].AsString);
            return true;
        }

        var filter = new BsonDocument();
        var limit = method == "findone" ? 1 : 100;

        if (args.Count > 0 && !args[0].IsBsonNull)
        {
            if (!args[0].IsBsonDocument)
            {
                error = "Find queries must use an object as the first argument.";
                return false;
            }

            filter = args[0].AsBsonDocument;
        }

        if (args.Count > 2 && !args[2].IsBsonNull)
        {
            try
            {
                limit = (int)args[2].ToInt64();
            }
            catch
            {
                error = "Find query limit must be a number when provided as the third argument.";
                return false;
            }
        }

        if (limit <= 0) limit = 1;

        parsedQuery = new ParsedCustomQuery(
            method == "findone" ? CustomQueryKind.FindOne : CustomQueryKind.Find,
            collectionName,
            filter,
            new BsonArray(),
            limit,
            string.Empty);
        return true;
    }

    private static string StripLineComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (current == '\\' && (inSingleQuote || inDoubleQuote) && i + 1 < text.Length)
            {
                builder.Append(current);
                builder.Append(text[++i]);
                continue;
            }

            if (!inDoubleQuote && current == '\'')
            {
                inSingleQuote = !inSingleQuote;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuote && current == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && current == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;

                if (i < text.Length)
                    builder.Append(text[i]);

                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
