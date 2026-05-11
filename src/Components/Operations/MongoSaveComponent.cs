using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using System.Windows.Forms;
using GenericMongoPlugin.Parameters;
using GenericMongoPlugin.Types;
using GenericMongoPlugin.Utils;
using MongoDB.Bson;
using MongoDB.Driver;
using Rhino.Geometry;

namespace GenericMongoPlugin.Components;

public sealed class MongoSaveComponent : GH_Component
{
    public enum SaveMode
    {
        Geometry = 0,
        Generic = 1
    }

    private SaveMode _mode = SaveMode.Geometry;

    public MongoSaveComponent()
        : base("Mongo Save", "MongoSave", "Saves either Geometry or Generic Grasshopper data to MongoDB (switch mode via right-click).", "MongoDB", "Operations")
    {
        UpdateMessage();
    }

    public override Guid ComponentGuid => new("3cc3c3a4-40f3-4c0c-9a58-55cb4b459f0f");

    protected override Bitmap Icon => PluginIcons.Get("component-save-mongo.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "MongoDB connection to use for the query", GH_ParamAccess.item);
        p.AddTextParameter("Collection", "Col", "Collection name", GH_ParamAccess.item);

        // Default is Geometry mode.
        p.AddGeometryParameter("Geometry", "Geom", "Geometry to store (list)", GH_ParamAccess.list);
        var plane = new Param_Plane
        {
            Name = "Anchor Plane",
            NickName = "AnchPl",
            Description = "Anchor plane stored alongside geometry. Optional: empty = World XY, 1 item = use for all, or provide N planes.",
            Access = GH_ParamAccess.list,
            Optional = true
        };
        p.AddParameter(plane);

        p.AddParameter(new MongoAttributesParam(), "Attributes", "Atr", "Optional attributes per item (empty or N items)", GH_ParamAccess.list);
        p[p.ParamCount - 1].Optional = true;

        p.AddBooleanParameter("Run", "Run", "Trigger - Avoids querying before the user added all parameters.", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("Log", "Log", "Logs/errors from the driver", GH_ParamAccess.item);
        p.AddTextParameter("Ids", "Ids", "Inserted document ids (same length as input list)", GH_ParamAccess.list);
    }

    private void UpdateMessage()
    {
        Message = _mode switch
        {
            SaveMode.Geometry => "Geometry",
            SaveMode.Generic => "Generic",
            _ => _mode.ToString()
        };
    }

    private void SetMode(SaveMode mode)
    {
        if (_mode == mode) return;
        RecordUndoEvent("Change Mongo Save Mode");
        _mode = mode;
        UpdateMessage();
        ApplyModeParameters();
        ExpireSolution(true);
    }

    private void ApplyModeParameters()
    {
        // Inputs always start with Connection, Collection.
        if (Params.Input.Count < 3) return;

        void TrimInputs(int desiredCount)
        {
            while (Params.Input.Count > desiredCount)
                Params.UnregisterInputParameter(Params.Input[Params.Input.Count - 1], true);
        }

        if (_mode == SaveMode.Geometry)
        {
            // Conn, Col, Geometry, Anchor Plane, Attributes(list), Run
            // Ensure Geometry param at index 2.
            var p2 = Params.Input[2];
            if (p2 is not Param_Geometry)
            {
                Params.UnregisterInputParameter(p2, true);
                p2 = new Param_Geometry();
                Params.RegisterInputParam(p2, 2);
            }
            p2.Name = "Geometry";
            p2.NickName = "G";
            p2.Description = "Geometry to store (list)";
            p2.Access = GH_ParamAccess.list;

            // Ensure Plane param at index 3.
            if (Params.Input.Count < 4)
            {
                var plane = new Param_Plane
                {
                    Name = "Anchor Plane",
                    NickName = "Pl",
                    Description = "Anchor plane stored alongside geometry. Optional: empty = World XY, 1 item = use for all, or provide N planes.",
                    Access = GH_ParamAccess.list,
                    Optional = true
                };
                Params.RegisterInputParam(plane, 3);
            }
            else
            {
                var p3 = Params.Input[3];
                if (p3 is not Param_Plane)
                {
                    Params.UnregisterInputParameter(p3, true);
                    p3 = new Param_Plane();
                    Params.RegisterInputParam(p3, 3);
                }
                p3.Name = "Anchor Plane";
                p3.NickName = "Pl";
                p3.Description = "Anchor plane stored alongside geometry. Optional: empty = World XY, 1 item = use for all, or provide N planes.";
                p3.Access = GH_ParamAccess.list;
                p3.Optional = true;
            }

            // Ensure Attributes(list) at index 4
            if (Params.Input.Count < 5)
            {
                var attrs = new MongoAttributesParam
                {
                    Name = "Attributes",
                    NickName = "A",
                    Description = "Optional attributes (connect multiple wires to create a list)",
                    Access = GH_ParamAccess.list,
                    Optional = true
                };
                Params.RegisterInputParam(attrs, 4);
            }
            else
            {
                var p4 = Params.Input[4];
                if (p4 is not MongoAttributesParam)
                {
                    Params.UnregisterInputParameter(p4, true);
                    p4 = new MongoAttributesParam();
                    Params.RegisterInputParam(p4, 4);
                }
                p4.Name = "Attributes";
                p4.NickName = "A";
                p4.Description = "Optional attributes per item (empty or N items)";
                p4.Access = GH_ParamAccess.list;
                p4.Optional = true;
            }

            // Ensure Run at index 5
            if (Params.Input.Count < 6)
            {
                var run = new Param_Boolean
                {
                    Name = "Run",
                    NickName = "R",
                    Description = "Trigger",
                    Access = GH_ParamAccess.item
                };
                Params.RegisterInputParam(run, 5);
            }
            else
            {
                var p5 = Params.Input[5];
                if (p5 is not Param_Boolean)
                {
                    Params.UnregisterInputParameter(p5, true);
                    p5 = new Param_Boolean();
                    Params.RegisterInputParam(p5, 5);
                }
                p5.Name = "Run";
                p5.NickName = "R";
                p5.Description = "Trigger";
                p5.Access = GH_ParamAccess.item;
            }

            TrimInputs(6);
        }
        else
        {
            // Conn, Col, Data, Attributes(list), Run
            // Reuse index 2 by converting it to Generic.
            var p2 = Params.Input[2];
            if (p2 is not Param_GenericObject)
            {
                Params.UnregisterInputParameter(p2, true);
                p2 = new Param_GenericObject();
                Params.RegisterInputParam(p2, 2);
            }
            p2.Name = "Data";
            p2.NickName = "D";
            p2.Description = "Data to store (list, any Grasshopper type)";
            p2.Access = GH_ParamAccess.list;

            // Ensure Attributes at index 3.
            if (Params.Input.Count < 4)
            {
                var attrs = new MongoAttributesParam
                {
                    Name = "Attributes",
                    NickName = "A",
                    Description = "Optional attributes (connect multiple wires to create a list)",
                    Access = GH_ParamAccess.list,
                    Optional = true
                };
                Params.RegisterInputParam(attrs, 3);
            }
            else
            {
                var p3 = Params.Input[3];
                if (p3 is not MongoAttributesParam)
                {
                    Params.UnregisterInputParameter(p3, true);
                    p3 = new MongoAttributesParam();
                    Params.RegisterInputParam(p3, 3);
                }
                p3.Name = "Attributes";
                p3.NickName = "A";
                p3.Description = "Optional attributes per item (empty or N items)";
                p3.Access = GH_ParamAccess.list;
                p3.Optional = true;
            }

            // Ensure Run at index 4.
            if (Params.Input.Count < 5)
            {
                var run = new Param_Boolean
                {
                    Name = "Run",
                    NickName = "R",
                    Description = "Trigger",
                    Access = GH_ParamAccess.item
                };
                Params.RegisterInputParam(run, 4);
            }
            else
            {
                var p4 = Params.Input[4];
                if (p4 is not Param_Boolean)
                {
                    Params.UnregisterInputParameter(p4, true);
                    p4 = new Param_Boolean();
                    Params.RegisterInputParam(p4, 4);
                }
                p4.Name = "Run";
                p4.NickName = "R";
                p4.Description = "Trigger";
                p4.Access = GH_ParamAccess.item;
            }

            TrimInputs(5);
        }

        Params.OnParametersChanged();
    }

#pragma warning disable CA1416 // Grasshopper's context menu APIs are ToolStrip-based.
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);

