using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Components.Attributes;

public sealed class DeconstructAttributesComponent : GH_Component
{
    public DeconstructAttributesComponent()
        : base(
            "Deconstruct Attribute",
            "DeAttr",
            "Deconstructs attribute item(s) into key/value lists. Useful for debugging and list operations.",
            "MongoDB",
            "Attributes")
    {
    }

    public override Guid ComponentGuid => new("b0f54eab-0cb4-4fb8-9b2d-e90d23f72ad2");

    protected override Bitmap Icon => PluginIcons.Get("component-view-attribute.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoAttributesParam(), "Attribute", "Attr", "Attribute(s) (connect multiple wires to create a list)", GH_ParamAccess.list);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("Key", "Key", "Attribute key(s)", GH_ParamAccess.list);
        p.AddGenericParameter("Value", "Val", "Attribute value(s)", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var attributesList = new List<MongoAttributesGoo>();
        if (!DA.GetDataList(0, attributesList)) return;

        var keys = new List<string>();
        var values = new List<IGH_Goo>();

        foreach (var attrsGoo in attributesList)
        {
            var doc = attrsGoo?.Value?.Document;
            if (doc == null) continue;

            foreach (var el in doc)
            {
                keys.Add(el.Name);
                values.Add(BsonValueToGooConverter.Convert(el.Value));
            }
        }

        DA.SetDataList(0, keys);
        DA.SetDataList(1, values);
    }
}
