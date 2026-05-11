using System.Drawing;
using Grasshopper.Kernel;
using System.Windows.Forms;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;

namespace GenericMongoPlugin.Components.Attributes;

public sealed class AttributeOperationsComponent : GH_Component, IGH_VariableParameterComponent
{
    public enum OperationMode
    {
        Split = 0,
        Merge = 1
    }

    private OperationMode _mode = OperationMode.Split;

    public AttributeOperationsComponent()
        : base(
            "Attribute Operation",
            "AttrOp",
            "Split: expands multi-attribute items into many single-attribute items. Merge: weaves multiple input streams together by index (Stream 1..N must have equal length).",
            "MongoDB",
            "Attributes")
    {
        UpdateMessage();
    }

    public override Guid ComponentGuid => new("d4034960-a926-4c20-9c46-9b71e46bdbed");

    protected override Bitmap Icon => PluginIcons.Get("component-expand-attributes.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        // Merge needs separate input parameters (like GH's native Merge) so each stream stays separate.
        // Split uses Stream 1 only.
        p.AddParameter(new MongoAttributesParam(), "Stream 1", "S1", "Attributes stream 1. Used for Split; also used as first stream for Merge.", GH_ParamAccess.list);
        p.AddParameter(new MongoAttributesParam(), "Stream 2", "S2", "Attributes stream 2 (for Merge). Right-click the component to add more streams.", GH_ParamAccess.list);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddParameter(new MongoAttributesParam(), "Attributes", "Attr", "Resulting attributes", GH_ParamAccess.list);
    }

    private void UpdateMessage()
    {
        Message = _mode switch
        {
            OperationMode.Split => "Split",
            OperationMode.Merge => "Merge",
            _ => _mode.ToString()
        };
    }

    private void SetMode(OperationMode mode)
    {
        if (_mode == mode) return;
        RecordUndoEvent("Change Attribute Operation Mode");
        _mode = mode;
        UpdateMessage();
        ExpireSolution(true);
    }

#pragma warning disable CA1416 // Grasshopper's context menu APIs are ToolStrip-based.
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);

        Menu_AppendSeparator(menu);

        var modeMenu = new ToolStripMenuItem("Mode");
        menu.Items.Add(modeMenu);

        void AddModeItem(string label, OperationMode mode)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _mode == mode
            };
            item.Click += (_, _) => SetMode(mode);
            modeMenu.DropDownItems.Add(item);
        }

        AddModeItem("Split", OperationMode.Split);
        AddModeItem("Merge", OperationMode.Merge);
    }
#pragma warning restore CA1416

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetInt32("OperationMode", (int)_mode);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("OperationMode"))
        {
            var v = reader.GetInt32("OperationMode");
            if (v >= 0 && v <= (int)OperationMode.Merge)
                _mode = (OperationMode)v;
        }

        UpdateMessage();
        return base.Read(reader);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        if (_mode == OperationMode.Split)
        {
            // Split = expand/weave: each input item becomes many 1-attribute items.
            var input = new List<MongoAttributesGoo>();
            if (!DA.GetDataList(0, input))
            {
                DA.SetDataList(0, Array.Empty<MongoAttributesGoo>());
                return;
            }

            var expanded = new List<MongoAttributesGoo>();

            foreach (var attrsGoo in input)
            {
                var doc = attrsGoo?.Value?.Document;
                if (doc == null) continue;

                foreach (var el in doc)
                {
                    var singleDoc = new BsonDocument(el.Name, el.Value.DeepClone());
                    expanded.Add(new MongoAttributesGoo(new MongoAttributes(singleDoc)));
                }
            }

            DA.SetDataList(0, expanded);
            return;
        }

        // Merge mode: weave/merge multiple input streams by index.
        var streams = new List<List<MongoAttributesGoo>>();
        for (var p = 0; p < Params.Input.Count; p++)
        {
            if (Params.Input[p].SourceCount == 0) continue;

            var stream = new List<MongoAttributesGoo>();
            if (!DA.GetDataList(p, stream))
                continue;

            streams.Add(stream);
        }

        if (streams.Count < 2)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Merge requires at least 2 connected streams (Stream 1..N).");
            DA.SetDataList(0, Array.Empty<MongoAttributesGoo>());
            return;
        }

        var expected = streams[0].Count;
        for (var s = 1; s < streams.Count; s++)
        {
            if (streams[s].Count != expected)
            {
                var lengths = string.Join(", ", streams.Select(x => x.Count));
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Merge requires all streams to have the same item count. Got: {lengths}.");
                DA.SetDataList(0, Array.Empty<MongoAttributesGoo>());
                return;
            }
        }

        var merged = new List<MongoAttributesGoo>(expected);
        var duplicateKeys = 0;

        for (var i = 0; i < expected; i++)
        {
            var mergedDoc = new BsonDocument();

            for (var s = 0; s < streams.Count; s++)
            {
                var attrsGoo = streams[s][i];
                var doc = attrsGoo?.Value?.Document;
                if (doc == null) continue;

                foreach (var el in doc)
                {
                    if (el.Name == "$id")
                    {
                        if (!mergedDoc.Contains("$id") || mergedDoc["$id"].IsBsonNull)
                        {
                            mergedDoc["$id"] = el.Value.DeepClone();
                        }
                        else
                        {
                            if (!mergedDoc["$id"].Equals(el.Value))
                                duplicateKeys++;
                        }

                        continue;
                    }

                    if (mergedDoc.Contains(el.Name))
                        duplicateKeys++;

                    mergedDoc[el.Name] = el.Value.DeepClone();
                }
            }

            merged.Add(new MongoAttributesGoo(new MongoAttributes(mergedDoc)));
        }

        if (duplicateKeys > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Merge: encountered {duplicateKeys} duplicate key(s). Later inputs overwrite earlier ones.");

        DA.SetDataList(0, merged);
    }

    public bool CanInsertParameter(GH_ParameterSide side, int index)
        => side == GH_ParameterSide.Input && index == Params.Input.Count;

    public bool CanRemoveParameter(GH_ParameterSide side, int index)
        => side == GH_ParameterSide.Input && Params.Input.Count > 2 && index >= 0 && index < Params.Input.Count;

    public IGH_Param CreateParameter(GH_ParameterSide side, int index)
    {
        if (side != GH_ParameterSide.Input)
            throw new ArgumentOutOfRangeException(nameof(side));

        var p = new MongoAttributesParam
        {
            Access = GH_ParamAccess.list
        };

        return p;
    }

    public bool DestroyParameter(GH_ParameterSide side, int index) => true;

    public void VariableParameterMaintenance()
    {
        if (Params.Input.Count < 2)
            return;

        for (var i = 0; i < Params.Input.Count; i++)
        {
            var p = Params.Input[i];
            p.Name = $"Stream {i + 1}";
            p.NickName = $"S{i + 1}";
            p.Description = i == 0
                ? "Attributes stream 1. Used for Split; also used as first stream for Merge."
                : "Attributes stream (for Merge). Right-click the component to add/remove streams.";

            p.Access = GH_ParamAccess.list;
            p.Optional = true;
        }
    }
}
