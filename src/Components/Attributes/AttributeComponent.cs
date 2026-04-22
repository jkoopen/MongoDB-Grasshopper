using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Components.Attributes;

public sealed class AttributeComponent : GH_Component
{
    public AttributeComponent()
        : base("Attribute", "Attr", "Creates a single attribute (key/value). Connect multiple outputs to build a list.", "MongoDB", "Attributes") { }

    public override Guid ComponentGuid => new("b4af0b5b-0dfe-4b63-926b-f9f7a6a10e08");

    protected override Bitmap Icon => PluginIcons.Get("component-create-attribute.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddTextParameter("Key", "Key", "Attribute key", GH_ParamAccess.item);
        p.AddGenericParameter("Value", "Val", "Attribute value (text/number/bool recommended)", GH_ParamAccess.item);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddParameter(new MongoAttributesParam(), "Attribute", "Attr", "Single attribute as a MongoAttributes item", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string key = "";
        IGH_Goo valueGoo = null!;

        if (!DA.GetData(0, ref key)) return;
        if (!DA.GetData(1, ref valueGoo)) return;

        key = (key ?? string.Empty).Trim();
        if (!MongoAttributes.IsValidKey(key))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid key. Mongo keys cannot contain '.' and cannot start with '$'.");
            return;
        }

        var attrs = new MongoAttributes();

        var ok = BsonValueConverter.TryConvert(valueGoo, out var bson);
        if (!ok)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Value type not supported directly. Stored as text.");

        attrs.Document[key] = bson;

        DA.SetData(0, new MongoAttributesGoo(attrs));
    }
}
