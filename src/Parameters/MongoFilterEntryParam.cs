using System.Drawing;
using Grasshopper.Kernel;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Parameters;

public sealed class MongoFilterEntryParam : GH_PersistentParam<MongoFilterEntryGoo>
{
    public MongoFilterEntryParam()
        : base(new GH_InstanceDescription("Mongo Filter Entry", "FEnt", "MongoDB filter entry", "MongoDB", "Params"))
    {
    }

    public override Guid ComponentGuid => new("b56f90c2-67c2-4c15-8df0-33b5b2ef5a16");

    protected override Bitmap Icon => PluginIcons.Get("param-filter-entry.png");

    protected override MongoFilterEntryGoo InstantiateT() => new(new MongoFilterEntry("key", MongoDB.Bson.BsonNull.Value));

    protected override GH_GetterResult Prompt_Singular(ref MongoFilterEntryGoo value) => GH_GetterResult.cancel;

    protected override GH_GetterResult Prompt_Plural(ref List<MongoFilterEntryGoo> values) => GH_GetterResult.cancel;
}
