using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using System.Windows.Forms;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using Rhino.Geometry;

namespace GenericMongoPlugin.Components;

public sealed class MongoQueryComponent : GH_Component
{
    public enum QueryMode
    {
        Geometry = 0,
        Generic = 1
    }

    private QueryMode _mode = QueryMode.Geometry;

    public MongoQueryComponent()
        : base("Mongo Query", "MongoQuery", "Queries either Geometry or Generic Grasshopper data from MongoDB (switch mode via right-click) and returns decoded data, alongside with its attributes.", "MongoDB", "Operations")
    {
        UpdateMessage();
    }

    public override Guid ComponentGuid => new Guid("ad7d2b82-1b80-4982-98f0-6ed33e4f3cf2");

    protected override Bitmap Icon => PluginIcons.Get("component-query-mongo.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "MongoDB connection to use for the query", GH_ParamAccess.item);
        p.AddTextParameter("Collection", "Col", "Collection name", GH_ParamAccess.item);
        p.AddParameter(new MongoFilterParam(), "Filters", "Filt", "Filters (connect multiple wires to create a list)", GH_ParamAccess.list);
        p[p.ParamCount - 1].Optional = true;
        p.AddIntegerParameter("Limit", "Limit", "Max documents", GH_ParamAccess.item, 100);
        p.AddBooleanParameter("Run", "Run", "Trigger - Avoids querying before the user added all parameters.", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        // Default is Geometry mode.
        p.AddTextParameter("Log", "L", "Logs/errors", GH_ParamAccess.item);
        p.AddGeometryParameter("Geometry", "G", "Geometries", GH_ParamAccess.list);
        p.AddPlaneParameter("Anchor Plane", "Pl", "Anchor plane per geometry (World XY if missing)", GH_ParamAccess.list);
        p.AddParameter(new MongoAttributesParam(), "Attributes", "A", "Attributes per geometry", GH_ParamAccess.list);
    }

    private void UpdateMessage()
    {
        Message = _mode switch
        {
            QueryMode.Geometry => "Geometry",
            QueryMode.Generic => "Generic",
            _ => _mode.ToString()
        };
    }

    private void SetMode(QueryMode mode)
    {
        if (_mode == mode) return;
        RecordUndoEvent("Change Mongo Query Mode");
        _mode = mode;
        UpdateMessage();
        ApplyModeParameters();
        ExpireSolution(true);
    }

    private void ApplyModeParameters()
    {
        // Output 0 is always Log. Remaining outputs are mode-specific.
        if (Params.Output.Count == 0) return;

        void TrimOutputs(int desiredCount)
        {
            while (Params.Output.Count > desiredCount)
                Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);
        }

        if (_mode == QueryMode.Geometry)
        {
            // Log + Geometry + Plane + Attributes
            if (Params.Output.Count < 2)
                Params.RegisterOutputParam(new Param_Geometry(), 1);
            if (Params.Output.Count < 3)
                Params.RegisterOutputParam(new Param_Plane(), 2);
            if (Params.Output.Count < 4)
                Params.RegisterOutputParam(new MongoAttributesParam(), 3);

            // Ensure proper types and metadata.
            var o1 = Params.Output[1];
            if (o1 is not Param_Geometry)
            {
                Params.UnregisterOutputParameter(o1, true);
                o1 = new Param_Geometry();
                Params.RegisterOutputParam(o1, 1);
            }
            o1.Name = "Geometry";
            o1.NickName = "G";
            o1.Description = "Geometries";
            o1.Access = GH_ParamAccess.list;

            var o2 = Params.Output[2];
            if (o2 is not Param_Plane)
            {
                Params.UnregisterOutputParameter(o2, true);
                o2 = new Param_Plane();
                Params.RegisterOutputParam(o2, 2);
            }
            o2.Name = "Anchor Plane";
            o2.NickName = "Pl";
            o2.Description = "Anchor plane per geometry (World XY if missing)";
            o2.Access = GH_ParamAccess.list;

            var o3 = Params.Output[3];
            if (o3 is not MongoAttributesParam)
            {
                Params.UnregisterOutputParameter(o3, true);
                o3 = new MongoAttributesParam();
                Params.RegisterOutputParam(o3, 3);
            }
            o3.Name = "Attributes";
            o3.NickName = "A";
            o3.Description = "Attributes per geometry";
            o3.Access = GH_ParamAccess.list;

            TrimOutputs(4);
        }
        else
        {
            // Log + Data + Attributes
            // Reuse output 1 as generic.
            if (Params.Output.Count < 2)
                Params.RegisterOutputParam(new Param_GenericObject(), 1);
            if (Params.Output.Count < 3)
                Params.RegisterOutputParam(new MongoAttributesParam(), 2);

            var o1 = Params.Output[1];
            if (o1 is not Param_GenericObject)
            {
                Params.UnregisterOutputParameter(o1, true);
                o1 = new Param_GenericObject();
                Params.RegisterOutputParam(o1, 1);
            }
            o1.Name = "Data";
            o1.NickName = "D";
            o1.Description = "Data";
            o1.Access = GH_ParamAccess.list;

            var o2 = Params.Output[2];
            if (o2 is not MongoAttributesParam)
            {
                Params.UnregisterOutputParameter(o2, true);
                o2 = new MongoAttributesParam();
                Params.RegisterOutputParam(o2, 2);
            }
            o2.Name = "Attributes";
            o2.NickName = "A";
            o2.Description = "Attributes per item";
            o2.Access = GH_ParamAccess.list;

            TrimOutputs(3);
        }

        // Log output should always be present.
        var log = Params.Output[0];
        log.Name = "Log";
        log.NickName = "L";
        log.Description = "Logs/errors";
        log.Access = GH_ParamAccess.item;

        Params.OnParametersChanged();
    }

#pragma warning disable CA1416 // Grasshopper's context menu APIs are ToolStrip-based.
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);

        Menu_AppendSeparator(menu);

        var modeMenu = new ToolStripMenuItem("Mode");
        menu.Items.Add(modeMenu);

        void AddModeItem(string label, QueryMode mode)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _mode == mode
            };
            item.Click += (_, _) => SetMode(mode);
            modeMenu.DropDownItems.Add(item);
        }

        AddModeItem("Geometry", QueryMode.Geometry);
        AddModeItem("Generic", QueryMode.Generic);
    }
