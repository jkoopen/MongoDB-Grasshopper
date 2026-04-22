using System;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Parameters;

public sealed class MongoDbConnectionParam : GH_PersistentParam<MongoDbConnectionGoo>
{
    public MongoDbConnectionParam()
        : base(new GH_InstanceDescription("Mongo Connection", "MongoConn", "MongoDB connection", "MongoDB", "Params"))
    {
    }

    public override Guid ComponentGuid => new Guid("8fdf7b07-2b63-47d1-8586-e5fa40fdfbf6");

    protected override Bitmap Icon => PluginIcons.Get("param-db.png");

    protected override MongoDbConnectionGoo InstantiateT() => new MongoDbConnectionGoo();

    protected override GH_GetterResult Prompt_Singular(ref MongoDbConnectionGoo value) => GH_GetterResult.cancel;

    protected override GH_GetterResult Prompt_Plural(ref System.Collections.Generic.List<MongoDbConnectionGoo> values) => GH_GetterResult.cancel;
}
