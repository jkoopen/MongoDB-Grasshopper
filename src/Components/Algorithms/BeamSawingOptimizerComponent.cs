using System.Drawing;
using System.Text.RegularExpressions;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
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

        p.AddNumberParameter(
            "Tolerance",
            "Tol",
            "Processing tolerance in millimeters. Used for split validation, cut matching, and collision checks.",
            GH_ParamAccess.item,
            1.0);

        p.AddIntegerParameter(
            "Max Retries",
            "Retries",
            "Maximum retry attempts per desired beam before giving up and marking it unmatched.",
            GH_ParamAccess.item,
            12);
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

        p.AddTextParameter(
            "Debug Log",
            "Dbg",
            "Per-beam debug log describing the matching result and the main rejection reason when a beam stays unmatched.",
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
        double toleranceMm = 1.0;
        int maxRetries = 12;

        if (!DA.GetData(0, ref accuracy)) return;
        if (!DA.GetDataList(1, stockGeoGoo)) return;
        if (!DA.GetDataList(2, stockUids)) return;
        if (!DA.GetDataList(3, desiredGeoGoo)) return;
        DA.GetData(4, ref horizontalAngleMaxDeg);
        DA.GetData(5, ref verticalAngleMaxDeg);
        DA.GetData(6, ref toleranceMm);
        DA.GetData(7, ref maxRetries);

        accuracy = Clamp(accuracy, 0.01, 1.0);
        horizontalAngleMaxDeg = Clamp(Math.Abs(horizontalAngleMaxDeg), 0.0, 89.0);
        verticalAngleMaxDeg = Clamp(Math.Abs(verticalAngleMaxDeg), 0.0, 89.0);
        toleranceMm = Math.Max(0.001, Math.Abs(toleranceMm));
        maxRetries = Math.Max(1, maxRetries);

        if (stockGeoGoo.Count != stockUids.Count)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                $"Stock Geometry count ({stockGeoGoo.Count}) must match Stock UIDs count ({stockUids.Count}).");
            return;
        }

        double tol = RhinoDoc.ActiveDoc != null
            ? Math.Max(RhinoDoc.ActiveDoc.ModelAbsoluteTolerance, toleranceMm)
            : toleranceMm;

        var resultGeo = new List<Brep>();
        var resultGeoO = new List<Brep>();
        var resultUids = new List<string>();
        var unmatched = new List<Brep>();
        var sawingTree = new GH_Structure<GH_Plane>();
        var debugLog = new List<string>();

        var inventoryGeo = new List<Brep>();
        var inventoryUids = new List<string>();
        var inventoryMeshes = new List<Mesh?>();
        var inventoryIsOffcut = new List<bool>();

        for (int i = 0; i < stockGeoGoo.Count; i++)
        {
            var stockBrep = FinalizeClosedBrep(ToBrep(stockGeoGoo[i]), tol) ?? ToBrep(stockGeoGoo[i]);
            if (stockBrep == null || !stockBrep.IsValid)
                continue;

            inventoryGeo.Add(stockBrep);
            inventoryUids.Add(stockUids[i]);
            inventoryMeshes.Add(CreateStockMesh(stockBrep, tol));
            inventoryIsOffcut.Add(false);
        }

        double totalStockVol = 0.0;
        double totalResultVol = 0.0;
        int resultIdx = 0;

        for (int targetIndex = 0; targetIndex < desiredGeoGoo.Count; targetIndex++)
        {
            var target = FinalizeClosedBrep(ToBrep(desiredGeoGoo[targetIndex]), tol) ?? ToBrep(desiredGeoGoo[targetIndex]);
            if (target == null || !target.IsValid)
            {
                unmatched.Add(target ?? new Brep());
                debugLog.Add($"Beam {targetIndex}: invalid desired geometry.");
                continue;
            }

            Plane tPlane = GetOrthoBeamPlane(target);
            Extents tLocal = GetLocalExtents(target, tPlane, Transform.Identity);

            // We'll allow retrying candidate selection if the produced placement collides
            // with already accepted result geometry. Track tried inventory indices.
            var triedIndices = new HashSet<int>();
            bool accepted = false;
            int retryCount = 0;

            while (!accepted && retryCount < maxRetries)
            {
                retryCount++;
                int bestIdxLocal = -1;
                Plane bestBeamPlaneLocal = Plane.Unset;
                Transform bestBeamToTargetLocal = Transform.Identity;
                Transform bestTargetToBeamLocal = Transform.Identity;
                Extents bestBeamLocal = default;
                Extents bestTargetInBeamLocal = default;
                double bestFitLocal = double.NegativeInfinity;
                double bestEfficiencyLocal = double.NegativeInfinity;
                double bestWasteLocal = double.PositiveInfinity;

                // Two-phase search:
                // 1) use only straight/original stock while any remains
                // 2) once straight stock is exhausted, allow irregular offcuts too
                bool allowOffcuts = !inventoryIsOffcut.Any(isOffcut => !isOffcut);

                // Candidate search excluding tried indices.
                for (int i = 0; i < inventoryGeo.Count; i++)
                {
                    if (triedIndices.Contains(i)) continue;
                    if (!allowOffcuts && inventoryIsOffcut[i]) continue;

                    var beam = inventoryGeo[i];
                    if (beam == null || !beam.IsValid) continue;

                    Plane bPlaneBase = GetOrthoBeamPlane(beam);

                    for (int r = 0; r < 4; r++)
                    {
                        Plane bPlane = new Plane(bPlaneBase);
                        if (r != 0)
                            bPlane.Rotate((Math.PI * 0.5) * r, bPlane.XAxis, bPlane.Origin);

                        Extents bLocal = GetLocalExtents(beam, bPlane, Transform.Identity);
                        Transform targetToBeamBase = Transform.PlaneToPlane(tPlane, bPlane);
                        Extents tInBeamLocalBase = GetLocalExtents(target, bPlane, targetToBeamBase);

                        var beamX = bLocal.X;
                        var targetX = tInBeamLocalBase.X;
                        double dAlignMin = beamX.T0 - targetX.T0;
                        double dAlignMax = beamX.T1 - targetX.T1;

                        EvaluateCandidateShift(0.0);
                        EvaluateCandidateShift(dAlignMin);
                        EvaluateCandidateShift(dAlignMax);

                        void EvaluateCandidateShift(double delta)
                        {
                            var tInBeamLocal = tInBeamLocalBase;
                            tInBeamLocal.X = new Interval(tInBeamLocal.X.T0 + delta, tInBeamLocal.X.T1 + delta);
                            var shift = Transform.Translation(bPlane.XAxis * delta);
                            var desiredToStock = shift * targetToBeamBase;

                            bool lengthFits = Within(tInBeamLocal.X, bLocal.X, tol);
                            if (!lengthFits) return;

                            double fit = 1.0;
                            double beamVol = SafeOrApproxVolume(beam, bLocal);
                            double targetVol = SafeOrApproxVolume(target, tLocal);
                            if (beamVol <= 0 || targetVol <= 0) return;

                            double candidateEfficiency = Clamp(targetVol / beamVol, 0.0, 1.0);
                            double waste = Math.Max(0.0, bLocal.X.Length - tInBeamLocal.X.Length);
                            double sideMismatch =
                                Math.Max(Math.Max(0.0, tInBeamLocal.Y.T0 - bLocal.Y.T0), Math.Max(0.0, bLocal.Y.T1 - tInBeamLocal.Y.T1)) +
                                Math.Max(Math.Max(0.0, tInBeamLocal.Z.T0 - bLocal.Z.T0), Math.Max(0.0, bLocal.Z.T1 - tInBeamLocal.Z.T1));

                            if (fit > bestFitLocal + 1e-12 ||
                                (Math.Abs(fit - bestFitLocal) <= 1e-12 && sideMismatch < bestWasteLocal - 1e-12) ||
                                (Math.Abs(fit - bestFitLocal) <= 1e-12 && Math.Abs(sideMismatch - bestWasteLocal) <= 1e-12 && waste < bestEfficiencyLocal - 1e-12))
                            {
                                bestFitLocal = fit;
                                bestWasteLocal = sideMismatch;
                                bestEfficiencyLocal = waste;
                                bestIdxLocal = i;
                                bestBeamPlaneLocal = bPlane;

                                bestTargetToBeamLocal = desiredToStock;

                                Transform inv;
                                bestBeamToTargetLocal = bestTargetToBeamLocal.TryGetInverse(out inv) ? inv : Transform.Identity;
                                bestBeamLocal = bLocal;
                                bestTargetInBeamLocal = tInBeamLocal;
                            }
                        }
                    }
                }

                if (bestIdxLocal < 0)
                {
                    unmatched.Add(FinalizeClosedBrep(target, tol) ?? target);
                    debugLog.Add($"Beam {targetIndex}: no candidate remaining (tried {triedIndices.Count}).");
                    break;
                }

                // Prepare to execute sawing for the chosen candidate.
                var chosenBeam = inventoryGeo[bestIdxLocal];
                var chosenUid = inventoryUids[bestIdxLocal];
                var baseUid = GetBaseUid(chosenUid);
                var pieceUid = EnsurePieceUid(chosenUid);
                Plane beamPlane = bestBeamPlaneLocal.IsValid ? bestBeamPlaneLocal : GetOrthoBeamPlane(chosenBeam);

                var cutResult = chosenBeam.DuplicateBrep();
                var confirmedCuts = new List<Plane>();
                var offcuts = new List<Brep>();

                Interval bx = bestBeamLocal.X;
                Interval tx = bestTargetInBeamLocal.X;

                Plane desiredStartPlaneW = new Plane(beamPlane.PointAt(tx.T0, 0, 0), beamPlane.XAxis);
                Plane desiredEndPlaneW = new Plane(beamPlane.PointAt(tx.T1, 0, 0), beamPlane.XAxis);
                if (TryGetEndFacePlanes(target, tPlane, tol, out Plane tStart, out Plane tEnd))
                {
                    Plane startCandidate = tStart;
                    startCandidate.Transform(bestTargetToBeamLocal);

                    Plane endCandidate = tEnd;
                    endCandidate.Transform(bestTargetToBeamLocal);

                    if (WithinSawAngles(startCandidate, beamPlane, horizontalAngleMaxDeg, verticalAngleMaxDeg))
                        desiredStartPlaneW = startCandidate;

                    if (WithinSawAngles(endCandidate, beamPlane, horizontalAngleMaxDeg, verticalAngleMaxDeg))
                        desiredEndPlaneW = endCandidate;
                }
                double desiredCenterX = (tx.T0 + tx.T1) * 0.5;

                var cutPlanes = new List<Plane> { desiredStartPlaneW, desiredEndPlaneW };
                foreach (Plane p0 in cutPlanes)
                {
                    var p = p0;
                    double ySpan = Math.Max(100.0, bestBeamLocal.Y.Length * 2.0);
                    double zSpan = Math.Max(100.0, bestBeamLocal.Z.Length * 2.0);
                    var surf = new PlaneSurface(p, new Interval(-ySpan, ySpan), new Interval(-zSpan, zSpan));
                    var surfBrep = surf.ToBrep(); if (surfBrep == null) continue;

                    Brep[]? split;
                    try { split = cutResult.Split(surfBrep, tol); } catch { split = null; }
                    if (split == null || split.Length == 0) continue;

                    var ordered = split.Where(b => b != null && b.IsValid)
                        .Select(piece => new
                        {
                            Piece = piece,
                            Local = GetLocalExtents(piece, beamPlane, Transform.Identity)
                        })
                        .OrderByDescending(x => OverlapFraction(tx, x.Local.X))
                        .ThenBy(x => Math.Abs(GetLocalCenterX(x.Piece, beamPlane) - desiredCenterX))
                        .Select(x => x.Piece)
                        .ToArray();

                    cutResult = ordered.FirstOrDefault();
                    for (int si = 1; si < ordered.Length; si++) offcuts.Add(ordered[si]);
                    if (cutResult == null) break;
                    cutResult = FinalizeClosedBrep(cutResult, tol);
                    if (cutResult == null) break;
                    confirmedCuts.Add(p);
                }

                cutResult = FinalizeClosedBrep(cutResult, tol);
                if (cutResult == null || !cutResult.IsValid)
                {
                    triedIndices.Add(bestIdxLocal);
                    debugLog.Add($"Beam {targetIndex}: candidate {chosenUid} produced invalid cut result, trying next candidate.");
                    continue;
                }

                var cutLocal = GetLocalExtents(cutResult, beamPlane, Transform.Identity);
                if (!MatchesDesiredCut(cutLocal, bestTargetInBeamLocal, tol))
                {
                    triedIndices.Add(bestIdxLocal);
                    debugLog.Add($"Beam {targetIndex}: candidate {chosenUid} cut dimensions do not match the desired beam slice, trying next candidate.");
                    continue;
                }

                var outBrep = FinalizeClosedBrep(cutResult.DuplicateBrep(), tol);
                if (outBrep == null)
                {
                    triedIndices.Add(bestIdxLocal);
                    debugLog.Add($"Beam {targetIndex}: candidate {chosenUid} duplicate/transform failed, trying next.");
                    continue;
                }
                outBrep.Transform(bestBeamToTargetLocal);
                outBrep = FinalizeClosedBrep(outBrep, tol);
                if (outBrep == null)
                {
                    triedIndices.Add(bestIdxLocal);
                    debugLog.Add($"Beam {targetIndex}: candidate {chosenUid} finalization failed, trying next.");
                    continue;
                }

                // Collision check: compare with already accepted result geometry.
                bool hasCollision = false;
                try
                {
                    var outBox = outBrep.GetBoundingBox(true);
                    foreach (var placed in resultGeoO)
                    {
                        if (placed == null || !placed.IsValid) continue;
                        var placedBox = placed.GetBoundingBox(true);
                        if (outBox.Max.X < placedBox.Min.X - tol || outBox.Min.X > placedBox.Max.X + tol) continue;
                        if (outBox.Max.Y < placedBox.Min.Y - tol || outBox.Min.Y > placedBox.Max.Y + tol) continue;
                        if (outBox.Max.Z < placedBox.Min.Z - tol || outBox.Min.Z > placedBox.Max.Z + tol) continue;
                        var inter = Brep.CreateBooleanIntersection(outBrep, placed, tol);
                        if (inter != null && inter.Length > 0)
                        {
                            double v = 0.0; foreach (var ib in inter) if (ib != null && ib.IsValid) v += SafeVolume(ib);
                            if (v > tol * tol * tol) { hasCollision = true; break; }
                        }
                    }
                }
                catch
                {
                    hasCollision = false;
                }

                if (hasCollision)
                {
                    triedIndices.Add(bestIdxLocal);
                    debugLog.Add($"Beam {targetIndex}: candidate {chosenUid} collides with placed geometry; trying next candidate.");
                    continue;
                }

                // Accept this candidate: record results and update inventory.
                resultGeo.Add(cutResult);
                resultUids.Add(pieceUid);
                resultGeoO.Add(outBrep);

                var path = new GH_Path(resultIdx);
                sawingTree.AppendRange(confirmedCuts.Select(pl => new GH_Plane(pl)), path);

                totalStockVol += SafeOrApproxVolume(chosenBeam, bestBeamLocal);
                totalResultVol += SafeOrApproxVolume(cutResult, cutLocal);

                inventoryGeo.RemoveAt(bestIdxLocal);
                inventoryUids.RemoveAt(bestIdxLocal);
                inventoryMeshes.RemoveAt(bestIdxLocal);
                inventoryIsOffcut.RemoveAt(bestIdxLocal);

                foreach (var off in offcuts)
                {
                    if (off == null || !off.IsValid) continue;
                    Brep? offBrep = FinalizeClosedBrep(off, tol);
                    if (offBrep == null || !offBrep.IsValid) continue;
                    var offLocal = GetLocalExtents(offBrep, beamPlane, Transform.Identity);
                    var offVol = SafeOrApproxVolume(offBrep, offLocal);
                    if (offVol <= tol * tol * tol) continue;
                    string offUid = NewPieceUid(baseUid);
                    inventoryGeo.Add(offBrep);
                    inventoryUids.Add(offUid);
                    inventoryMeshes.Add(CreateStockMesh(offBrep, tol));
                    inventoryIsOffcut.Add(true);
                }

                debugLog.Add($"Beam {targetIndex}: matched to stock '{chosenUid}' -> '{pieceUid}' using inventory index {bestIdxLocal}; tried {triedIndices.Count} failures before accept; result solid={cutResult.IsSolid}, surplus offcuts={offcuts.Count}.");

                accepted = true;
                resultIdx++;
            }

            if (!accepted)
            {
                unmatched.Add(target);
                debugLog.Add($"Beam {targetIndex}: exhausted retries ({retryCount}/{maxRetries}) without a valid placement.");
            }
        }

        double efficiency = totalStockVol > 0 ? Clamp(totalResultVol / totalStockVol, 0.0, 1.0) : 0.0;

        DA.SetData(0, efficiency);
        DA.SetDataList(1, resultGeo);
        DA.SetDataList(2, resultGeoO);
        DA.SetDataList(3, resultUids);
        DA.SetDataTree(4, sawingTree);
        DA.SetDataList(5, inventoryUids);
        DA.SetDataList(6, inventoryGeo.Select(b => FinalizeClosedBrep(b, tol) ?? b).ToList());
        DA.SetDataList(7, unmatched.Select(b => FinalizeClosedBrep(b, tol) ?? b).ToList());
        DA.SetDataList(8, debugLog);
    }

    private static string GetBaseUid(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return "piece";

        var match = Regex.Match(uid, @"^(.*?)(?:-\d+)?$");
        if (!match.Success)
            return uid;

        var baseUid = match.Groups[1].Value;
        return string.IsNullOrWhiteSpace(baseUid) ? uid : baseUid;
    }

    private static string EnsurePieceUid(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return "piece-1";

        if (Regex.IsMatch(uid, @"-\d+$"))
            return uid;

        return $"{uid}-1";
    }

    private static string NewPieceUid(string baseUid)
        => $"{GetBaseUid(baseUid)}-{Guid.NewGuid():N}";

    private static Brep? FinalizeClosedBrep(Brep? brep, double tol)
    {
        if (brep == null || !brep.IsValid)
            return null;

        Brep result = brep;

        try
        {
            var capped = result.CapPlanarHoles(tol);
            if (capped != null && capped.IsValid)
                result = capped;
        }
        catch
        {
            // ignore and continue with the original brep
        }

        if (!result.IsSolid)
        {
            try
            {
                var capped = result.CapPlanarHoles(Math.Max(tol * 10.0, tol));
                if (capped != null && capped.IsValid)
                    result = capped;
            }
            catch
            {
                // ignore and continue with the current brep
            }
        }

        if (!result.IsSolid)
        {
            try
            {
                var capped = result.CapPlanarHoles(Math.Max(tol * 100.0, tol));
                if (capped != null && capped.IsValid)
                    result = capped;
            }
            catch
            {
                // ignore and continue with the current brep
            }
        }

        try
        {
            result.Standardize();
            result.MergeCoplanarFaces(tol);
            result.Compact();
        }
        catch
        {
            // ignore cleanup failures
        }

        if (!result.IsSolid)
        {
            try
            {
                var union = Brep.CreateBooleanUnion(new[] { result }, tol);
                if (union != null && union.Length > 0 && union[0] != null && union[0].IsValid)
                    result = union[0];
            }
            catch
            {
                // ignore
            }
        }

        return result.IsValid ? result : null;
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

    private static bool MatchesDesiredCut(Extents actual, Extents desired, double tol)
    {
        if (!Within(actual.X, desired.X, tol * 2.0))
            return false;

        if (Math.Abs(actual.Y.Length - desired.Y.Length) > tol * 4.0)
            return false;

        if (Math.Abs(actual.Z.Length - desired.Z.Length) > tol * 4.0)
            return false;

        return true;
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
