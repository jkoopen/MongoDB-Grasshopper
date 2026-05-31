using System.Drawing;
using System.Text.RegularExpressions;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using GenericMongoPlugin.Components.Algorithms;
using GenericMongoPlugin.Utils;

namespace GenericMongoPlugin.Components;

public sealed class BeamSawingOptimizerComponent : GH_Component
{
    public BeamSawingOptimizerComponent()
        : base(
            "Sawing Algorithm",
            "SawAlgo",
            "Matches desired beam geometry to available stock geometry and simulates sawing to optimize material efficiency. ",
            "MongoDB",
            "Algorithms")
    {
    }

    public override Guid ComponentGuid => new("d6b2d8bc-0c26-4c3e-b7a2-239a45b31f85");

    protected override Bitmap Icon => PluginIcons.Get("component-sawing-optimizer.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddNumberParameter(
            "Accuracy",
            "Acc",
            "0.01–1.00 threshold for how strict the matching is. 1.00 requires the desired extents to fully fit in stock (100% fit). 0.01 accepts matches where only ~1% of the desired extents overlap with stock.",
            GH_ParamAccess.item,
            0.9);

        p.AddGeometryParameter(
            "Stock Geometry",
            "Stock",
            "Stock beam geometry to match against (Brep/Extrusion/Surface/Mesh are converted to Brep when possible).",
            GH_ParamAccess.list);

        p.AddTextParameter(
            "Stock UIDs",
            "UID",
            "UIDs paired to Stock Geometry.",
            GH_ParamAccess.list);

        p.AddGeometryParameter(
            "Desired Geometry",
            "Design",
            "Incoming desired design geometry to be fulfilled by stock beams (Brep/Extrusion/Surface/Mesh are converted to Brep when possible).",
            GH_ParamAccess.list);

        p.AddNumberParameter(
            "Saw Yaw Max",
            "YMax",
            "Max allowed horizontal cut angle in degrees (yaw). 0 means perpendicular cuts only.",
            GH_ParamAccess.item,
            0.0);

        p.AddNumberParameter(
            "Saw Roll Max",
            "RMax",
            "Max allowed vertical cut angle in degrees (roll). 0 means perpendicular cuts only.",
            GH_ParamAccess.item,
            0.0);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddNumberParameter(
            "Efficiency",
            "Eff",
            "Overall efficiency across all processed beams: total result volume / total stock volume.",
            GH_ParamAccess.item);

        p.AddBrepParameter(
            "Result Geometry",
            "Res",
            "Resulting geometry after processing in stock/world orientation (use this to visualize sawing).",
            GH_ParamAccess.list);

        p.AddBrepParameter(
            "Result Geometry (O)",
            "ResO",
            "Resulting geometry oriented back to the design for visualization.",
            GH_ParamAccess.list);

        p.AddTextParameter(
            "Result UIDs",
            "UID",
            "UIDs of the stock beams linked to the Result Geometry.",
            GH_ParamAccess.list);

        p.AddPlaneParameter(
            "Sawing Instructions",
            "Saw",
            "Sawing planes as a tree, where each branch corresponds to a Result Geometry item.",
            GH_ParamAccess.tree);

        p.AddTextParameter(
            "Surplus UIDs",
            "Sur",
            "UIDs of stock beams that were not used to fulfill the design.",
            GH_ParamAccess.list);

        p.AddBrepParameter(
            "Surplus Geometry",
            "SurG",
            "Geometry of stock beams that were not used to fulfill the design (including reusable offcuts).",
            GH_ParamAccess.list);

