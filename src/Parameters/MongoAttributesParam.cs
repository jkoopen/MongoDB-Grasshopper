using System.Drawing;
using Grasshopper.Kernel;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Parameters;

public sealed class MongoAttributesParam : GH_PersistentParam<MongoAttributesGoo>
{
    public MongoAttributesParam()
        : base(new GH_InstanceDescription("Mongo Attributes", "Attrs", "MongoDB attributes", "MongoDB", "Params"))
    {
    }

    public override Guid ComponentGuid => new("f2402011-1e44-44ff-bc09-76a5189a252b");

    protected override Bitmap Icon => PluginIcons.Get("param-attribute.png");

    protected override MongoAttributesGoo InstantiateT() => new(new MongoAttributes());

    protected override GH_GetterResult Prompt_Singular(ref MongoAttributesGoo value) => GH_GetterResult.cancel;

    protected override GH_GetterResult Prompt_Plural(ref List<MongoAttributesGoo> values) => GH_GetterResult.cancel;
}