        Menu_AppendSeparator(menu);

        var modeMenu = new ToolStripMenuItem("Mode");
        menu.Items.Add(modeMenu);

        void AddModeItem(string label, SaveMode mode)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _mode == mode
            };
            item.Click += (_, _) => SetMode(mode);
            modeMenu.DropDownItems.Add(item);
        }

        AddModeItem("Geometry", SaveMode.Geometry);
        AddModeItem("Generic", SaveMode.Generic);
    }
#pragma warning restore CA1416

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetInt32("SaveMode", (int)_mode);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("SaveMode"))
        {
            var v = reader.GetInt32("SaveMode");
            if (v >= 0 && v <= (int)SaveMode.Generic)
                _mode = (SaveMode)v;
        }

        UpdateMessage();
        ApplyModeParameters();
        return base.Read(reader);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        MongoDbConnectionGoo connGoo = null!;
        string collectionName = "";
        bool run = false;

        if (!DA.GetData(0, ref connGoo) || connGoo?.Value == null) return;
        if (!DA.GetData(1, ref collectionName)) return;

        collectionName = (collectionName ?? string.Empty).Trim();

        if (!connGoo.Value.IsValid)
        {
            DA.SetData(0, "Error: Invalid connection.");
            DA.SetDataList(1, Array.Empty<string>());
            return;
        }

        if (string.IsNullOrWhiteSpace(collectionName))
        {
            DA.SetData(0, "Error: Collection name is required.");
            DA.SetDataList(1, Array.Empty<string>());
            return;
        }

        if (_mode == SaveMode.Geometry)
        {
            var geometries = new List<IGH_GeometricGoo>();
            var anchorPlanes = new List<Plane>();
            var attrsList = new List<MongoAttributesGoo>();

            if (!DA.GetDataList(2, geometries) || geometries.Count == 0) return;
            DA.GetDataList(3, anchorPlanes);
            DA.GetDataList(4, attrsList);
            DA.GetData(5, ref run);

            if (!run) return;

            // Validate planes: empty (default WorldXY), 1 (broadcast), or N.
            if (anchorPlanes.Count != 0 && anchorPlanes.Count != 1 && anchorPlanes.Count != geometries.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Anchor Plane must be empty, 1 item, or match Geometry count (expected {geometries.Count}). Got {anchorPlanes.Count}.");
                DA.SetData(0, null);
                DA.SetDataList(1, Array.Empty<string>());
                return;
            }

            // Validate attributes: empty or N.
            if (attrsList.Count != 0 && attrsList.Count != geometries.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Attributes must be empty or match Geometry count (expected {geometries.Count}). Got {attrsList.Count}.");
                DA.SetData(0, null);
                DA.SetDataList(1, Array.Empty<string>());
                return;
            }

            for (var i = 0; i < geometries.Count; i++)
            {
                if (geometries[i] == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Geometry list contains a null item at index {i}.");
                    DA.SetData(0, null);
                    DA.SetDataList(1, Array.Empty<string>());
                    return;
                }
            }

            try
            {
                var db = connGoo.Value.CreateDatabase();
                var col = db.GetCollection<BsonDocument>(collectionName);

                var docs = new List<BsonDocument>(geometries.Count);
                for (var i = 0; i < geometries.Count; i++)
                {
                    var geometry = geometries[i];
                    if (geometry == null) throw new InvalidOperationException($"Geometry item {i} is null.");

                    var anchorPlane = anchorPlanes.Count switch
                    {
                        0 => Plane.WorldXY,
                        1 => anchorPlanes[0],
                        _ => anchorPlanes[i]
                    };

                    var bytes = GhArchiveGeometrySerializer.Serialize(geometry);

                    var bb = geometry.Boundingbox;
                    var bboxDoc = new BsonDocument
                    {
                        { "min", new BsonArray { bb.Min.X, bb.Min.Y, bb.Min.Z } },
                        { "max", new BsonArray { bb.Max.X, bb.Max.Y, bb.Max.Z } }
                    };

                    var geomType = GhArchiveGeometrySerializer.GetGeometryTypeName(geometry);
                    var anchorDoc = PlaneBsonConverter.ToBson(anchorPlane);

                    var attrsDoc = attrsList.Count == 0
                        ? new BsonDocument()
                        : MongoOperations.MergeAttributes(new[] { attrsList[i] });

                    var doc = new BsonDocument
                    {
                        { "type", "geometry" },
                        { "geom", new BsonBinaryData(bytes) },
                        { "geomType", geomType },
                        { "anchor", anchorDoc },
                        { "bbox", bboxDoc },
                        { "attrs", attrsDoc },
                        { "createdAt", DateTime.UtcNow }
                    };

                    docs.Add(doc);
                }

                col.InsertMany(docs);

                var ids = new List<string>(docs.Count);
                foreach (var d in docs)
                {
                    if (d.TryGetValue("_id", out var idVal) && !idVal.IsBsonNull)
                    {
                        ids.Add((idVal.IsObjectId ? idVal.AsObjectId.ToString() : idVal.ToString()) ?? string.Empty);
                    }
                    else
                    {
                        ids.Add(string.Empty);
                    }
                }

                DA.SetData(0, $"Success: Inserted {ids.Count} geometry item(s)." );
                DA.SetDataList(1, ids);
            }
            catch (Exception ex)
            {
                DA.SetData(0, "Error: " + ex.Message);
                DA.SetDataList(1, Array.Empty<string>());
            }

            return;
        }

        // Generic
        var dataList = new List<IGH_Goo>();
        var attrs = new List<MongoAttributesGoo>();

        if (!DA.GetDataList(2, dataList) || dataList.Count == 0) return;
        DA.GetDataList(3, attrs);
        DA.GetData(4, ref run);

        if (!run) return;

        if (attrs.Count != 0 && attrs.Count != dataList.Count)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Attributes must be empty or match Data count (expected {dataList.Count}). Got {attrs.Count}.");
            DA.SetData(0, null);
            DA.SetDataList(1, Array.Empty<string>());
            return;
        }

        for (var i = 0; i < dataList.Count; i++)
        {
            if (dataList[i] == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Data list contains a null item at index {i}.");
                DA.SetData(0, null);
                DA.SetDataList(1, Array.Empty<string>());
                return;
            }
        }

        try
        {
            var db = connGoo.Value.CreateDatabase();
            var col = db.GetCollection<BsonDocument>(collectionName);

            var docs = new List<BsonDocument>(dataList.Count);
            for (var i = 0; i < dataList.Count; i++)
            {
                var dataGoo = dataList[i];
                if (dataGoo == null) throw new InvalidOperationException($"Data item {i} is null.");

                var bytes = GhArchiveGooSerializer.Serialize(dataGoo);
                var dataType = GhArchiveGooSerializer.GetGooTypeName(dataGoo);

                var attrsDoc = attrs.Count == 0
                    ? new BsonDocument()
                    : MongoOperations.MergeAttributes(new[] { attrs[i] });

                var doc = new BsonDocument
                {
                    { "type", "data" },
                    { "data", new BsonBinaryData(bytes) },
                    { "dataType", dataType },
                    { "attrs", attrsDoc },
                    { "createdAt", DateTime.UtcNow }
                };

                docs.Add(doc);
            }

            col.InsertMany(docs);

            var ids = new List<string>(docs.Count);
            foreach (var d in docs)
            {
                if (d.TryGetValue("_id", out var idVal) && !idVal.IsBsonNull)
                {
                    ids.Add((idVal.IsObjectId ? idVal.AsObjectId.ToString() : idVal.ToString()) ?? string.Empty);
                }
                else
                {
                    ids.Add(string.Empty);
                }
            }

            DA.SetData(0, $"Success: Inserted {ids.Count} item(s)." );
            DA.SetDataList(1, ids);
        }
        catch (Exception ex)
        {
            DA.SetData(0, "Error: " + ex.Message);
            DA.SetDataList(1, Array.Empty<string>());
        }
    }
}