        p.AddBrepParameter(
            "Unmatched",
            "Unm",
            "Desired geometry items that could not be matched/fulfilled.",
            GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        double accuracy = 0.9;
        var stockGeoGoo = new List<IGH_GeometricGoo>();
        var stockUids = new List<string>();
        var desiredGeoGoo = new List<IGH_GeometricGoo>();
        double horizontalAngleMaxDeg = 0.0;
        double verticalAngleMaxDeg = 0.0;

        if (!DA.GetData(0, ref accuracy)) return;
        if (!DA.GetDataList(1, stockGeoGoo)) return;
        if (!DA.GetDataList(2, stockUids)) return;
        if (!DA.GetDataList(3, desiredGeoGoo)) return;
        DA.GetData(4, ref horizontalAngleMaxDeg);
        DA.GetData(5, ref verticalAngleMaxDeg);

        accuracy = Clamp(accuracy, 0.01, 1.0);
        horizontalAngleMaxDeg = Clamp(Math.Abs(horizontalAngleMaxDeg), 0.0, 89.0);
        verticalAngleMaxDeg = Clamp(Math.Abs(verticalAngleMaxDeg), 0.0, 89.0);

        if (stockGeoGoo.Count != stockUids.Count)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                $"Stock Geometry count ({stockGeoGoo.Count}) must match Stock UIDs count ({stockUids.Count}).");
            return;
        }

        double tol = RhinoDoc.ActiveDoc != null
            ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
            : RhinoMath.ZeroTolerance;

        var resultGeo = new List<Brep>();
        var resultGeoO = new List<Brep>();
        var resultUids = new List<string>();
        var unmatched = new List<Brep>();
        var sawingTree = new GH_Structure<GH_Plane>();

        // Piece UID bookkeeping (for suffixing + reuse).
        // - Base UID: the original incoming stock UID.
        // - Piece UID: Base UID with a "-n" suffix.
        var uidPieceCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        var uidBaseMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var allocatedPieceUids = new HashSet<string>(StringComparer.Ordinal);

        static bool TryParseUidStrict(string uid, out string baseUid, out int? pieceIndex, out string? error)
        {
            baseUid = uid;
            pieceIndex = null;
            error = null;

            if (string.IsNullOrWhiteSpace(uid))
            {
                error = "UID is empty.";
                return false;
            }

            // Only treat as a suffixed UID if it ENDS with -<digits>.
            // Otherwise (e.g. "my-plank"), treat the entire string as the base UID and suffix internally.
            var m = Regex.Match(uid, @"^(?<base>.+)-(?<n>\d+)$");
            if (!m.Success)
                return true;

            baseUid = m.Groups["base"].Value;

            if (!int.TryParse(m.Groups["n"].Value, out int n) || n <= 0)
            {
                error = $"UID '{uid}' is invalid: numeric suffix must be a positive integer.";
                return false;
            }

            pieceIndex = n;
            return true;
        }

        string GetBaseUid(string uid)
            => uidBaseMap.TryGetValue(uid, out var b) ? b : uid;

        string NewPieceUid(string baseUid)
        {
            if (!uidPieceCounters.TryGetValue(baseUid, out int k))
                k = 0;
            k++;
            uidPieceCounters[baseUid] = k;

            string pieceUid = $"{baseUid}-{k}";
            uidBaseMap[pieceUid] = baseUid;
            allocatedPieceUids.Add(pieceUid);
            return pieceUid;
        }

        string EnsurePieceUid(string uid)
        {
            if (allocatedPieceUids.Contains(uid))
                return uid;

            string baseUid = GetBaseUid(uid);
            return NewPieceUid(baseUid);
        }

        // Convert inputs to Breps and keep Stock Geometry paired to Stock UIDs.
        var inventoryGeo = new List<Brep>();
        var inventoryUids = new List<string>();
        var inventoryMeshes = new List<Mesh?>();

        for (int i = 0; i < stockGeoGoo.Count; i++)
        {
            string uidIn = stockUids[i];
            if (!TryParseUidStrict(uidIn, out string baseUidParsed, out int? parsedIndex, out string? uidErr))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Stock UID at index {i} is invalid: {uidErr}");
                continue;
            }

            var brep0 = ToBrep(stockGeoGoo[i]);
            var brep = brep0;
            if (brep0 != null)
            {
                try
                {
                    var capped = brep0.CapPlanarHoles(tol);
                    if (capped != null && capped.IsValid)
                        brep = capped;
                }
                catch
                {
                    brep = brep0;
                }
            }

            if (brep == null || !brep.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Stock Geometry at index {i} (UID '{stockUids[i]}') could not be converted to a valid Brep and will be ignored.");
                continue;
            }

            inventoryGeo.Add(brep);
            inventoryUids.Add(uidIn);
            inventoryMeshes.Add(CreateStockMesh(brep, tol));

            // Seed base UID mapping and counter state.
            uidBaseMap[uidIn] = baseUidParsed;
            if (!uidBaseMap.ContainsKey(baseUidParsed))
                uidBaseMap[baseUidParsed] = baseUidParsed;

            if (parsedIndex.HasValue)
            {
                // Treat incoming uid as already-suffixed piece.
                allocatedPieceUids.Add(uidIn);
                if (!uidPieceCounters.TryGetValue(baseUidParsed, out int k))
                    k = 0;
                uidPieceCounters[baseUidParsed] = Math.Max(k, parsedIndex.Value);
            }
        }

        var desiredGeo = new List<Brep>();
        for (int i = 0; i < desiredGeoGoo.Count; i++)
        {
            var brep = ToBrep(desiredGeoGoo[i]);
            if (brep == null || !brep.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Desired Geometry at index {i} could not be converted to a valid Brep and will be ignored.");
                continue;
            }
            desiredGeo.Add(brep);
        }

        if (inventoryGeo.Count == 0 || desiredGeo.Count == 0)
        {
            DA.SetData(0, 0.0);
            DA.SetDataList(1, new List<Brep>());
            DA.SetDataList(2, new List<Brep>());
            DA.SetDataList(3, new List<string>());
            DA.SetDataTree(4, new GH_Structure<GH_Plane>());
            DA.SetDataList(5, inventoryUids);
            DA.SetDataList(6, inventoryGeo);
            DA.SetDataList(7, desiredGeo);
            return;
        }

        double totalStockVol = 0.0;
        double totalResultVol = 0.0;
        int resultIdx = 0;

        foreach (var target in desiredGeo)
        {
            if (target == null)
                continue;

            if (!target.IsValid)
            {
                unmatched.Add(target);
                continue;
            }

            // Target-local frame and extents.
            Plane tPlane = GetOrthoBeamPlane(target);
            Extents tLocal = GetLocalExtents(target, tPlane, Transform.Identity);

            var desiredSamples = FitUtils.GetSamplePoints(target, maxPoints: 250);
            bool useSampleFit = desiredSamples.Count > 0;

            int bestIdx = -1;
            Plane bestBeamPlane = Plane.Unset;
            Transform bestBeamToTarget = Transform.Identity;
            Transform bestTargetToBeam = Transform.Identity;
            Extents bestBeamLocal = default;
            Extents bestTargetInBeamLocal = default;
            double bestFit = double.NegativeInfinity;
            double bestEfficiency = double.NegativeInfinity;
            int bestCutsNeeded = int.MaxValue;

            // Candidate search.
            for (int i = 0; i < inventoryGeo.Count; i++)
            {
                var beam = inventoryGeo[i];
                if (beam == null || !beam.IsValid) continue;

                Plane bPlaneBase = GetOrthoBeamPlane(beam);

                // Try 4 rotations around beam length axis (rectangular cross-section ambiguity).
                for (int r = 0; r < 4; r++)
                {
                    Plane bPlane = new Plane(bPlaneBase);
                    if (r != 0)
                        bPlane.Rotate((Math.PI * 0.5) * r, bPlane.XAxis, bPlane.Origin);

                    Extents bLocal = GetLocalExtents(beam, bPlane, Transform.Identity);

                    // Align target into this beam frame (base transform).
                    Transform targetToBeamBase = Transform.PlaneToPlane(tPlane, bPlane);
                    Extents tInBeamLocalBase = GetLocalExtents(target, bPlane, targetToBeamBase);

                    // Consider sliding along the beam axis to reduce cuts.
                    // Evaluate: no shift, align desired min->beam min, align desired max->beam max.
                    var beamX = bLocal.X;
                    var targetX = tInBeamLocalBase.X;
                    double dAlignMin = beamX.T0 - targetX.T0;
                    double dAlignMax = beamX.T1 - targetX.T1;

                    EvaluateCandidateShift(0.0);
                    EvaluateCandidateShift(dAlignMin);
                    EvaluateCandidateShift(dAlignMax);

                    void EvaluateCandidateShift(double delta)
                    {
                        // Translation along beam local X just shifts the X interval.
                        var tInBeamLocal = tInBeamLocalBase;
                        tInBeamLocal.X = new Interval(tInBeamLocal.X.T0 + delta, tInBeamLocal.X.T1 + delta);

                        // Compose translation after base alignment.
                        var shift = Transform.Translation(bPlane.XAxis * delta);
                        var desiredToStock = shift * targetToBeamBase;

                        double fit;
                        if (useSampleFit)
                        {
                            fit = FitUtils.ComputeFitRatioSampled(inventoryMeshes[i], beam, desiredSamples, desiredToStock, tol);
                        }
                        else
                        {
                            // Fallback: extents-overlap is cheap and works even when sampling fails.
                            fit =
                                OverlapFraction(tInBeamLocal.X, bLocal.X) *
                                OverlapFraction(tInBeamLocal.Y, bLocal.Y) *
                                OverlapFraction(tInBeamLocal.Z, bLocal.Z);
                        }

                        if (fit + 1e-12 < accuracy) return;

                        double beamVol = SafeOrApproxVolume(beam, bLocal);
                        double targetVol = SafeOrApproxVolume(target, tLocal);
                        if (beamVol <= 0 || targetVol <= 0) return;

                        double candidateEfficiency = Clamp(targetVol / beamVol, 0.0, 1.0);
                        int cutsNeeded = CutsNeeded(tInBeamLocal.X, bLocal.X, tol);

                        // Rank: higher fit, then fewer cuts, then higher efficiency.
                        if (fit > bestFit + 1e-12 ||
                            (Math.Abs(fit - bestFit) <= 1e-12 && cutsNeeded < bestCutsNeeded) ||
                            (Math.Abs(fit - bestFit) <= 1e-12 && cutsNeeded == bestCutsNeeded && candidateEfficiency > bestEfficiency))
                        {
                            bestFit = fit;
                            bestCutsNeeded = cutsNeeded;
                            bestEfficiency = candidateEfficiency;
                            bestIdx = i;
                            bestBeamPlane = bPlane;

                            bestTargetToBeam = desiredToStock;

                            Transform inv;
                            bestBeamToTarget = bestTargetToBeam.TryGetInverse(out inv) ? inv : Transform.Identity;
                            bestBeamLocal = bLocal;
                            bestTargetInBeamLocal = tInBeamLocal;
                        }
                    }
                }
            }

            if (bestIdx < 0)
            {
                unmatched.Add(target);
                continue;
            }

            // Execute sawing: end cuts only, in the chosen beam frame.
            var chosenBeam = inventoryGeo[bestIdx];
            var chosenUid = inventoryUids[bestIdx];
            var baseUid = GetBaseUid(chosenUid);
            var pieceUid = EnsurePieceUid(chosenUid);
            Plane beamPlane = bestBeamPlane.IsValid ? bestBeamPlane : GetOrthoBeamPlane(chosenBeam);

            var cutResult = chosenBeam.DuplicateBrep();
            var confirmedCuts = new List<Plane>();
            var offcuts = new List<Brep>();

            Interval bx = bestBeamLocal.X;
            Interval tx = bestTargetInBeamLocal.X;

            // Compute desired end-face planes (in target frame), then transform them into beam/world space.
            // If end faces aren't planar or exceed angle limits, we fall back to perpendicular planes.
            Plane desiredStartPlaneW = new Plane(beamPlane.PointAt(tx.T0, 0, 0), beamPlane.XAxis);
            Plane desiredEndPlaneW = new Plane(beamPlane.PointAt(tx.T1, 0, 0), beamPlane.XAxis);
            if (TryGetEndFacePlanes(target, tPlane, tol, out Plane tStart, out Plane tEnd))
            {
                desiredStartPlaneW = tStart;
                desiredStartPlaneW.Transform(bestTargetToBeam);

                desiredEndPlaneW = tEnd;
                desiredEndPlaneW.Transform(bestTargetToBeam);

                if (!WithinSawAngles(desiredStartPlaneW, beamPlane, horizontalAngleMaxDeg, verticalAngleMaxDeg))
                    desiredStartPlaneW = new Plane(beamPlane.PointAt(tx.T0, 0, 0), beamPlane.XAxis);

                if (!WithinSawAngles(desiredEndPlaneW, beamPlane, horizontalAngleMaxDeg, verticalAngleMaxDeg))
                    desiredEndPlaneW = new Plane(beamPlane.PointAt(tx.T1, 0, 0), beamPlane.XAxis);
            }

            // Desired center in world (beam) space for robust split selection.
            Point3d desiredCenterW = target.GetBoundingBox(true).Center;
            desiredCenterW.Transform(bestTargetToBeam);

            // Since target is inside beam bounds, tx.T0/tx.T1 should fall within bx.
            // Generate a cut plane if the beam extends beyond the target on that end.
            var cutXs = new List<double>();
            var cutPlanes = new List<Plane>();
            if (tx.T0 > bx.T0 + tol) cutPlanes.Add(desiredStartPlaneW);
            if (tx.T1 < bx.T1 - tol) cutPlanes.Add(desiredEndPlaneW);

            foreach (Plane p0 in cutPlanes)
            {
                var p = p0;

                double ySpan = Math.Max(100.0, bestBeamLocal.Y.Length * 2.0);
                double zSpan = Math.Max(100.0, bestBeamLocal.Z.Length * 2.0);
                var surf = new PlaneSurface(p, new Interval(-ySpan, ySpan), new Interval(-zSpan, zSpan));

                Brep[]? split;
                try { split = cutResult.Split(surf.ToBrep(), tol); }
                catch { split = null; }

                if (split == null || split.Length == 0) continue;

                // Keep the piece closest to the desired center; all others become offcuts we can reuse.
                var ordered = split
                    .Where(b => b != null && b.IsValid)
                    .OrderBy(piece => piece.GetBoundingBox(true).Center.DistanceTo(desiredCenterW))
                    .ToArray();

                cutResult = ordered.FirstOrDefault();
                for (int si = 1; si < ordered.Length; si++)
                    offcuts.Add(ordered[si]);

                if (cutResult == null) break;
                confirmedCuts.Add(p);
            }

            if (cutResult != null)
            {
                try { cutResult = cutResult.CapPlanarHoles(tol); }
                catch { /* ignore */ }
            }

            if (cutResult == null || !cutResult.IsValid)
            {
                unmatched.Add(target);
                continue;
            }

            // Output geometry in world/stock orientation (for sawing visualization).
            resultGeo.Add(cutResult);

            // Output geometry oriented back to the design (for design visualization).
            var outBrep = cutResult.DuplicateBrep();
            outBrep.Transform(bestBeamToTarget);

            resultGeoO.Add(outBrep);
            resultUids.Add(pieceUid);

            var path = new GH_Path(resultIdx);
            // Sawing planes stay in world/stock orientation to align with Result Geometry.
            sawingTree.AppendRange(confirmedCuts.Select(pl => new GH_Plane(pl)), path);

            // Efficiency accounting.
            var cutLocal = GetLocalExtents(cutResult, beamPlane, Transform.Identity);
            totalStockVol += SafeOrApproxVolume(chosenBeam, bestBeamLocal);
            totalResultVol += SafeOrApproxVolume(cutResult, cutLocal);

            // Remove used stock.
            inventoryGeo.RemoveAt(bestIdx);
            inventoryUids.RemoveAt(bestIdx);
            inventoryMeshes.RemoveAt(bestIdx);

            // Reuse offcuts as new stock inventory.
            // The offcuts keep the same base UID, but get new piece suffix indices.
            foreach (var off in offcuts)
            {
                if (off == null || !off.IsValid) continue;

                Brep offBrep = off;
                try
                {
                    var capped = offBrep.CapPlanarHoles(tol);
                    if (capped != null && capped.IsValid)
                        offBrep = capped;
                }
                catch
                {
                    offBrep = off;
                }

                if (offBrep == null || !offBrep.IsValid) continue;

                // Ignore tiny fragments.
                var offLocal = GetLocalExtents(offBrep, beamPlane, Transform.Identity);
                var offVol = SafeOrApproxVolume(offBrep, offLocal);
                if (offVol <= tol * tol * tol) continue;

                string offUid = NewPieceUid(baseUid);
                inventoryGeo.Add(offBrep);
                inventoryUids.Add(offUid);
                inventoryMeshes.Add(CreateStockMesh(offBrep, tol));
            }

            resultIdx++;
        }

        double efficiency = totalStockVol > 0 ? Clamp(totalResultVol / totalStockVol, 0.0, 1.0) : 0.0;

        DA.SetData(0, efficiency);
        DA.SetDataList(1, resultGeo);
        DA.SetDataList(2, resultGeoO);
        DA.SetDataList(3, resultUids);
        DA.SetDataTree(4, sawingTree);
        DA.SetDataList(5, inventoryUids);
        DA.SetDataList(6, inventoryGeo);
        DA.SetDataList(7, unmatched);
    }

    private static Mesh? CreateStockMesh(Brep stock, double tol)
    {
        if (stock == null || !stock.IsValid)
            return null;

        try
        {
            // Ensure as-solid-as-possible input for meshing.
            Brep s = stock;
            try
            {
                var capped = stock.CapPlanarHoles(tol);
                if (capped != null && capped.IsValid)
                    s = capped;
            }
            catch
            {
                s = stock;
            }

            var parts = Mesh.CreateFromBrep(s, MeshingParameters.FastRenderMesh);
            if (parts == null || parts.Length == 0)
                return null;

            var m = new Mesh();
            foreach (var mm in parts)
                if (mm != null) m.Append(mm);

            if (m.Vertices.Count == 0)
                return null;

            m.Normals.ComputeNormals();
            m.Compact();
            return m;
        }
        catch
        {
            return null;
        }
    }

    private struct Extents
    {
        public Interval X;
        public Interval Y;
        public Interval Z;
    }

    private static double Clamp(double v, double min, double max)
        => v < min ? min : v > max ? max : v;

    private static Brep? ToBrep(IGH_GeometricGoo? goo)
    {
        if (goo == null) return null;

        object? v = null;
        try { v = goo.ScriptVariable(); }
        catch { v = null; }

        if (v == null) return null;

        try
        {
            switch (v)
            {
                case Brep b:
                    return b;
                case Extrusion ex:
                    return ex.ToBrep();
                case Surface s:
                    return s.ToBrep();
                case Mesh m:
                    return Brep.CreateFromMesh(m, true);
                case Box box:
                    return box.ToBrep();
                case GeometryBase gb:
                {
                    var converted = Brep.TryConvertBrep(gb);
                    if (converted != null) return converted;
                    break;
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static bool Within(Interval inner, Interval outer, double tol)
        => inner.T0 >= outer.T0 - tol && inner.T1 <= outer.T1 + tol;

    private static double OverlapFraction(Interval desired, Interval stock)
    {
        // Fraction of the desired interval length that overlaps with the stock interval.
        // Returns 0 when desired length is zero.
        double desiredLen = desired.Length;
        if (desiredLen <= RhinoMath.ZeroTolerance)
            return 0.0;

        double a0 = Math.Max(desired.T0, stock.T0);
        double a1 = Math.Min(desired.T1, stock.T1);
        double inter = Math.Max(0.0, a1 - a0);
        return Clamp(inter / desiredLen, 0.0, 1.0);
    }

    private static int CutsNeeded(Interval desiredInBeam, Interval stockInBeam, double tol)
    {
        // For end-cuts: you need a cut wherever stock extends beyond the desired interval.
        int cuts = 0;
        if (desiredInBeam.T0 > stockInBeam.T0 + tol) cuts++;
        if (desiredInBeam.T1 < stockInBeam.T1 - tol) cuts++;
        return cuts;
    }

    private static bool TryGetEndFacePlanes(Brep target, Plane targetFrame, double tol, out Plane start, out Plane end)
    {
        start = Plane.Unset;
        end = Plane.Unset;
        if (target == null || !target.IsValid) return false;

        // We want planes representing the two end faces (roughly normal to the beam axis).
        // Approach: pick planar faces whose normals are most aligned with +/- targetFrame.XAxis.
        double bestPos = -1.0;
        double bestNeg = -1.0;
        Plane bestPosPlane = Plane.Unset;
        Plane bestNegPlane = Plane.Unset;

        try
        {
            for (int i = 0; i < target.Faces.Count; i++)
            {
                var face = target.Faces[i];
                if (!face.IsPlanar(tol)) continue;

                if (!face.TryGetPlane(out Plane fp, tol)) continue;

                Vector3d n = fp.Normal;
                if (!n.Unitize()) continue;

                double d = n * targetFrame.XAxis; // [-1..1]
                double ad = Math.Abs(d);

                // Ignore side faces.
                if (ad < 0.5) continue;

                if (d > 0 && ad > bestPos)
                {
                    bestPos = ad;
                    bestPosPlane = fp;
                }
                else if (d < 0 && ad > bestNeg)
                {
                    bestNeg = ad;
                    bestNegPlane = fp;
                }
            }
        }
        catch
        {
            return false;
        }

        if (!bestPosPlane.IsValid || !bestNegPlane.IsValid)
            return false;

        // Ensure the plane normals point in opposite directions (+X and -X).
        // For cutting we only need a plane; normal direction doesn't matter, but consistency helps angle checks.
        start = bestNegPlane;
        end = bestPosPlane;
        return true;
    }

    private static bool WithinSawAngles(Plane cutPlaneWorld, Plane beamFrameWorld, double hMaxDeg, double vMaxDeg)
    {
        // The perpendicular "reference" cut plane has normal = beamFrameWorld.XAxis.
        // We decompose the deviation of the cut normal from that axis into:
        // - horizontal (miter): rotation towards beam Y
        // - vertical (bevel): rotation towards beam Z
        // This is an approximation that matches workshop intuition.

        Vector3d n = cutPlaneWorld.Normal;
        if (!n.Unitize()) return false;

        Vector3d x = beamFrameWorld.XAxis;
        Vector3d y = beamFrameWorld.YAxis;
        Vector3d z = beamFrameWorld.ZAxis;
        x.Unitize(); y.Unitize(); z.Unitize();

        double nx = n * x;
        double ny = n * y;
        double nz = n * z;

        // Allow both directions (+/-X) since plane normal can flip.
        nx = Math.Abs(nx);

        // Project deviation angles.
        // horizontal angle = atan2(|ny|, nx)
        // vertical angle   = atan2(|nz|, nx)
        double hDeg = RhinoMath.ToDegrees(Math.Atan2(Math.Abs(ny), Math.Max(RhinoMath.ZeroTolerance, nx)));
        double vDeg = RhinoMath.ToDegrees(Math.Atan2(Math.Abs(nz), Math.Max(RhinoMath.ZeroTolerance, nx)));

        return hDeg <= hMaxDeg + 1e-9 && vDeg <= vMaxDeg + 1e-9;
    }

    private static double GetLocalCenterX(Brep b, Plane plane)
    {
        if (b == null) return double.PositiveInfinity;
        BoundingBox bb = b.GetBoundingBox(true);
        Point3d c = bb.Center;
        Vector3d v = c - plane.Origin;
        return v * plane.XAxis;
    }

    private static IEnumerable<Point3d> SamplePoints(Brep b)
    {
        if (b == null) yield break;

        Point3d[]? verts = null;
        try { verts = b.DuplicateVertices(); }
        catch { verts = null; }

        if (verts != null && verts.Length > 0)
        {
            foreach (var pt in verts) yield return pt;
            yield break;
        }

        BoundingBox bb = b.GetBoundingBox(true);
        if (!bb.IsValid) yield break;

        foreach (var c in bb.GetCorners())
            yield return c;
    }

    private static Extents GetLocalExtents(Brep b, Plane localPlane, Transform preTransform)
    {
        bool any = false;
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;

        foreach (Point3d p0 in SamplePoints(b))
        {
            Point3d p = p0;
            if (!preTransform.IsIdentity) p.Transform(preTransform);

            Vector3d v = p - localPlane.Origin;
            double x = v * localPlane.XAxis;
            double y = v * localPlane.YAxis;
            double z = v * localPlane.ZAxis;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
            any = true;
        }

        if (!any)
        {
            return new Extents
            {
                X = new Interval(0, 0),
                Y = new Interval(0, 0),
                Z = new Interval(0, 0)
            };
        }

        return new Extents
        {
            X = new Interval(minX, maxX),
            Y = new Interval(minY, maxY),
            Z = new Interval(minZ, maxZ)
        };
    }

    private static double SafeVolume(Brep b)
    {
        try
        {
            return (b != null && b.IsValid) ? Math.Abs(b.GetVolume()) : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    private static double SafeOrApproxVolume(Brep b, Extents extents)
    {
        double v = SafeVolume(b);
        if (v > 0) return v;
        return Math.Abs(extents.X.Length * extents.Y.Length * extents.Z.Length);
    }

    private static Plane GetOrthoBeamPlane(Brep b)
    {
        if (b == null)
            return Plane.WorldXY;

        VolumeMassProperties? vmp = null;
        try { vmp = VolumeMassProperties.Compute(b); }
        catch { vmp = null; }

        Point3d center = (vmp != null) ? vmp.Centroid : b.GetBoundingBox(true).Center;

        var samples = new List<Vector3d>();
        foreach (Point3d p in SamplePoints(b))
        {
            Vector3d v = p - center;
            if (v.IsTiny()) continue;
            samples.Add(v);
        }

        Vector3d xDir = Vector3d.XAxis;
        if (samples.Count >= 3)
        {
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var v in samples)
            {
                xx += v.X * v.X;
                xy += v.X * v.Y;
                xz += v.X * v.Z;
                yy += v.Y * v.Y;
                yz += v.Y * v.Z;
                zz += v.Z * v.Z;
            }

            Vector3d v0 = new Vector3d(1, 0.3, 0.1);
            v0.Unitize();
            for (int k = 0; k < 20; k++)
            {
                Vector3d v1 = new Vector3d(
                    xx * v0.X + xy * v0.Y + xz * v0.Z,
                    xy * v0.X + yy * v0.Y + yz * v0.Z,
                    xz * v0.X + yz * v0.Y + zz * v0.Z
                );

                if (v1.IsTiny()) break;
                v1.Unitize();
                v0 = v1;
            }

            xDir = v0;
        }

        if (!xDir.Unitize()) xDir = Vector3d.XAxis;

        Vector3d yDir = Vector3d.Zero;
        try
        {
            foreach (var face in b.Faces)
            {
                Vector3d n = face.NormalAt(0.5, 0.5);
                if (!n.Unitize()) continue;
                if (Math.Abs(n * xDir) < 0.1)
                {
                    yDir = n;
                    break;
                }
            }
        }
        catch
        {
            yDir = Vector3d.Zero;
        }

        if (yDir.IsZero)
        {
            yDir = Vector3d.CrossProduct(xDir, Vector3d.ZAxis);
            if (yDir.IsTiny()) yDir = Vector3d.CrossProduct(xDir, Vector3d.YAxis);
        }

        yDir = yDir - (yDir * xDir) * xDir;
        if (!yDir.Unitize()) yDir = Vector3d.YAxis;
        Vector3d zDir = Vector3d.CrossProduct(xDir, yDir);
        if (!zDir.Unitize()) zDir = Vector3d.ZAxis;
        yDir = Vector3d.CrossProduct(zDir, xDir);
        yDir.Unitize();

        return new Plane(center, xDir, yDir);
    }
}
