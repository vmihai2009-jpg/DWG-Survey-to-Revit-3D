using Autodesk.Revit.DB;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>
/// Checks of the model as built (item Q of MCP-AUDIT-AND-TOOLS.md), for every user, with or without the MCP side:
/// roofs cutting walls and slabs, detail floating in front of nothing, duplicate walls, walls off their levels, elements
/// in another phase. Read-only; each finding has element ids and the fix. A check that fails is skipped, never a reason
/// to lose the build.
/// </summary>
internal static class BuiltModelChecks
{
    static double Mm(double ft) => UnitConversion.FeetToMm(ft);
    static double Ft(double mm) => UnitConversion.MmToFeet(mm);

    static double[] At(Element e) => e.get_BoundingBox(null) is BoundingBoxXYZ b
        ? new[] { Math.Round(Mm((b.Min.X + b.Max.X) / 2)), Math.Round(Mm((b.Min.Y + b.Max.Y) / 2)), Math.Round(Mm(b.Min.Z)) } : Array.Empty<double>();

    public static List<BuiltFinding> Run(Document doc, Phase? surveyPhase, List<string>? problems = null)
    {
        var found = new List<BuiltFinding>();
        var gen = SurveyPipeline.Generated(doc).Select(g => g.Element).ToList();
        var walls = gen.OfType<Wall>().ToList();
        var floors = gen.OfType<Floor>().ToList();
        var roofs = gen.OfType<RoofBase>().ToList();
        void Try(string what, Action a) { try { a(); } catch (Exception ex) { problems?.Add($"{what}: {ex.Message}"); } }

        // roofs through walls and slabs (item D will stop walls under roofs; until then, say where)
        Try("roofs", () =>
        {
            foreach (var r in roofs)
            {
                var hit = new ElementIntersectsElementFilter(r);
                foreach (var w in walls.Where(w => hit.PassesFilter(w)))
                    found.Add(new("roof-through-wall", SurveyPipeline.LevelName(doc, w), new[] { r.Id.Value, w.Id.Value }, At(w),
                        $"roof {r.Id.Value} cuts wall {w.Id.Value}", "attach the wall's top to the roof (Attach Top/Base), or lower it to the roof's underside", Math.Round(Mm(w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0) / 1000, 1), "m of wall"));
                foreach (var f in floors.Where(f => hit.PassesFilter(f)))
                    found.Add(new("slab-through-roof", SurveyPipeline.LevelName(doc, f), new[] { f.Id.Value, r.Id.Value }, At(f),
                        $"slab {f.Id.Value} pushes through roof {r.Id.Value}", "trim the slab's edge to the roof (edit boundary) or check the roof's height", 10, "slab"));
            }
        });

        // detail (generic models, columns) with nothing behind it: its box grown by 50 mm touches no wall, floor or roof
        Try("detail", () =>
        {
            var hosts = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs }))
                .WhereElementIsNotElementType().ToElementIds();
            foreach (var e in gen.Where(e => e.Category?.Id.Value == (long)BuiltInCategory.OST_GenericModel || e.Category?.Id.Value == (long)BuiltInCategory.OST_Columns))
            {
                if (e.get_BoundingBox(null) is not BoundingBoxXYZ b || hosts.Count == 0) continue;
                var grow = Ft(50);
                var outline = new Outline(new XYZ(b.Min.X - grow, b.Min.Y - grow, b.Min.Z - grow), new XYZ(b.Max.X + grow, b.Max.Y + grow, b.Max.Z + grow));
                bool touches = new FilteredElementCollector(doc, hosts).WherePasses(new BoundingBoxIntersectsFilter(outline)).Any(h => h.Id != e.Id);
                if (!touches)
                    found.Add(new("floating", SurveyPipeline.LevelName(doc, e), new[] { e.Id.Value }, At(e), $"{e.Name} {e.Id.Value} reaches no wall, floor or roof within 50 mm",
                        "move it back onto its wall, or delete it if nothing is drawn behind it", 4, "element"));
            }
        });

        // duplicate walls, by level
        Try("duplicates", () =>
        {
            foreach (var g in walls.Where(w => w.Location is LocationCurve { Curve: Line }).GroupBy(w => w.LevelId.Value))
            {
                var lines = g.Select(w => { var c = ((LocationCurve)w.Location).Curve; var a = c.GetEndPoint(0); var z = c.GetEndPoint(1);
                    return (w.Id.Value, new Segment2(new Vec2(Mm(a.X), Mm(a.Y)), new Vec2(Mm(z.X), Mm(z.Y)))); }).ToList();
                foreach (var (a, bId, over) in BuiltChecks.DuplicateWalls(lines))
                {
                    var w = doc.GetElement(new ElementId(a));
                    found.Add(new("duplicate-wall", SurveyPipeline.LevelName(doc, w), new[] { a, bId }, At(w), $"walls {a} and {bId} lie on one line, overlapping {over / 1000:0.0} m",
                        "delete one of them (keep the one that hosts the windows)", Math.Round(over / 1000, 1), "m of wall"));
                }
            }
        });

        // walls off their levels: base or top offset beyond 50 mm (low walls and walls running past a mezzanine are meant to)
        Try("levels", () =>
        {
            foreach (var w in walls)
            {
                var note = w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                // parapets, and the buildings next door (context: their own heights, not this building's levels)
                if (note.Contains("Parapet") || note.Contains("|CONTEXT|")) continue;
                double baseOff = Mm(w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
                bool topLevel = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() is ElementId t && t != ElementId.InvalidElementId;
                double topOff = topLevel ? Mm(w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0) : 0;
                if (Math.Abs(baseOff) > BuiltChecks.LevelOffMm || Math.Abs(topOff) > BuiltChecks.LevelOffMm)
                    found.Add(new("wall-off-level", SurveyPipeline.LevelName(doc, w), new[] { w.Id.Value }, At(w), $"wall {w.Id.Value}: base {baseOff:0} mm, top {topOff:0} mm off its levels",
                        "check the floor levels (a level off the survey moves every wall on it)", 2, "wall"));
            }
        });

        // what the engine made, and the railings Revit adds by itself, in the survey phase (the user's own new work is left alone)
        Try("phases", () =>
        {
            if (surveyPhase == null) return;
            var railings = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StairsRailing).WhereElementIsNotElementType().ToElements();
            var later = gen.Concat(railings).GroupBy(e => e.Id.Value).Select(g => g.First())
                .Where(e => e.Category != null && e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() is ElementId p && p != ElementId.InvalidElementId && p != surveyPhase.Id)
                .ToList();
            if (later.Count > 0)
                found.Add(new("wrong-phase", "", later.Take(200).Select(e => e.Id.Value).ToList(), Array.Empty<double>(),
                    $"{later.Count} model elements are not in the '{surveyPhase.Name}' phase ({string.Join(", ", later.GroupBy(e => e.Category!.Name).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Count()} {g.Key}"))})",
                    $"set Phase Created to '{surveyPhase.Name}' (select them by id)", later.Count, "elements"));
        });
        return found;
    }
}
