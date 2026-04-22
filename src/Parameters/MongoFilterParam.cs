using System.Drawing;
using Grasshopper.Kernel;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Parameters;

public sealed class MongoFilterParam : GH_PersistentParam<MongoFilterGoo>
{
    public MongoFilterParam()
        : base(new GH_InstanceDescription("Mongo Filter", "Filter", "MongoDB filter", "MongoDB", "Params"))
    {
    }

    public override Guid ComponentGuid => new Guid("8a5ed799-90b9-4f21-bbd6-d3d50df7a331");
    protected override Bitmap Icon => PluginIcons.Get("param-filter.png");
    protected override MongoFilterGoo InstantiateT() => new MongoFilterGoo(MongoFilter.Empty());
    protected override GH_GetterResult Prompt_Singular(ref MongoFilterGoo value) => GH_GetterResult.cancel;
    protected override GH_GetterResult Prompt_Plural(ref List<MongoFilterGoo> values) => GH_GetterResult.cancel;
}
