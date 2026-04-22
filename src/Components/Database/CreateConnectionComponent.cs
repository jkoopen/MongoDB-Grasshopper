using System.Drawing;
using Grasshopper.Kernel;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GenericMongoPlugin.Components;

public class ConstructMongoConnectionComponent : GH_Component
{
    public ConstructMongoConnectionComponent()
        : base("Mongo Connection", "MongoConn", "Creates a reusable MongoDB connection object.", "MongoDB", "Database") { }

    public override Guid ComponentGuid => new("4f5f1ff8-6c24-44f1-b72a-1d4c17ed6f4b");

    protected override Bitmap Icon => PluginIcons.Get("component-create-connection.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddTextParameter("Host", "Host", "MongoDB host (e.g. localhost)", GH_ParamAccess.item, "localhost");
        p.AddIntegerParameter("Port", "Port", "MongoDB port", GH_ParamAccess.item, 27017);
        p.AddTextParameter("User", "User", "Username (optional) to use for authentication", GH_ParamAccess.item);
        p[p.ParamCount - 1].Optional = true;
        p.AddTextParameter("Password", "Pw", "Password (optional) to use for authentication", GH_ParamAccess.item);
        p[p.ParamCount - 1].Optional = true;
        p.AddTextParameter("Database", "DB", "Database name, must exist and will not be created if it doesnt exist", GH_ParamAccess.item);
        p.AddTextParameter("Options", "Opt", "Optional extra connection string options (e.g. authSource=admin&tls=true)", GH_ParamAccess.item, "");
        p[p.ParamCount - 1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "Reusable MongoDB connection object", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string host = "localhost";
        int port = 27017;
        string user = "";
        string password = "";
        string databaseName = "";
        string options = "";

        if (!DA.GetData(0, ref host)) return;
        DA.GetData(1, ref port);
        DA.GetData(2, ref user);
        DA.GetData(3, ref password);
        if (!DA.GetData(4, ref databaseName)) return;
        DA.GetData(5, ref options);

        host = host?.Trim() ?? string.Empty;
        databaseName = databaseName?.Trim() ?? string.Empty;
        options = options?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(host))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Host is required.");
            return;
        }

        if (port is < 1 or > 65535)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Port must be between 1 and 65535.");
            return;
        }

        if (!string.IsNullOrEmpty(password) && string.IsNullOrEmpty(user))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "If Password is provided, User must also be provided.");
            return;
        }

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Database is required.");
            return;
        }

        var connStr = BuildMongoConnectionString(host, port, user, password, databaseName, options);

        var conn = new MongoDbConnection
        {
            ConnectionString = connStr,
            DatabaseName = databaseName
        };

        // Ping/auth test in the solver: only output a connection if this succeeds.
        try
        {
            var settings = MongoClientSettings.FromConnectionString(connStr);

            // Keep Grasshopper responsive: avoid long blocks when host is unreachable.
            // Users can still override via their connection string options if needed.
            if (settings.ServerSelectionTimeout > TimeSpan.FromSeconds(5))
                settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            if (settings.ConnectTimeout > TimeSpan.FromSeconds(5))
                settings.ConnectTimeout = TimeSpan.FromSeconds(5);

            var client = new MongoClient(settings);
            var db = client.GetDatabase(databaseName);
            db.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            DA.SetData(0, new MongoDbConnectionGoo(conn));
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "MongoDB ping/auth failed: " + ex.Message);
        }
    }

    private static string BuildMongoConnectionString(
        string host,
        int port,
        string user,
        string password,
        string databaseName,
        string options)
    {
        var userInfo = "";
        if (!string.IsNullOrEmpty(user))
        {
            var u = Uri.EscapeDataString(user);
            if (!string.IsNullOrEmpty(password))
            {
                var p = Uri.EscapeDataString(password);
                userInfo = $"{u}:{p}@";
            }
            else
            {
                userInfo = $"{u}@";
            }
        }

        // Keep this simple: mongodb://user:pass@host:port/database?options
        var dbPath = Uri.EscapeDataString(databaseName);
        var connStr = $"mongodb://{userInfo}{host}:{port}/{dbPath}";

        options = (options ?? string.Empty).Trim();
        if (options.Length > 0)
        {
            // Allow users to paste either "?x=y" or "x=y" or "&x=y".
            while (options.StartsWith("?") || options.StartsWith("&"))
                options = options[1..];

            if (options.Length > 0)
                connStr += "?" + options;
        }

        return connStr;
    }
}
