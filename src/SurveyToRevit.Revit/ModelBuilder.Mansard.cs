using Autodesk.Revit.DB;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>
/// A mansard's slopes: along every facade stretch the reading calls a Mansard (from a section's profile, or carried round
/// from one), a native footprint roof strip from the eaves line at the eaves height, back to the kerb line, its outer edge
/// sloping up at the surveyed pitch to the kerb's height. The plan's thin cut lines through the slope are already left out
/// of the walls; dormer fronts (walls with windows) stay. Tagged MANSARD; replaced by every build.
/// </summary>
public sealed partial class ModelBuilder
{
    const string MansardCode = "MANSARD";

    /// <summary>
    /// The mansard as one footprint roof on the outline of the floor below (the facade line): each edge along a mansard
    /// stretch slopes at that stretch's pitch, short edges between two sloping ones (the chords of a curved corner) at
    /// theirs, the rest do not slope; cut off at the kerb, where the flat roof takes over. Null when Revit refuses it
    /// (the caller then builds a strip per stretch).
    /// </summary>
    string? MansardRing(List<FacadeTopCall> calls, List<Level> levels, RoofType type)
    {
        var (_, below) = FacadeTops.TopStoreys(_model.Levels);
        if (below?.Outline is not Polygon2 outline) return null;
        var sure = calls.Where(c => c.Confidence == "High").DefaultIfEmpty(calls[0]).ToList();
        double Median(IEnumerable<double> xs) { var s = xs.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        double eavesZ = sure.Min(c => c.Eaves![1]), kerbZ = Median(sure.Select(c => c.Kerb![1]));
        using var sub = new SubTransaction(_doc);
        sub.Start();
        try
        {
            double baseZ = Z(eavesZ);
            var level = levels.Where(l => l.Elevation <= baseZ + Ft(10)).OrderByDescending(l => l.Elevation).FirstOrDefault() ?? levels.OrderBy(l => l.Elevation).First();
            var loop = Loop(outline, level.Elevation) ?? throw new InvalidOperationException("outline too small");
            var curves = new CurveArray();
            foreach (var cv in loop) curves.Append(cv);
            var footprint = new ModelCurveArray();
            var roof = _doc.Create.NewFootPrintRoof(curves, level, type, out footprint);
            roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(baseZ - level.Elevation);

            // each edge: the mansard stretch it lies on (midpoint within 300 mm of the stretch, running along it)
            var edges = footprint.Cast<ModelCurve>().Select(mc =>
            {
                var a = mc.GeometryCurve.GetEndPoint(0); var b = mc.GeometryCurve.GetEndPoint(1);
                var seg = new Segment2(new Vec2(Mm(a.X), Mm(a.Y)), new Vec2(Mm(b.X), Mm(b.Y)));
                var call = calls.FirstOrDefault(c =>
                {
                    var s = new Segment2(new Vec2(c.Line[0], c.Line[1]), new Vec2(c.Line[2], c.Line[3]));
                    double along = (seg.Mid - s.A).Dot(s.Direction);
                    return along >= -300 && along <= s.Length + 300 && Math.Abs((seg.Mid - s.A).Cross(s.Direction)) <= 300
                           && Math.Abs(seg.Direction.Dot(s.Direction)) > 0.9;
                });
                double? slope = call == null ? null : (call.Kerb![1] - call.Eaves![1]) / Math.Max(100, call.Eaves[0] - call.Kerb[0]);
                return (mc, seg, slope);
            }).ToList();
            // short edges between two sloping ones: the chords of a curve, or a small step in the facade
            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].slope != null || edges[i].seg.Length > FacadeTops.MinStretchMm) continue;
                var touching = edges.Where((e, j) => j != i && e.slope != null && (e.seg.A.DistanceTo(edges[i].seg.A) < 20 || e.seg.A.DistanceTo(edges[i].seg.B) < 20
                                                     || e.seg.B.DistanceTo(edges[i].seg.A) < 20 || e.seg.B.DistanceTo(edges[i].seg.B) < 20)).ToList();
                if (touching.Count == 2) edges[i] = (edges[i].mc, edges[i].seg, touching.Average(e => e.slope!.Value));
            }
            int sloped = 0;
            foreach (var (mc, _, slope) in edges)
            {
                roof.set_DefinesSlope(mc, slope != null);
                if (slope != null) { roof.set_SlopeAngle(mc, slope.Value); sloped++; }
            }
            // cut off at the kerb: the flat roof over the top storey takes over from there
            roof.get_Parameter(BuiltInParameter.ROOF_UPTO_LEVEL_PARAM)?.Set(level.Id);
            roof.get_Parameter(BuiltInParameter.ROOF_UPTO_LEVEL_OFFSET_PARAM)?.Set(Z(kerbZ) - level.Elevation);
            _doc.Regenerate();
            Mark(roof, MansardCode, $"Mansard round {below.Name}: {sloped} of {edges.Count} edges sloping, from {eavesZ / 1000:0.00} m cut off at {kerbZ / 1000:0.00} m");
            sub.Commit();
            return $"Mansard: one roof round {below.Name}'s outline, {sloped} of {edges.Count} edges sloping (the stretches' pitches), from {eavesZ / 1000:0.00} m, cut off at the kerb {kerbZ / 1000:0.00} m.";
        }
        catch (Exception ex)
        {
            sub.RollBack();
            Log.Add($"Mansard: one roof round the floor refused ({ex.Message}); built as strips instead.");
            return null;
        }
    }

    /// <summary>
    /// The height a flat roof over the top storey is built at when a section shows the mansard round it reaching higher:
    /// the mansard's kerb, when the roof plan's own spot heights reach it too (the roof's height was taken low in its
    /// range); otherwise the roof's own.
    /// </summary>
    double RoofTopMm(RoofCandidate r)
    {
        if (r.EavesMm != null || r.Heights is not { Count: > 0 } hs) return r.ElevationMm;
        var shown = _model.FacadeTops.Where(c => c.Class == "Mansard" && c.Confidence == "High" && c.Kerb is { Length: 2 }).ToList();
        if (shown.Count == 0) return r.ElevationMm;
        var topStorey = _model.Levels.FirstOrDefault(l => l.Name == shown[0].Level)?.Outline;
        if (topStorey == null) return r.ElevationMm;
        var mid = new Vec2(r.Outline.Vertices.Average(v => v.X), r.Outline.Vertices.Average(v => v.Y));
        if (!FacadeWalls.Contains(topStorey, mid)) return r.ElevationMm;
        double kerb = shown.Max(c => c.Kerb![1]);
        return r.ElevationMm < kerb - 500 && hs.Max(h => h.Mm) >= kerb ? kerb : r.ElevationMm;
    }

    void BuildMansards(List<Level> levels)
    {
        var calls = _model.FacadeTops.Where(c => c.Class == "Mansard" && c.Confidence != "Low" && c.Eaves is { Length: 2 }
                                                 && c.Kerb is { Length: 2 } && c.Inward is { Length: 2 } && c.SlopeDeg is > 30 and < 89 && c.Line.Length == 4).ToList();
        using var t = new Transaction(_doc, "Survey mansard");
        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
        t.Start();
        DeleteGenerated(MansardCode);
        if (calls.Count == 0) { t.Commit(); return; }
        // thin: on a 70° slope a 300 mm roof is 0.9 m thick upright, and its top edge stands up like a board
        var type = RoofTypeFor(100);
        if (type == null) { t.RollBack(); Log.Add("Mansard: the project has no roof type."); return; }
        // one roof round the whole floor: clean hips at every corner, curved ones included; strips only if Revit refuses it
        if (MansardRing(calls, levels, type) is string ring) { t.Commit(); Log.Add(ring); return; }
        int made = 0; string? firstError = null;
        foreach (var c in calls)
        {
            try
            {
                var a = new Vec2(c.Line[0], c.Line[1]); var b = new Vec2(c.Line[2], c.Line[3]);
                var n = new Vec2(c.Inward![0], c.Inward[1]);
                double eavesOut = c.Eaves![0], eavesZ = c.Eaves[1], kerbOut = c.Kerb![0], kerbZ = c.Kerb[1];
                double depth = eavesOut - kerbOut;
                if (depth < 100 || kerbZ - eavesZ < 500) continue;
                // out is measured in front of the facade line: the eaves line sits eavesOut outward, the kerb line kerbOut
                Vec2 Off(Vec2 p, double outMm) => p - n * outMm;
                var poly = new Polygon2(new List<Vec2> { Off(a, eavesOut), Off(b, eavesOut), Off(b, kerbOut), Off(a, kerbOut) });
                double baseZ = Z(eavesZ);
                var level = levels.Where(l => l.Elevation <= baseZ + Ft(10)).OrderByDescending(l => l.Elevation).FirstOrDefault() ?? levels.OrderBy(l => l.Elevation).First();
                var loop = Loop(poly, level.Elevation) ?? throw new InvalidOperationException("strip too small");
                var curves = new CurveArray();
                foreach (var cv in loop) curves.Append(cv);
                var footprint = new ModelCurveArray();
                var roof = _doc.Create.NewFootPrintRoof(curves, level, type, out footprint);
                roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(baseZ - level.Elevation);
                // only the eaves edge slopes: up to the kerb across the strip's depth
                var eavesMid = (Off(a, eavesOut) + Off(b, eavesOut)) * 0.5;
                double slope = (kerbZ - eavesZ) / depth;
                foreach (ModelCurve mc in footprint)
                {
                    var m = mc.GeometryCurve.Evaluate(0.5, true);
                    bool isEaves = new Vec2(Mm(m.X), Mm(m.Y)).DistanceTo(eavesMid) < 50;
                    roof.set_DefinesSlope(mc, isEaves);
                    if (isEaves) roof.set_SlopeAngle(mc, slope);
                }
                Mark(roof, MansardCode, $"Mansard {c.Id}: {c.SlopeDeg:0.#}° from {eavesZ / 1000:0.00} to {kerbZ / 1000:0.00} m ({c.Confidence})");
                made++;
            }
            catch (Exception ex) { firstError ??= ex.Message; }
        }
        t.Commit();
        Log.Add($"Mansard: {made} of {calls.Count} sloped strips built (native roofs, tagged MANSARD)" + (firstError != null ? $"; refused: {firstError}" : "") + ".");
    }
}
