using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Components.Attributes;

public sealed class FindAttributeComponent : GH_Component
{
    public FindAttributeComponent()
        : base(
            "Find Attribute",
            "FindAttr",
            "Finds a value for a key in each Attributes item. Outputs per-item value (or null) and per-item found flag.",
            "MongoDB",
            "Attributes")
    {
    }

    public override Guid ComponentGuid => new Guid("7ad99b64-0b4e-4c56-96dd-650898d3c19a");

    protected override Bitmap Icon => PluginIcons.Get("component-search-attribute.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoAttributesParam(), "Attributes", "A", "Attributes item(s) (connect multiple wires to create a list)", GH_ParamAccess.list);

        p.AddTextParameter("Key", "K", "Key to look up", GH_ParamAccess.item);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("Value", "V", "Value per attributes item (null when not found)", GH_ParamAccess.list);
        p.AddBooleanParameter("Found", "F", "True when key exists in the corresponding attributes item", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var attributesList = new List<MongoAttributesGoo>();
        DA.GetDataList(0, attributesList);

        string key = string.Empty;
        if (!DA.GetData(1, ref key)) return;

        key = (key ?? string.Empty).Trim();

        var values = new List<IGH_Goo?>(attributesList.Count);
        var found = new List<bool>(attributesList.Count);

        if (!MongoAttributes.IsValidKey(key))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid key. Mongo keys cannot contain '.' and cannot start with '$'.");

            for (var i = 0; i < attributesList.Count; i++)
            {
                values.Add(null);
                found.Add(false);
            }

            DA.SetDataList(0, values);
            DA.SetDataList(1, found);
            return;
        }

        foreach (var attrsGoo in attributesList)
        {
            var doc = attrsGoo?.Value?.Document;
            if (doc == null)
            {
                values.Add(null);
                found.Add(false);
                continue;
            }

            if (!doc.TryGetValue(key, out var bsonValue))
            {
                values.Add(null);
                found.Add(false);
                continue;
            }

            found.Add(true);
            values.Add(bsonValue == null || bsonValue.IsBsonNull ? null : BsonValueToGooConverter.Convert(bsonValue));
        }

        DA.SetDataList(0, values);
        DA.SetDataList(1, found);
    }
}
