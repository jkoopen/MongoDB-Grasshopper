using System;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using System.Windows.Forms;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;

namespace GenericMongoPlugin.Components.Filters;

public sealed class FilterEntryComponent : GH_Component
{
    public enum EntryMode
    {
        Value = 0,
        Range = 1
    }

    private EntryMode _mode = EntryMode.Value;

    public FilterEntryComponent()
        : base("Filter Entry", "FEntry", "Creates a single filter entry for building MongoDB filters. Modes: Value (key/value) and Range (key + lower/upper limit). For string values you can use regex: re:<pattern> or /pattern/flags (e.g. /foo.*/i).", "MongoDB", "Filters")
    {
        UpdateMessage();
    }

    public override Guid ComponentGuid => new Guid("3eaf0e9a-4bb8-4a31-9e06-3453912fc2da");

    protected override Bitmap Icon => PluginIcons.Get("component-filter-entry.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddTextParameter("Key", "K", "Attribute key", GH_ParamAccess.item);

        // Default is Value mode. If a different mode is loaded from file, Read() will rebuild params.
        p.AddGenericParameter("Value", "V", "Value to match. If this is a string, regex is supported via re:<pattern> or /pattern/flags (e.g. /chair_.*/i).", GH_ParamAccess.item);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddParameter(new MongoFilterEntryParam(), "Entry", "E", "Filter entry", GH_ParamAccess.item);
    }

    private void UpdateMessage()
    {
        Message = _mode switch
        {
            EntryMode.Value => "Value",
            EntryMode.Range => "Range",
            _ => _mode.ToString()
        };
    }

    private void SetMode(EntryMode mode)
    {
        if (_mode == mode) return;
        RecordUndoEvent("Change Filter Entry Mode");
        _mode = mode;
        UpdateMessage();
        ApplyModeParameters();
        ExpireSolution(true);
    }

    private void ApplyModeParameters()
    {
        // Key param stays the same. Swap/reconfigure remaining inputs.
        if (Params.Input.Count < 2) return;

        // Remove any extra inputs beyond what the current mode needs.
        void TrimInputs(int desiredCount)
        {
            while (Params.Input.Count > desiredCount)
                Params.UnregisterInputParameter(Params.Input[Params.Input.Count - 1], true);
        }

        if (_mode == EntryMode.Value)
        {
            // Key + Value
            TrimInputs(2);

            var p1 = Params.Input[1];
            if (p1 is not Param_GenericObject)
            {
                Params.UnregisterInputParameter(p1, true);
                p1 = new Param_GenericObject();
                Params.RegisterInputParam(p1, 1);
            }

            p1.Name = "Value";
            p1.NickName = "V";
            p1.Description = "Value to match. If this is a string, regex is supported via re:<pattern> or /pattern/flags (e.g. /chair_.*/i).";
            p1.Access = GH_ParamAccess.item;
        }
        else
        {
            // Key + Lower + Upper
            // Keep existing second input if it is generic, so wires can remain attached (Value -> Lower).
            var p1 = Params.Input[1];
            if (p1 is not Param_GenericObject)
            {
                Params.UnregisterInputParameter(p1, true);
                p1 = new Param_GenericObject();
                Params.RegisterInputParam(p1, 1);
            }

            p1.Name = "Lower Limit";
            p1.NickName = "L";
            p1.Description = "Lower bound (inclusive). Creates {$gte: lower, $lte: upper} for this attribute.";
            p1.Access = GH_ParamAccess.item;

            if (Params.Input.Count < 3)
            {
                var upper = new Param_GenericObject
                {
                    Name = "Upper Limit",
                    NickName = "U",
                    Description = "Upper bound (inclusive). Creates {$gte: lower, $lte: upper} for this attribute.",
                    Access = GH_ParamAccess.item
                };
                Params.RegisterInputParam(upper, 2);
            }
            else
            {
                var p2 = Params.Input[2];
                if (p2 is not Param_GenericObject)
                {
                    Params.UnregisterInputParameter(p2, true);
                    p2 = new Param_GenericObject();
                    Params.RegisterInputParam(p2, 2);
                }

                p2.Name = "Upper Limit";
                p2.NickName = "U";
                p2.Description = "Upper bound (inclusive). Creates {$gte: lower, $lte: upper} for this attribute.";
                p2.Access = GH_ParamAccess.item;
            }

            TrimInputs(3);
        }

        Params.OnParametersChanged();
    }

#pragma warning disable CA1416 // Grasshopper's context menu APIs are ToolStrip-based.
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);

        Menu_AppendSeparator(menu);

        var modeMenu = new ToolStripMenuItem("Entry Type");
        menu.Items.Add(modeMenu);

        void AddModeItem(string label, EntryMode mode)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _mode == mode
            };
            item.Click += (_, _) => SetMode(mode);
            modeMenu.DropDownItems.Add(item);
        }

        AddModeItem("Value", EntryMode.Value);
        AddModeItem("Range", EntryMode.Range);
    }
#pragma warning restore CA1416

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetInt32("EntryMode", (int)_mode);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("EntryMode"))
        {
            var v = reader.GetInt32("EntryMode");
            if (v >= 0 && v <= (int)EntryMode.Range)
                _mode = (EntryMode)v;
        }

        UpdateMessage();
        ApplyModeParameters();
        return base.Read(reader);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string key = "";
        IGH_Goo valueGoo = null!;
        IGH_Goo lowerGoo = null!;
        IGH_Goo upperGoo = null!;

        if (!DA.GetData(0, ref key)) return;

        if (_mode == EntryMode.Range)
        {
            if (!DA.GetData(1, ref lowerGoo)) return;
            if (!DA.GetData(2, ref upperGoo)) return;
        }
        else
        {
            if (!DA.GetData(1, ref valueGoo)) return;
        }

        key = (key ?? string.Empty).Trim();
        if (!MongoAttributes.IsValidKey(key))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid key. Mongo keys cannot contain '.' and cannot start with '$'.");
            return;
        }

        if (_mode == EntryMode.Range)
        {
            var okL = BsonValueConverter.TryConvert(lowerGoo, out var lower);
            var okU = BsonValueConverter.TryConvert(upperGoo, out var upper);

            if (!okL || !okU)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Limit type not supported directly. Matched using text representation.");

            var rangeDoc = new BsonDocument
            {
                { "$gte", lower },
                { "$lte", upper }
            };

            DA.SetData(0, new MongoFilterEntryGoo(new MongoFilterEntry(key, rangeDoc)));
            return;
        }

        var ok = BsonValueConverter.TryConvert(valueGoo, out var bson);
        if (!ok)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Value type not supported directly. Matched using text representation.");

        DA.SetData(0, new MongoFilterEntryGoo(new MongoFilterEntry(key, bson)));
    }
}
