using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Components.Attributes;

public sealed class DeconstructAttributeComponent : GH_Component
{
    public DeconstructAttributeComponent()
        : base(
            "Deconstruct Attribute",
            "DeAttr",
            "Deconstructs attribute item(s) into key/value lists. Useful for debugging and list operations.",
            "MongoDB",
            "Attributes")
    {
    }

    public override Guid ComponentGuid => new Guid("d4034960-a926-4c20-9c46-9b71e46bdbed");

    protected override Bitmap Icon => PluginIcons.Get("component-view-attribute.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoAttributesParam(), "Attribute", "A", "Attribute(s) (connect multiple wires to create a list)", GH_ParamAccess.list);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("Key", "K", "Attribute key(s)", GH_ParamAccess.list);
        p.AddGenericParameter("Value", "V", "Attribute value(s)", GH_ParamAccess.list);
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
