using System.Text.Json.Nodes;

namespace SurveyToRevit.Core.Survey;

/// <summary>
/// The engine's whole reading of a survey, for another program to measure a model against (the Barr Gazetas MCP
/// hub's 1:1 score and autofix, through the survey_reading tool). Unlike <see cref="SurveySummary"/> this is the
/// geometry itself, so it is written to a file and never handed to an assistant as an answer.
///
/// Contract (schema "survey-reading/1"; add fields freely, never rename or remove one, bump the number if a
/// meaning changes):
///   x, y            model mm: Revit internal X and Y of a project Build from Survey set up (internal origin = the
///                   survey base point, turned to project north). frame says how to get back to survey grid:
///                   survey = base + rotate(+rotationDeg) * model.
///   heights         mm above survey datum (AOD) for levels, spot heights, roofs and sections; mm above the floor
///                   for sills, heads, ceilings and wall heights (heights[] notes, windows, doors).
///   lines           [x1, y1, x2, y2]. Arcs [cx, cy, r, startDeg, endDeg], counter-clockwise. Outlines [[x, y], ...].
///   wallFaces       the plan's lines on wall layers exactly as drawn (not straightened onto the building's axes,
///                   which the walls are): the survey's faces, to measure against.
///   walls           the walls the engine read (centre line, thickness): what a build makes, before any fix.
/// The layer roles come from the built-in rules plus any layers.txt next to the drawings, so a survey drawn
/// to another office's standard reads the same way here as it does for the build.
/// </summary>
public static class SurveyReading
{
    public const string Schema = "survey-reading/1";

