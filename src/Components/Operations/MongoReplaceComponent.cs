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

public sealed class MongoReplaceComponent : GH_Component
{
    public enum ReplaceMode
    {
        Geometry = 0,
        Generic = 1
    }

    private ReplaceMode _mode = ReplaceMode.Geometry;

    public MongoReplaceComponent()
        : base("Mongo Replace", "MongoReplace", "Replaces stored content for matching documents (switch mode via right-click). This updates matching documents using $set. Safety: replace is blocked if no filters are provided.", "MongoDB", "Operations")
    {
        UpdateMessage();
    }

    public override Guid ComponentGuid => new("c9dd9b8e-1a88-4512-a875-59899ba443a8");

    protected override Bitmap Icon => PluginIcons.Get("component-replace-mongo.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddParameter(new MongoDbConnectionParam(), "Connection", "Conn", "MongoDB connection to use for the query", GH_ParamAccess.item);
        p.AddTextParameter("Collection", "Col", "Collection name, if nonexistent the collection is automatically created.", GH_ParamAccess.item);
        p.AddParameter(new MongoFilterParam(), "Filters", "Filt", "Filters (connect multiple wires to create a list)", GH_ParamAccess.list);
        p[p.ParamCount - 1].Optional = true;

        // Default is Geometry mode.
        p.AddGeometryParameter("Geometry", "Geom", "New geometry to store into matching documents", GH_ParamAccess.item);
        p.AddPlaneParameter("Anchor Plane", "Anch", "Anchor plane stored alongside geometry (default World XY). May be used to store the position of geometry so it can be reoriented later on.", GH_ParamAccess.item, Plane.WorldXY);

        p.AddParameter(new MongoAttributesParam(), "Attributes", "Attr", "Optional attributes (connect multiple wires to create a list)", GH_ParamAccess.list);
        p[p.ParamCount - 1].Optional = true;

        p.AddBooleanParameter("Run", "Run", "Trigger - Avoids querying before the user added all parameters.", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("Log", "Log", "Logs/errors from the driver", GH_ParamAccess.item);
        p.AddIntegerParameter("Matched", "Mat", "Number of matched documents", GH_ParamAccess.item);
        p.AddIntegerParameter("Modified", "Mod", "Number of modified documents", GH_ParamAccess.item);
    }

    private void UpdateMessage()
    {
        Message = _mode switch
        {
            ReplaceMode.Geometry => "Geometry",
            ReplaceMode.Generic => "Generic",
            _ => _mode.ToString()
        };
    }

    private void SetMode(ReplaceMode mode)
    {
        if (_mode == mode) return;
        RecordUndoEvent("Change Mongo Replace Mode");
        _mode = mode;
        UpdateMessage();
        ApplyModeParameters();
        ExpireSolution(true);
    }

    private void ApplyModeParameters()
    {
        // Inputs always start: Connection, Collection, Filters.
        if (Params.Input.Count < 4) return;

        void TrimInputs(int desiredCount)
        {
            while (Params.Input.Count > desiredCount)
                Params.UnregisterInputParameter(Params.Input[Params.Input.Count - 1], true);
        }

        if (_mode == ReplaceMode.Geometry)
        {
            // Conn, Col, Filters, Geometry, Plane, Attributes(list), Run
            var p3 = Params.Input[3];
            if (p3 is not Param_Geometry)
            {
                Params.UnregisterInputParameter(p3, true);
                p3 = new Param_Geometry();
                Params.RegisterInputParam(p3, 3);
            }
            p3.Name = "Geometry";
            p3.NickName = "Geom";
            p3.Description = "New geometry to store into matching documents";
            p3.Access = GH_ParamAccess.item;

            if (Params.Input.Count < 5)
            {
                var plane = new Param_Plane
                {
                    Name = "Anchor Plane",
                    NickName = "Anch",
                    Description = "Anchor plane stored alongside geometry (default World XY)",
                    Access = GH_ParamAccess.item
                };
                Params.RegisterInputParam(plane, 4);
            }
            else
            {
                var p4 = Params.Input[4];
                if (p4 is not Param_Plane)
                {
                    Params.UnregisterInputParameter(p4, true);
                    p4 = new Param_Plane();
                    Params.RegisterInputParam(p4, 4);
                }
                p4.Name = "Anchor Plane";
                p4.NickName = "Pl";
                p4.Description = "Anchor plane stored alongside geometry (default World XY)";
                p4.Access = GH_ParamAccess.item;
            }

            if (Params.Input.Count < 6)
            {
                var attrs = new MongoAttributesParam
                {
                    Name = "Attributes",
                    NickName = "A",
                    Description = "Optional attributes (connect multiple wires to create a list)",
                    Access = GH_ParamAccess.list,
                    Optional = true
                };
                Params.RegisterInputParam(attrs, 5);
            }
            else
            {
                var p5 = Params.Input[5];
                if (p5 is not MongoAttributesParam)
                {
                    Params.UnregisterInputParameter(p5, true);
                    p5 = new MongoAttributesParam();
                    Params.RegisterInputParam(p5, 5);
                }
                p5.Name = "Attributes";
                p5.NickName = "A";
                p5.Description = "Optional attributes (connect multiple wires to create a list)";
                p5.Access = GH_ParamAccess.list;
                p5.Optional = true;
            }

            if (Params.Input.Count < 7)
            {
                var run = new Param_Boolean
                {
                    Name = "Run",
                    NickName = "R",
                    Description = "Trigger",
                    Access = GH_ParamAccess.item
                };
                Params.RegisterInputParam(run, 6);
            }
            else
            {
                var p6 = Params.Input[6];
                if (p6 is not Param_Boolean)
                {
                    Params.UnregisterInputParameter(p6, true);
                    p6 = new Param_Boolean();
                    Params.RegisterInputParam(p6, 6);
                }
                p6.Name = "Run";
                p6.NickName = "R";
                p6.Description = "Trigger";
                p6.Access = GH_ParamAccess.item;
            }

            TrimInputs(7);
        }
        else
        {
            // Conn, Col, Filters, Data, Attributes(list), Run
            var p3 = Params.Input[3];
            if (p3 is not Param_GenericObject)
            {
                Params.UnregisterInputParameter(p3, true);
                p3 = new Param_GenericObject();
                Params.RegisterInputParam(p3, 3);
            }
            p3.Name = "Data";
            p3.NickName = "D";
            p3.Description = "New data to store into matching documents (any Grasshopper type)";
            p3.Access = GH_ParamAccess.item;

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
                p4.Description = "Optional attributes (connect multiple wires to create a list)";
                p4.Access = GH_ParamAccess.list;
                p4.Optional = true;
            }

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

        Params.OnParametersChanged();
    }

#pragma warning disable CA1416 // Grasshopper's context menu APIs are ToolStrip-based.
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);

