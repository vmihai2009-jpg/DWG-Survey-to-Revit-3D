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

    void BuildMansards(List<Level> levels)
    {
        var calls = _model.FacadeTops.Where(c => c.Class == "Mansard" && c.Confidence != "Low" && c.Eaves is { Length: 2 }
                                                 && c.Kerb is { Length: 2 } && c.Inward is { Length: 2 } && c.SlopeDeg is > 30 and < 89 && c.Line.Length == 4).ToList();
        using var t = new Transaction(_doc, "Survey mansard");
        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
        t.Start();
        DeleteGenerated(MansardCode);
        if (calls.Count == 0) { t.Commit(); return; }
        var type = RoofTypeFor(300);
        if (type == null) { t.RollBack(); Log.Add("Mansard: the project has no roof type."); return; }
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