    static JsonNode? N(double v, int digits = 1) => double.IsNaN(v) || double.IsInfinity(v) ? null : Math.Round(v, digits);
    static JsonArray Line(Segment2 s) => new(N(s.A.X), N(s.A.Y), N(s.B.X), N(s.B.Y));
    static JsonArray Point(Vec2 p) => new(N(p.X), N(p.Y));
    static JsonArray Arc(Arc2 a) => new(N(a.Center.X), N(a.Center.Y), N(a.Radius), N(a.Start * 180 / Math.PI, 3), N(a.End * 180 / Math.PI, 3));
    static JsonArray Outline(Polygon2 p) => new(p.Vertices.Select(v => (JsonNode)Point(v)).ToArray());
    static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonNode?> f) => new(items.Select(f).ToArray());

    /// <summary>The reading as JSON. <paramref name="only"/>: floor codes or names to include (default all).</summary>
    public static JsonObject ToJson(SurveyModel model, IReadOnlyCollection<string>? only = null, IEnumerable<string>? files = null)
    {
        var frame = model.Frame;
        var rules = model.Rules;
        bool Wanted(SurveyLevel l) => only == null || only.Count == 0 ||
            only.Any(o => o.Equals(l.Code, StringComparison.OrdinalIgnoreCase) || o.Equals(l.Name, StringComparison.OrdinalIgnoreCase));

        var levels = new JsonArray();
        foreach (var l in model.Levels.Where(Wanted))
        {
            var plan = l.Plan;
            var texts = plan.Texts.Select(t => (P: frame.ToModel(t.Position), t.Value, t.Layer)).ToList();
            var walls = l.Walls.Walls;
            var o = new JsonObject
            {
                ["code"] = l.Code,
                ["name"] = l.Name,
                ["order"] = N(l.Order, 3),
                ["elevationMm"] = N(l.ElevationMm),
                ["levelFromSpotHeights"] = l.Estimate == null ? null : new JsonObject
                {
                    ["floorLevelMm"] = N(l.Estimate.FloorLevelMm), ["supporting"] = l.Estimate.Supporting, ["total"] = l.Estimate.Total,
                },
                ["wallHeightMm"] = N(l.WallHeightMm),
                ["ceilingMm"] = N(l.CeilingHeightMm),
                ["ceilingFromSurvey"] = l.CeilingFromSurvey,
                ["mezzanine"] = l.Partial,
                ["include"] = l.Include,
                ["plan"] = plan.Path,
                ["drawing"] = plan.Name.FileName,
                ["wallFaces"] = Array(plan.WallLines.Select(frame.ToModel), s => Line(s)),
                ["wallFaceArcs"] = Array(plan.WallArcs.Select(frame.ToModel), a => Arc(a)),
                ["windowLines"] = Array(plan.OpeningLines.Where(s => rules.Classify(s.Layer) == LayerRole.Window).Select(frame.ToModel), s => Line(s)),
                ["doorLines"] = Array(plan.OpeningLines.Where(s => rules.Classify(s.Layer) == LayerRole.Door).Select(frame.ToModel), s => Line(s)),
                ["walls"] = Array(walls, w =>
                {
                    var wo = new JsonObject
                    {
                        ["line"] = Line(w.Centerline), ["thicknessMm"] = N(w.MeasuredThickness), ["typeMm"] = N(w.TypeThickness),
                        ["confidence"] = w.Confidence.ToString(), ["layer"] = w.Centerline.Layer,
                    };
                    if (w.Arc != null) wo["arc"] = Arc(w.Arc);
                    if (w.HeightMm is double h) wo["heightMm"] = N(h);
                    if (w.Curtain) wo["curtain"] = true;
                    if (w.PastPartialFloor) wo["pastMezzanine"] = true;
                    return wo;
                }),
                ["windows"] = Array(l.Windows, w => new JsonObject
                {
                    ["x"] = N(w.Center.X), ["y"] = N(w.Center.Y), ["widthMm"] = N(w.WidthMm), ["sillMm"] = N(w.SillMm), ["headMm"] = N(w.HeadMm),
                    ["heightsFromSurvey"] = w.HeightsFromSurvey, ["wall"] = w.HostIndex,
                }),
                ["doors"] = Array(l.Doors, d => new JsonObject
                {
                    ["x"] = N(d.Center.X), ["y"] = N(d.Center.Y), ["widthMm"] = N(d.WidthMm), ["heightMm"] = N(d.HeightMm),
                    ["double"] = d.Double, ["heightFromSurvey"] = d.HeightFromSurvey, ["wall"] = d.HostIndex,
                }),
                // every sill, head, door head and ceiling note on the plan, as the build reads them
                ["heights"] = Array(SurveyHeights.Read(texts.Select(t => (t.P, t.Value))), h => new JsonObject
                {
                    ["x"] = N(h.Position.X), ["y"] = N(h.Position.Y), ["kind"] = h.Kind.ToString(), ["mm"] = N(h.Mm),
                }),
                ["spotHeights"] = Array(texts.Where(t => rules.Classify(t.Layer) == LayerRole.LevelText)
                    .Select(t => (t.P, t.Value, Mm: LevelEstimator.ParseMm(t.Value))).Where(t => t.Mm != null), t => new JsonObject
                {
                    ["x"] = N(t.P.X), ["y"] = N(t.P.Y), ["mm"] = N(t.Mm!.Value), ["text"] = t.Value,
                }),
                ["rooms"] = Array(l.RoomNames, r => new JsonObject { ["x"] = N(r.Position.X), ["y"] = N(r.Position.Y), ["name"] = r.Name }),
                ["ceilingNotes"] = Array(l.CeilingNotes, c => new JsonObject { ["x"] = N(c.Position.X), ["y"] = N(c.Position.Y), ["mm"] = N(c.Mm) }),
                ["outline"] = l.Outline == null ? null : Outline(l.Outline),
                ["ceilingOutline"] = l.CeilingOutline == null ? null : Outline(l.CeilingOutline),
                ["stairs"] = Array(l.Stairs, s => new JsonObject
                {
                    ["risers"] = s.Risers,
                    ["directionKnown"] = s.DirectionKnown,
                    ["footprint"] = Outline(s.Footprint()),
                    ["flights"] = Array(s.Flights, f => new JsonObject
                    {
                        ["risers"] = f.Risers.Count, ["widthMm"] = N(f.Width), ["goingMm"] = N(f.Going), ["curved"] = f.Curved,
                        ["bottom"] = Point(f.Bottom), ["top"] = Point(f.Risers[^1].Mid),
                    }),
                }),
                ["stairVoids"] = Array(l.StairVoids, v => Outline(v)),
            };
            levels.Add(o);
        }

        return new JsonObject
        {
            ["schema"] = Schema,
            ["made"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["files"] = files == null ? null : Array(files, f => f),
            ["frame"] = new JsonObject
            {
                ["baseEastMm"] = N(frame.BasePoint.X), ["baseNorthMm"] = N(frame.BasePoint.Y), ["rotationDeg"] = N(frame.RotationDeg, 6),
            },
            ["units"] = "x, y: model mm (Revit internal X and Y of a project Build from Survey set up); levels, spot heights, roofs, sections: mm above survey datum; sills, heads, ceilings, wall heights: mm above the floor.",
            ["layerFile"] = rules.OverrideFile,
            ["levels"] = levels,
            ["sections"] = Array(model.Sections, s => new JsonObject
            {
                ["name"] = s.Name, ["cut"] = s.Cut == null ? null : Line(s.Cut), ["heightsMm"] = Array(s.HeightsMm, h => N(h)),
                ["file"] = s.File, ["note"] = s.Note,
            }),
            ["roofs"] = Array(model.Roofs, r => new JsonObject { ["outline"] = Outline(r.Outline), ["elevationMm"] = N(r.ElevationMm), ["note"] = r.Note }),
            ["skipped"] = Array(model.Skipped, s => new JsonObject { ["file"] = s.FileName, ["reason"] = s.Reason }),
            ["notes"] = Array(model.Notes, n => n),
        };
    }

    /// <summary>Each layer's role under the rules (built-in plus any layers.txt), for a program reading drawings itself.</summary>
    public static JsonObject Roles(LayerRules rules, IEnumerable<string> layers)
    {
        var o = new JsonObject();
        foreach (var name in layers.Distinct(StringComparer.Ordinal)) o[name] = rules.Classify(name).ToString();
        return o;
    }
}
