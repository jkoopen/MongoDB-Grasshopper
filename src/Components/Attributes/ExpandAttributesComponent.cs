using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using MongoDB.Bson;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Components.Attributes;

public sealed class ExpandAttributesComponent : GH_Component
{
    public ExpandAttributesComponent()
        : base(
            "Expand Attributes",
            "ExpAttrs",
            "Splits one multi-attribute item into many single-attribute items.",
            "MongoDB",
            "Attributes")
    {
    }

    public override Guid ComponentGuid => new Guid("b0f54eab-0cb4-4fb8-9b2d-e90d23f72ad2");

    protected override Bitmap Icon => PluginIcons.Get("component-expand-attributes.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoAttributesParam(), "Attributes", "A", "Attributes item to expand", GH_ParamAccess.item);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddParameter(new MongoAttributesParam(), "Attribute", "A", "Expanded single-attribute items", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        MongoAttributesGoo attrsGoo = null!;
        if (!DA.GetData(0, ref attrsGoo) || attrsGoo?.Value?.Document == null)
        {
            DA.SetDataList(0, Array.Empty<MongoAttributesGoo>());
            return;
        }

        var doc = attrsGoo.Value.Document;
        var expanded = new List<MongoAttributesGoo>(doc.ElementCount);

        foreach (var el in doc)
        {
            var singleDoc = new BsonDocument(el.Name, (BsonValue)el.Value.DeepClone());
            expanded.Add(new MongoAttributesGoo(new MongoAttributes(singleDoc)));
        }

        DA.SetDataList(0, expanded);
    }
}