        Menu_AppendSeparator(menu);

        var modeMenu = new ToolStripMenuItem("Mode");
        menu.Items.Add(modeMenu);

        void AddModeItem(string label, ReplaceMode mode)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _mode == mode
            };
            item.Click += (_, _) => SetMode(mode);
            modeMenu.DropDownItems.Add(item);
        }

        AddModeItem("Geometry", ReplaceMode.Geometry);
        AddModeItem("Generic", ReplaceMode.Generic);
    }
#pragma warning restore CA1416

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetInt32("ReplaceMode", (int)_mode);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("ReplaceMode"))
        {
            var v = reader.GetInt32("ReplaceMode");
            if (v >= 0 && v <= (int)ReplaceMode.Generic)
                _mode = (ReplaceMode)v;
        }

        UpdateMessage();
        ApplyModeParameters();
        return base.Read(reader);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        MongoDbConnectionGoo connGoo = null!;
        string collectionName = string.Empty;
        var filters = new List<MongoFilterGoo>();
        bool run = false;

        if (!DA.GetData(0, ref connGoo) || connGoo?.Value == null) return;
        if (!DA.GetData(1, ref collectionName)) return;
        DA.GetDataList(2, filters);

        if (!connGoo.Value.IsValid)
        {
            DA.SetData(0, "Error: Invalid connection.");
            DA.SetData(1, 0);
            DA.SetData(2, 0);
            return;
        }

        collectionName = (collectionName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            DA.SetData(0, "Error: Collection name is required.");
            DA.SetData(1, 0);
            DA.SetData(2, 0);
            return;
        }

        // Safety: block replaces without user filters.
        var nonEmptyFilters = 0;
        foreach (var f in filters)
        {
            var d = f?.Value?.Document;
            if (d == null) continue;
            if (d.ElementCount == 0) continue;
            nonEmptyFilters++;
        }

        if (nonEmptyFilters == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No filters provided. Replace is blocked to prevent accidental mass updates.");
            DA.SetData(0, null);
            DA.SetData(1, 0);
            DA.SetData(2, 0);
            return;
        }

        try
        {
            var db = connGoo.Value.CreateDatabase();
            var col = db.GetCollection<BsonDocument>(collectionName);

            if (_mode == ReplaceMode.Geometry)
            {
                IGH_GeometricGoo geometry = null!;
                Plane anchorPlane = Plane.WorldXY;
                var attrsList = new List<MongoAttributesGoo>();

                if (!DA.GetData(3, ref geometry) || geometry == null) return;
                DA.GetData(4, ref anchorPlane);
                DA.GetDataList(5, attrsList);
                DA.GetData(6, ref run);

                if (!run) return;

                var bytes = GhArchiveGeometrySerializer.Serialize(geometry);
                var geomType = GhArchiveGeometrySerializer.GetGeometryTypeName(geometry);
                var anchorDoc = PlaneBsonConverter.ToBson(anchorPlane);

                var bb = geometry.Boundingbox;
                var bboxDoc = new BsonDocument
                {
                    { "min", new BsonArray { bb.Min.X, bb.Min.Y, bb.Min.Z } },
                    { "max", new BsonArray { bb.Max.X, bb.Max.Y, bb.Max.Z } }
                };

                var attrsDoc = MongoOperations.MergeAttributes(attrsList);

                var filterDoc = MongoOperations.BuildTypedQueryFilter("geometry", "geom", filters);
                var filter = new BsonDocumentFilterDefinition<BsonDocument>(filterDoc);

                var setDoc = new BsonDocument
                {
                    { "type", "geometry" },
                    { "geom", new BsonBinaryData(bytes) },
                    { "geomType", geomType },
                    { "anchor", anchorDoc },
                    { "bbox", bboxDoc },
                    { "attrs", attrsDoc },
                    { "updatedAt", DateTime.UtcNow }
                };

                var update = new BsonDocument("$set", setDoc);
                var res = col.UpdateMany(filter, new BsonDocumentUpdateDefinition<BsonDocument>(update));

                DA.SetData(0, $"Success: Matched {res.MatchedCount}, modified {res.ModifiedCount} document(s)." );
                DA.SetData(1, (int)res.MatchedCount);
                DA.SetData(2, (int)res.ModifiedCount);
                return;
            }

            // Generic
            IGH_Goo dataGoo = null!;
            var attrs = new List<MongoAttributesGoo>();

            if (!DA.GetData(3, ref dataGoo) || dataGoo == null) return;
            DA.GetDataList(4, attrs);
            DA.GetData(5, ref run);

            if (!run) return;

            var bytes2 = GhArchiveGooSerializer.Serialize(dataGoo);
            var dataType = GhArchiveGooSerializer.GetGooTypeName(dataGoo);
            var attrsDoc2 = MongoOperations.MergeAttributes(attrs);

            var filterDoc2 = MongoOperations.BuildTypedQueryFilter("data", "data", filters);
            var filter2 = new BsonDocumentFilterDefinition<BsonDocument>(filterDoc2);

            var setDoc2 = new BsonDocument
            {
                { "type", "data" },
                { "data", new BsonBinaryData(bytes2) },
                { "dataType", dataType },
                { "attrs", attrsDoc2 },
                { "updatedAt", DateTime.UtcNow }
            };

            var update2 = new BsonDocument("$set", setDoc2);
            var res2 = col.UpdateMany(filter2, new BsonDocumentUpdateDefinition<BsonDocument>(update2));

            DA.SetData(0, $"Success: Matched {res2.MatchedCount}, modified {res2.ModifiedCount} document(s)." );
            DA.SetData(1, (int)res2.MatchedCount);
            DA.SetData(2, (int)res2.ModifiedCount);
        }
        catch (Exception ex)
        {
            DA.SetData(0, "Error: " + ex.Message);
            DA.SetData(1, 0);
            DA.SetData(2, 0);
        }
    }
}