#pragma warning restore CA1416

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetInt32("QueryMode", (int)_mode);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("QueryMode"))
        {
            var v = reader.GetInt32("QueryMode");
            if (v >= 0 && v <= (int)QueryMode.Generic)
                _mode = (QueryMode)v;
        }

        UpdateMessage();
        ApplyModeParameters();
        return base.Read(reader);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        MongoDbConnectionGoo connGoo = null!;
        string collectionName = "";
        var filters = new List<MongoFilterGoo>();
        int limit = 100;
        bool run = false;

        if (!DA.GetData(0, ref connGoo) || connGoo?.Value == null) return;
        if (!DA.GetData(1, ref collectionName)) return;
        DA.GetDataList(2, filters);
        DA.GetData(3, ref limit);
        DA.GetData(4, ref run);

        if (!run) return;

        if (!connGoo.Value.IsValid)
        {
            DA.SetData(0, "Error: Invalid connection.");
            return;
        }

        collectionName = (collectionName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            DA.SetData(0, "Error: Collection name is required.");
            return;
        }

        if (limit <= 0) limit = 1;

        try
        {
            if (_mode == QueryMode.Geometry)
            {
                var (log, geometries, planes, attributes) = MongoOperations.QueryGeometry(connGoo.Value, collectionName, filters, limit);
                DA.SetData(0, log);
                DA.SetDataList(1, geometries);
                DA.SetDataList(2, planes);
                DA.SetDataList(3, attributes);
                return;
            }

            var (log2, data, attributes2) = MongoOperations.QueryData(connGoo.Value, collectionName, filters, limit);
            DA.SetData(0, log2);
            DA.SetDataList(1, data);
            DA.SetDataList(2, attributes2);
        }
        catch (Exception ex)
        {
            DA.SetData(0, "Error: " + ex.Message);
        }
    }
}
