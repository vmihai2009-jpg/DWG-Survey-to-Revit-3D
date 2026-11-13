namespace SurveyToRevit.Core.Survey;

/// <summary>
/// How survey coordinates map to the model. The model's origin sits at
/// <see cref="BasePoint"/> (survey grid, mm) and the model is turned by
/// <see cref="RotationDeg"/> so the building's main walls run square to the
/// screen ("project north"). Grid north of the survey becomes true north.
/// Heights stay as surveyed (e.g. metres above Ordnance Datum).
/// </summary>
public sealed record SurveyFrame(Vec2 BasePoint, double RotationDeg)
{
    public Vec2 ToModel(Vec2 survey)
    {
        var d = survey - BasePoint;
        var r = -RotationDeg * Math.PI / 180;
        return new Vec2(d.X * Math.Cos(r) - d.Y * Math.Sin(r), d.X * Math.Sin(r) + d.Y * Math.Cos(r));
    }

    public Segment2 ToModel(Segment2 s) => s with { A = ToModel(s.A), B = ToModel(s.B) };

    /// <summary>Back from the model to survey grid coordinates (mm).</summary>
    public Vec2 ToSurvey(Vec2 model)
    {
        var r = RotationDeg * Math.PI / 180;
        return BasePoint + new Vec2(model.X * Math.Cos(r) - model.Y * Math.Sin(r), model.X * Math.Sin(r) + model.Y * Math.Cos(r));
    }

    public Arc2 ToModel(Arc2 a)
    {
        double r = -RotationDeg * Math.PI / 180;
        return a with { Center = ToModel(a.Center), Start = a.Start + r, End = a.End + r };
    }
}

/// <summary>One floor. Walls, outline and unclear regions are in model coordinates (mm).</summary>
public sealed record SurveyLevel(
    string Code, string Name, double Order, double ElevationMm, LevelEstimate? Estimate,
    SurveyDrawing Plan, WallDetectionResult Walls, Polygon2? Outline, IReadOnlyList<UnclearRegion> Unclear)
{
    /// <summary>Floor level above survey datum (mm). Editable before building.</summary>
    public double ElevationMm { get; set; } = ElevationMm;
    /// <summary>The floor's outline (slab edge); Interior Survey trims it to the existing façade's inner face.</summary>
    public Polygon2? Outline { get; set; } = Outline;
    /// <summary>Further floor parts with slabs and ceilings of their own (Interior Survey: a floor the existing model splits in two).</summary>
    public IReadOnlyList<Polygon2> MoreOutlines { get; set; } = Array.Empty<Polygon2>();
    /// <summary>Walls (walls made to hold doors in empty openings are added after the doors are read).</summary>
    public WallDetectionResult Walls { get; set; } = Walls;
    /// <summary>Wall height: up to the next level, or the default for the top level.</summary>
    public double WallHeightMm { get; set; }
    /// <summary>A mezzanine: covers only part of the floor below, whose walls elsewhere run up past it.</summary>
    public bool Partial { get; set; }
    /// <summary>Ceiling height above this floor (mm): the surveyed one when the plan gives it ("u/s 2.52"), else a default to adjust.</summary>
    public double CeilingHeightMm { get; set; }
    /// <summary>Where the ceiling goes, when not the whole floor (under a mezzanine only: the rest is double height).</summary>
    public Polygon2? CeilingOutline { get; set; }
    /// <summary>True when <see cref="CeilingHeightMm"/> comes from ceiling heights written on the plan.</summary>
    public bool CeilingFromSurvey { get; set; }
    /// <summary>Each ceiling height written on the plan (in a room), for ceilings room by room.</summary>
    public IReadOnlyList<(Vec2 Position, double Mm)> CeilingNotes { get; set; } = Array.Empty<(Vec2, double)>();
    /// <summary>Wall material no wall covers, as lists of 25 mm cell centres (filled as generic models so nothing is left open).</summary>
    public IReadOnlyList<List<Vec2>> Leftover { get; set; } = Array.Empty<List<Vec2>>();
    /// <summary>Columns, chimneys, fittings, balconies, rooflights and railings, as generic model blocks.</summary>
    public IReadOnlyList<DetailShape> Details { get; set; } = Array.Empty<DetailShape>();
    /// <summary>Stairs found on the stair layers, climbing from this floor.</summary>
    public IReadOnlyList<StairCandidate> Stairs { get; set; } = Array.Empty<StairCandidate>();
    /// <summary>Openings this floor's slab needs for the stairs coming up from the floor below.</summary>
    public IReadOnlyList<Polygon2> StairVoids { get; set; } = Array.Empty<Polygon2>();
    /// <summary>Is this point (model mm) in a room the drawing encloses?</summary>
    public Func<Vec2, bool>? IsRoom { get; set; }
    /// <summary>Is this point drawn wall material (the plan's grid reading)?</summary>
    public Func<Vec2, bool>? IsMaterial { get; set; }
    /// <summary>Windows found on this floor (hosted in <see cref="Walls"/>).</summary>
    public IReadOnlyList<WindowCandidate> Windows { get; set; } = Array.Empty<WindowCandidate>();
    /// <summary>Doors found on this floor (hosted in <see cref="Walls"/>).</summary>
    public IReadOnlyList<DoorCandidate> Doors { get; set; } = Array.Empty<DoorCandidate>();
    /// <summary>Room names from the survey, at their label positions (model mm).</summary>
    public IReadOnlyList<(Vec2 Position, string Name)> RoomNames { get; set; } = Array.Empty<(Vec2, string)>();
    /// <summary>Build this floor? (Editable before building.)</summary>
    public bool Include { get; set; } = true;
}

public sealed record SkippedDrawing(string FileName, string Reason);

public sealed class SurveyModel
{
    public required SurveyFrame Frame { get; init; }
    public required IReadOnlyList<SurveyLevel> Levels { get; init; }
    public required IReadOnlyList<SkippedDrawing> Skipped { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>Flat concept roofs over the parts of each floor not covered by the floor above.</summary>
    public IReadOnlyList<RoofCandidate> Roofs { get; init; } = Array.Empty<RoofCandidate>();
    /// <summary>The buildings next door: the roof-plan areas over them (outline and roof height), built as context.</summary>
    public IReadOnlyList<RoofCandidate> Context { get; init; } = Array.Empty<RoofCandidate>();
    /// <summary>The neighbours' fronts as the elevations draw them (skyline steps and windows), built as context.</summary>
    public IReadOnlyList<ContextFront> ContextFronts { get; init; } = Array.Empty<ContextFront>();
    /// <summary>The context as built: fronts, block walls (none along this building's own outline) and roofs; null: build the roof-plan blocks as they are.</summary>
    public ContextPlan? ContextPlan { get; init; }
    /// <summary>The layer rules used (built-in, plus any layers.txt).</summary>
    public LayerRules Rules { get; init; } = LayerRules.Default;
    /// <summary>Survey sections read from section sheets (heights, and cut lines for section views).</summary>
    public IReadOnlyList<SurveySection> Sections { get; init; } = Array.Empty<SurveySection>();
    /// <summary>What each section says (item H): height fit, registration, profiles; written to section-index.json.</summary>
    public IReadOnlyList<SectionIndexEntry> SectionIndex { get; init; } = Array.Empty<SectionIndexEntry>();
    /// <summary>Elevation drawings (item E): facade, mapping, height fits; written to elevation-index.json.</summary>
    public IReadOnlyList<ElevationDrawing> Elevations { get; init; } = Array.Empty<ElevationDrawing>();
    /// <summary>Places the self-checks found worth a look (see <see cref="ModelChecks"/>), most important first.</summary>
    public IReadOnlyList<CheckItem> Checks { get; init; } = Array.Empty<CheckItem>();
    /// <summary>What happened to each entry of survey-decisions.json (applied, stale, contradicted, waiting); empty without the file.</summary>
    public IReadOnlyList<DecisionOutcome> Decisions { get; init; } = Array.Empty<DecisionOutcome>();
    /// <summary>What the top of each facade stretch is (item C), for roof-edges.json.</summary>
    public IReadOnlyList<FacadeTopCall> FacadeTops { get; init; } = Array.Empty<FacadeTopCall>();
    /// <summary>The decisions file read, or null.</summary>
    public SurveyDecisions? DecisionsFile { get; init; }
}

/// <summary>
/// Turns a set of survey DWGs into levels, walls and a coordinate frame,
/// without Revit. One floor plan per level is used; duplicates (e.g. a fire
/// plan of the same floor) are skipped and reported.
/// </summary>
public static class SurveyAnalyzer
{
    /// <summary>Height of a balustrade or parapet in front of windows, where the windows' sill does not give one.</summary>
    public const double ParapetMm = 1100;
    /// <summary>A floor named a mezzanine and smaller than this share of the one below covers only part of it.</summary>
    public const double MezzanineShare = 0.6;

    static readonly System.Text.RegularExpressions.Regex MezzanineName = new(@"mezz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The outline of the mezzanine over floor <paramref name="i"/>, or null. Only a floor the survey names a
    /// mezzanine counts (surveyors always do): a smaller floor that is not named one is a setback or a part
    /// floor at full storey height (Mount Row, Parkshot House), not a mezzanine within a storey.
    /// </summary>
    static Polygon2? Mezzanine(IReadOnlyList<SurveyLevel> levels, int i)
    {
        if (i + 2 >= levels.Count || levels[i + 1].Outline is not Polygon2 m || levels[i].Outline is not Polygon2 below) return null;
        if (m.Area >= MezzanineShare * below.Area) return null;
        return MezzanineName.IsMatch(levels[i + 1].Name) ? m : null;
    }

    static double EdgeDistance(Polygon2 poly, Vec2 p)
    {
        var v = poly.Vertices; double best = double.MaxValue;
        for (int i = 0; i < v.Count; i++)
        {
            Vec2 a = v[i], b = v[(i + 1) % v.Count], d = b - a;
            double len2 = d.Dot(d), t = len2 == 0 ? 0 : Math.Clamp((p - a).Dot(d) / len2, 0, 1);
            best = Math.Min(best, p.DistanceTo(a + d * t));
        }
        return best;
    }

    public const double DefaultTopWallHeightMm = 3000;
    /// <summary>The least height between a floor and the floor above (mm).</summary>
    public const double MinStoreyMm = 2000;

    /// <summary>Ceiling 2700 mm above the floor, or lower where the storey is low (at least 600 mm below the floor above).</summary>
    public static double DefaultCeilingHeight(double storeyMm) => Math.Round(Math.Min(2700, Math.Max(2100, storeyMm - 600)) / 10) * 10;

    /// <summary>Development aid: receives each floor's classified grid (level code, width, height, classes).</summary>
    public static Action<string, int, int, byte[]>? PlanDebug { get; set; }

    public static SurveyModel Analyze(IEnumerable<string> paths, Tolerances? tol = null, LayerRules? rules = null, SurveyFrame? frame = null)
    {
        tol ??= Tolerances.Default;
        rules ??= LayerRules.Default;
        var skipped = new List<SkippedDrawing>();
        var notes = new List<string>();
        paths = paths.ToList();
        // the office's own layer names, in a layers.txt next to the drawings
        if (rules.OverrideFile == null && paths.Select(p => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p) ?? "", LayerRules.OverrideFileName)).FirstOrDefault(File.Exists) is string layerFile)
        {
            rules = rules.WithOverrides(layerFile, notes);
            notes.Add($"Layer names from {layerFile} ({rules.Overrides.Count} rules) are used before the built-in ones.");
        }
        // calls made outside the engine (the MCP side, a person) that rebuilds keep: survey-decisions.json beside layers.txt
        var decisions = SurveyDecisions.Find(paths.Select(p => System.IO.Path.GetDirectoryName(p) ?? ""), notes);
        var outcomes = new List<DecisionOutcome>();
        var plans = new List<SurveyDrawing>();
        var roofPlans = new List<SurveyDrawing>();
        var sectionSheets = new List<SurveyDrawing>();
        var elevationSheets = new List<SurveyDrawing>();

        var queue = new List<SurveyDrawing>();
        foreach (var path in paths.OrderBy(p => System.IO.Path.GetFileName(p)))
        {
            SurveyDrawing read;
            try { read = SurveyDrawing.Read(path, rules); }
            catch (Exception ex) { skipped.Add(new(System.IO.Path.GetFileName(path), $"could not be read: {ex.Message}")); continue; }
            // several floor plans on one sheet: each becomes its own drawing, the rest (sections) stays
            if (read.Name.LevelCode == null && !RoofsAndDetails.IsRoofPlan(read, rules) && SheetSplitter.Split(read) is { } split)
            {
                notes.AddRange(split.Notes);
                queue.AddRange(split.Plans);
                if (SectionReader.IsSectionSheet(split.Rest)) sectionSheets.Add(split.Rest);
                if (ElevationIndex.IsElevationSheet(split.Rest)) elevationSheets.Add(split.Rest);
                continue;
            }
            queue.Add(read);
        }
        foreach (var d in queue)
        {

            if (RoofsAndDetails.IsRoofPlan(d, rules)) { roofPlans.Add(d); continue; }
            if (d.Name.LevelCode == null && ElevationIndex.IsElevationSheet(d)) elevationSheets.Add(d); // read for elevation-index.json, never built from
            if (d.Name.LevelCode == null && SectionReader.IsSectionSheet(d)) { sectionSheets.Add(d); continue; }
            if (d.Name.LevelCode == null) { skipped.Add(new(d.Name.FileName, "not a single-floor plan (elevation, section or multi-level sheet)")); continue; }
            if (d.WallLines.Count < 30) { skipped.Add(new(d.Name.FileName, LayerCheck.FewWalls(d, rules))); continue; }
            if (!d.SpotHeightsMm(rules).Any() && DrawingName.DescribeLevel(d.Name.LevelCode).Order == double.MaxValue)
            { skipped.Add(new(d.Name.FileName, "level not recognised and no spot heights")); continue; }
            plans.Add(d);
        }

        // One plan per level: the one with most wall linework (ties: first by name).
        var chosen = new List<SurveyDrawing>();
        foreach (var g in plans.GroupBy(p => p.Name.LevelCode!))
        {
            var best = g.OrderByDescending(p => p.WallLines.Count).First();
            chosen.Add(best);
            foreach (var other in g.Where(p => p != best))
                skipped.Add(new(other.Name.FileName, $"same floor as {best.Name.FileName}; used that one"));
        }

        // a frame given by the caller (Interior Survey: the existing model's own coordinates) is used as it is
        frame ??= MakeFrame(chosen, tol);
        var sections = new List<SurveySection>();
        foreach (var sheet in sectionSheets)
        {
            try { sections.AddRange(SectionReader.Read(sheet, rules, frame, chosen.SelectMany(c => c.WallLines).ToList())); }
            catch (Exception ex) { notes.Add($"{sheet.Name.FileName}: section sheet could not be read ({ex.Message})."); }
        }
        foreach (var s in sections) notes.Add(s.Note);
        var levels = new List<SurveyLevel>();
        int neighbourTotal = 0;
        var windowInput = new Dictionary<SurveyLevel, (List<Segment2> Lines, List<(Vec2, string)> Texts)>(ReferenceEqualityComparer.Instance);
        var doorInput = new Dictionary<SurveyLevel, List<Segment2>>(ReferenceEqualityComparer.Instance);
        foreach (var p in chosen)
        {
            var (order, name) = DrawingName.DescribeLevel(p.Name.LevelCode!);
            // layers and height notes this surveyor writes that the rules don't read
            notes.AddRange(LayerCheck.Plan(p, rules));
            var est = LevelEstimator.Estimate(p.SpotHeightsMm(rules));
            if (est == null) notes.Add($"{name}: no spot heights found; level height must be set by hand.");
            else if (est.Supporting < 0.4 * est.Total)
                notes.Add($"CHECK {name}: split or unclear floor level. Used {est.FloorLevelMm / 1000:0.00} m, but only {est.Supporting} of {est.Total} spot heights agree" +
                          (est.OtherLevels.Count > 0 ? $" (others: {string.Join(", ", est.OtherLevels.Select(o => $"{o.LevelMm / 1000:0.00}"))})" : "") +
                          ". Move the level in Revit if needed; its walls follow.");

            // Into model coordinates, straightened onto the building's directions.
            var lines = p.WallLines.Select(frame.ToModel).ToList();
            // curves drawn as chains of short lines: fitted to arcs before the lines are straightened onto the axes, so they
            // are built as one curved wall (CurvedWalls drops the straight walls along them)
            var chainArcs = ArcChains.Find(lines);
            var axes = AxisSnapper.Square(AxisSnapper.FindAxes(lines.Where(l => l.Length >= tol.MinSegmentLength).ToList(), tol.AngleSnapDeg));
            lines = AxisSnapper.Snap(lines, axes, tol.AngleSnapDeg);
            var openings = AxisSnapper.Snap(p.OpeningLines.Select(frame.ToModel), axes, tol.AngleSnapDeg);
            var markers = p.Texts.Where(t => IsRoomMarker(t, rules)).Select(t => frame.ToModel(t.Position)).ToList();
            var arcs = p.WallArcs.Select(frame.ToModel).ToList();
            arcs.AddRange(chainArcs.Where(c => !arcs.Any(x => x.Center.DistanceTo(c.Center) < 100 && Math.Abs(x.Radius - c.Radius) < 50)));

            var code = p.Name.LevelCode!;
            // The grid reading says where wall material is (robust to gaps in the linework);
            // walls are then the drawn face pairs with wall material between them (exact thickness).
            var raster = new PlanRaster { Debug = PlanDebug == null ? null : (w, h, c) => PlanDebug(code, w, h, c) }.Analyze(lines.Concat(arcs.SelectMany(a => a.Tessellate(100))).Concat(p.SmallWallArcs.Select(frame.ToModel).SelectMany(a => a.Tessellate(40))).ToList(), openings, markers, tol);
            var walls = WallDetector.Detect(lines, tol, openings, markers, raster.IsMaterial);
            var found = WallDetector.FromSingleLines(walls.UnpairedSegments, walls.Walls.ToList(), raster.Outline, tol, rules.FacesOnly.IsMatch, raster.IsMaterial, raster.IsRoom, markers);
            var facade = FacadeWalls.Consolidate(found, raster.Outline, tol);
            facade = WallDetector.CloseGaps(facade, tol, openings);
            facade = CurvedWalls.Add(facade, arcs, tol, raster.IsMaterial, raster.Outline);
            facade = WallDetector.BridgeBends(facade, tol, openings);
            facade = PierWalls.Fill(facade, raster.Leftover, tol);
            facade = WallDetector.CloseFacade(facade, raster.Outline, tol);
            facade = WallFit.Fit(facade, raster.IsMaterial, raster.Outline, tol);
            facade = facade.Where(w => w.Length >= tol.MinWallLength).ToList();
            // the buildings next door drawn on the outline layer: walls from that layer only, away from all of this
            // building's own linework (its other wall layers, doors, stairs)
            var ownLinework = lines.Where(l => !rules.FacesOnly.IsMatch(l.Layer) && rules.Classify(l.Layer) == LayerRole.Wall)
                .Concat(openings.Where(o => rules.Classify(o.Layer) == LayerRole.Door))
                .Concat(p.FeatureLines.Where(f => rules.Classify(f.Layer) == LayerRole.Stair).Select(frame.ToModel))
                .SelectMany(l => new[] { l.A, l.B });
            facade = NeighbourWalls.Remove(facade, ownLinework, rules.FacesOnly.IsMatch, out int neighbourWalls);
            neighbourTotal += neighbourWalls;
            if (neighbourWalls > 0)
                notes.Add($"{name}: {neighbourWalls} walls on the outline layer away from this building's own walls, doors and stairs left out (the buildings next door).");
            facade = Rosettes.Remove(facade, out var rosettes);
            foreach (var (c, r, n) in rosettes)
                notes.Add($"{name}: {n} lines crossing at one point (x {c.X / 1000:0.0}, y {c.Y / 1000:0.0} m, {2 * r / 1000:0.0} m across) read as a pattern, not walls: a lantern, rooflight or round feature.");
            // walls drawn on glazed-screen layers become curtain walls
            facade = facade.Select(w => rules.Classify(w.Centerline.Layer) == LayerRole.CurtainWall ? w with { Curtain = true } : w).ToList();
            // glazed screens the pair reader left (single lines, thin pairs)
            facade.AddRange(GlassScreens.Find(lines.Where(l => rules.Classify(l.Layer) == LayerRole.CurtainWall).ToList(), facade));
            walls = walls with { Walls = facade, ThicknessGroups = facade.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
            // unclear pockets now filled by a pier wall are explained
            var unclearLeft = raster.Unclear.Where(u => !facade.Any(w => w.FaceA == "pier" && FacadeWalls.Contains(u.Outline, w.Centerline.Mid))).ToList();
            var plan = raster with { Walls = walls, Unclear = unclearLeft };
            // with the buildings next door left out, the floor outline (the slab) is cut back to this building's walls too
            if (neighbourWalls > 0 && plan.Outline is Polygon2 before && NeighbourWalls.Envelope(facade) is { } envelope
                && NeighbourWalls.Clip(before, envelope) is Polygon2 clipped && clipped.Area < before.Area * 0.98)
            {
                plan = plan with { Outline = clipped };
                notes.Add($"{name}: floor outline cut back to this building's walls, {before.Area / 1e6:0} -> {clipped.Area / 1e6:0} m² (the rest is next door).");
            }
            var level = new SurveyLevel(p.Name.LevelCode!, name, order, est?.FloorLevelMm ?? double.NaN, est, p, plan.Walls, plan.Outline, plan.Unclear);
            level.IsRoom = raster.IsRoom;
            level.IsMaterial = raster.IsMaterial;
            if (raster.Leftover != null)
                level.Leftover = raster.Leftover(pt => facade.Any(w => RoomBounds.Gap(w, pt) <= 30))
                    .Where(cells => cells.Count > 0 && !unclearLeft.Any(u => FacadeWalls.Contains(u.Outline, cells[cells.Count / 2]))) // unclear areas have their own generic model
                    .Where(cells => !IsDoorSwing(cells, openings.Where(o => rules.Classify(o.Layer) == LayerRole.Door).ToList()))
                    // drawn material only (not the outline's fill where it bridges a concave façade)
                    .Where(cells => raster.IsMaterial == null || cells.Count(raster.IsMaterial) >= 0.7 * cells.Count).ToList();
            // a pier is a short piece of masonry: drawn fill longer than maxPierMm is a ledge, a terrace or the building
            // next door, not a pier; nor is fill outside this building's walls
            if (level.Leftover != null)
            {
                var env = NeighbourWalls.Envelope(facade);
                int fills = level.Leftover.Count;
                level.Leftover = level.Leftover.Where(cells =>
                {
                    double w = cells.Max(c => c.X) - cells.Min(c => c.X), h = cells.Max(c => c.Y) - cells.Min(c => c.Y);
                    if (Math.Max(w, h) > tol.MaxPierMm) return false;
                    var mid = new Vec2(cells.Average(c => c.X), cells.Average(c => c.Y));
                    return env == null || NeighbourWalls.Outside(env, mid) <= 300;
                }).ToList();
                if (level.Leftover.Count < fills)
                    notes.Add($"{name}: {fills - level.Leftover.Count} drawn fills left out as piers (longer than {tol.MaxPierMm / 1000:0.#} m, or outside this building's walls).");
            }
            level.Stairs = StairFinder.Find(p.FeatureLines.Where(f => rules.Classify(f.Layer) == LayerRole.Stair).Select(frame.ToModel).ToList(),
                p.Texts.Select(t => (frame.ToModel(t.Position), t.Value)).ToList());
            doorInput[level] = openings.Where(o => rules.Classify(o.Layer) == LayerRole.Door).ToList();
            level.RoomNames = p.Texts.Where(t => rules.Classify(t.Layer) == LayerRole.RoomName)
                .Select(t => (frame.ToModel(t.Position), Name: System.Text.RegularExpressions.Regex.Replace(t.Value, @"\s+", " ").Trim()))
                .Where(t => t.Name.Length >= 2).ToList();
            windowInput[level] = (openings.Where(o => rules.Classify(o.Layer) == LayerRole.Window).ToList(),
                                  p.Texts.Select(t => (frame.ToModel(t.Position), t.Value)).ToList());
            levels.Add(level);
            if (plan.Outline == null) notes.Add($"{name}: no closed building outline found; no floor slab.");
        }
        levels = levels.OrderBy(l => l.Order).ThenBy(l => l.ElevationMm).ToList();
        // a floor can't sit less than a low storey above the one below it: a plan whose most common
        // spot height is (e.g. a roof plan's terrace) takes its next best level that clears the floor below
        for (int i = 1; i < levels.Count; i++)
        {
            var below = levels.Take(i).LastOrDefault(l => !double.IsNaN(l.ElevationMm) && l.Order < levels[i].Order && Math.Floor(l.Order) != Math.Floor(levels[i].Order));
            if (below == null || double.IsNaN(levels[i].ElevationMm) || levels[i].ElevationMm - below.ElevationMm >= MinStoreyMm) continue;
            var better = levels[i].Estimate?.OtherLevels.Where(o => o.LevelMm - below.ElevationMm >= MinStoreyMm).OrderByDescending(o => o.Count).ThenBy(o => o.LevelMm).FirstOrDefault();
            double was = levels[i].ElevationMm;
            levels[i].ElevationMm = better is { Count: > 0 } b ? b.LevelMm : below.ElevationMm + DefaultTopWallHeightMm;
            notes.Add($"{levels[i].Name}: most spot heights ({was / 1000:0.00} m) are under {MinStoreyMm / 1000:0.0} m above {below.Name}; level set to {levels[i].ElevationMm / 1000:0.00} m" +
                      (better is { Count: > 0 } ? " (its next most common spot height). Check it." : " (an assumed storey). Check it."));
        }

        if (decisions != null)
        {
            outcomes.AddRange(DecisionRules.ApplyLevels(decisions, levels));
            foreach (var l in levels)
            {
                var (walls, outs) = DecisionRules.ApplyWalls(decisions, l, l.Walls.Walls, windowInput[l].Lines);
                if (outs.Count > 0) l.Walls = l.Walls with { Walls = walls, ThicknessGroups = walls.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
                outcomes.AddRange(outs);
            }
            foreach (var x in decisions.NotWalls.Concat(decisions.Walls).Where(x => !levels.Any(l => string.Equals(l.Code, x.Level, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, x.Level, StringComparison.OrdinalIgnoreCase))))
                outcomes.Add(new DecisionOutcome(decisions.NotWalls.Contains(x) ? "notWalls" : "walls", x.Level, "stale", "no floor of that code or name"));
            outcomes.AddRange(DecisionRules.Waiting(decisions));
            notes.Add($"Decisions from {decisions.Path}: {outcomes.Count(o => o.State == "applied")} applied, {outcomes.Count(o => o.State == "stale")} match nothing, " +
                      $"{outcomes.Count(o => o.State == "contradicted")} contradicted by the drawing, {outcomes.Count(o => o.State == "waiting")} read for steps this version doesn't have yet.");
            foreach (var o in outcomes.Where(o => o.State is "stale" or "contradicted"))
                notes.Add($"CHECK decision {o.Kind} {o.What}: {(o.State == "stale" ? "matches nothing" : "contradicted")}: {o.Detail}.");
        }

        // item H: each section's height fit, where it sits along its cut, and its outer profile at each end (feeds item C)
        var sectionIndex = new List<SectionIndexEntry>();
        var profiles = new List<StretchProfile>();
        foreach (var sec in sections)
        {
            try { var (entry, found) = SectionProfiles.Build(sec, sec.Lines, sec.Marks, levels); sectionIndex.Add(entry); profiles.AddRange(found); }
            catch (Exception ex) { notes.Add($"Section {sec.Name}: profile not read ({ex.Message})."); }
        }
        if (sectionIndex.Count > 0)
            notes.Add($"Sections: {sectionIndex.Count(x => x.Registration?.Sure == true)} of {sectionIndex.Count} registered along their cuts; {profiles.Count} facade profiles read"
                      + (sectionIndex.SelectMany(x => x.Ends).Where(x => x.OffsetMm is double o && Math.Abs(o) > 100).ToList() is { Count: > 0 } off
                          ? $"; section face off the plan's by {string.Join(", ", off.Select(o => $"{o.OffsetMm:0} mm"))} (taken out of the profiles)" : "") + ".");

        // item C: what the top of each facade stretch is, from the evidence; a mansard's cut through its skin is not walls
        var (topStorey, _) = FacadeTops.TopStoreys(levels);
        var tops = new List<FacadeTopCall>();
        if (topStorey != null)
        {
            tops = FacadeTops.Classify(levels, windowInput[topStorey].Lines, null, profiles, decisions);
            var keptWalls = FacadeTops.LeaveOutSkins(topStorey, tops, windowInput[topStorey].Lines);
            if (keptWalls.Count < topStorey.Walls.Walls.Count)
            {
                notes.Add($"{topStorey.Name}: {topStorey.Walls.Walls.Count - keptWalls.Count} thin lines on the mansard stretches are the plan's cut through the roof's skin: left out of the walls ({tops.Sum(t => t.DormerFrontsKept.Count)} dormer fronts kept).");
                topStorey.Walls = topStorey.Walls with { Walls = keptWalls, ThicknessGroups = keptWalls.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
            }
        }

        // party / flank walls drawn on some floors only (inside face only, or a stretch left out): carried through
        var party = new List<PartyWalls.Added>();
        for (int pass = 0; pass < 3; pass++) // a floor filled in one pass can complete the floor next to it in the next
        {
            // walls with window or door linework along them (windows are placed later): façades, not party walls
            var opened = levels.Select(l =>
            {
                var marks = windowInput[l].Lines.Concat(doorInput[l]).Where(s => s.Length >= 300).ToList();
                return l.Walls.Walls.Where(w => w.Arc == null && marks.Any(s =>
                {
                    var c = w.Centerline; var n = c.Direction.Perp();
                    double along = (s.Mid - c.A).Dot(c.Direction);
                    return along > 0 && along < c.Length && Math.Abs((s.Mid - c.A).Dot(n)) <= w.TypeThickness / 2 + 400;
                })).ToHashSet(ReferenceEqualityComparer.Instance as IEqualityComparer<WallCandidate>);
            }).ToList();
            var found = PartyWalls.Fill(levels.Select(l => ((IReadOnlyList<WallCandidate>)l.Walls.Walls.Concat(party.Where(a => a.Floor == levels.IndexOf(l)).Select(a => a.Wall)).ToList(), l.Walls.UnpairedSegments)).ToList(),
                withOpenings: opened);
            if (found.Count == 0) break;
            party.AddRange(found);
        }
        foreach (var g in party.GroupBy(a => a.Floor))
        {
            var lv = levels[g.Key];
            var all = lv.Walls.Walls.Concat(g.Select(a => a.Wall)).ToList();
            lv.Walls = lv.Walls with { Walls = all, ThicknessGroups = all.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
            notes.Add($"{lv.Name}: {g.Count()} stretches of party/flank wall ({g.Sum(a => a.Wall.Length) / 1000:0.0} m) carried from the floors below/above ({string.Join("; ", g.Select(a => a.Why).Distinct())}).");
        }
        // party walls drawn by their inside face only (built thin): thickened outward, the drawn face kept
        {
            var opened = levels.Select(l =>
            {
                var marks = windowInput[l].Lines.Concat(doorInput[l]).Where(s => s.Length >= 300).ToList();
                return l.Walls.Walls.Where(w => w.Arc == null && marks.Any(s =>
                {
                    var c = w.Centerline; var n = c.Direction.Perp();
                    double along = (s.Mid - c.A).Dot(c.Direction);
                    return along > 0 && along < c.Length && Math.Abs((s.Mid - c.A).Dot(n)) <= w.TypeThickness / 2 + 400;
                })).ToHashSet(ReferenceEqualityComparer.Instance as IEqualityComparer<WallCandidate>);
            }).ToList();
            var thick = PartyWalls.Thicken(levels.Select(l => (IReadOnlyList<WallCandidate>)l.Walls.Walls).ToList(), opened);
            foreach (var g in thick.GroupBy(x => x.Floor))
            {
                var lv = levels[g.Key];
                var swap = g.ToDictionary(x => x.Old, x => x.New, ReferenceEqualityComparer.Instance as IEqualityComparer<WallCandidate>);
                var all = lv.Walls.Walls.Select(w => swap.TryGetValue(w, out var nw) ? nw : w).ToList();
                lv.Walls = lv.Walls with { Walls = all, ThicknessGroups = all.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
                notes.Add($"{lv.Name}: {g.Count()} thin windowless edge walls on a line the floor below/above repeats ({g.Sum(x => x.Old.Length) / 1000:0.0} m) read as party walls drawn by their inside face: thickened outward to 215 mm.");
            }
        }
        // which outside walls line up with the façade on the floors below and above (the building's face)
        var stacked = FacadeStack.Shares(levels.Select(l => (l.Walls.Walls, l.Outline)).ToList());
        // floors with a mezzanine over part of them (see below): outside it they are double height
        Polygon2? MezzanineOver(int i) => Mezzanine(levels, i);
        for (int i = 0; i < levels.Count; i++)
        {
            var next = levels.Skip(i + 1).FirstOrDefault(l => !double.IsNaN(l.ElevationMm) && l.ElevationMm > levels[i].ElevationMm);
            levels[i].WallHeightMm = next != null ? next.ElevationMm - levels[i].ElevationMm : DefaultTopWallHeightMm;
            var (wl, tx) = windowInput[levels[i]];
            var heights = SurveyHeights.Read(tx, rules.LabelWords);
            // surveyed ceiling heights ("u/s 2.52") win over the default
            var surveyedCeiling = SurveyHeights.CeilingHeight(heights, levels[i].WallHeightMm);
            levels[i].CeilingNotes = heights.Where(h => h.Kind == HeightKind.Ceiling && h.Mm >= 1800 && h.Mm <= levels[i].WallHeightMm - 50)
                .Select(h => (h.Position, h.Mm)).ToList();
            // heights on the survey sections between this floor and the next: the ceilings (and soffits) seen in section
            var inSection = sections.SelectMany(s => s.HeightsMm).Where(h => next != null && h > levels[i].ElevationMm + 2000 && h < next.ElevationMm - 60).OrderBy(h => h).ToList();
            if (surveyedCeiling == null && inSection.Count > 0)
            {
                surveyedCeiling = Math.Round((inSection[inSection.Count / 2] - levels[i].ElevationMm) / 10) * 10;
                notes.Add($"{levels[i].Name}: ceiling {surveyedCeiling / 1000:0.00} m from the survey section ({inSection.Count} heights on the section in this storey).");
            }
            if (sections.Count > 0 && !double.IsNaN(levels[i].ElevationMm))
            {
                var near = sections.SelectMany(s => s.HeightsMm).Where(h => Math.Abs(h - levels[i].ElevationMm) <= 600).OrderBy(h => Math.Abs(h - levels[i].ElevationMm)).ToList();
                if (near.Count > 0 && Math.Abs(near[0] - levels[i].ElevationMm) <= 30) notes.Add($"{levels[i].Name}: level {levels[i].ElevationMm / 1000:0.00} m agrees with the survey section.");
                else if (near.Count > 0) notes.Add($"CHECK {levels[i].Name}: level {levels[i].ElevationMm / 1000:0.00} m from the plan; the section shows {string.Join(", ", near.Take(3).Select(h => $"{h / 1000:0.00}"))} m nearby.");
            }
            levels[i].CeilingFromSurvey = surveyedCeiling != null;
            levels[i].CeilingHeightMm = surveyedCeiling ?? DefaultCeilingHeight(levels[i].WallHeightMm);
            var windowWalls = new List<WallCandidate>();
            // under a mezzanine's void the windows can rise to the floor after it; under the mezzanine itself they stop below it
            var mezzAbove = MezzanineOver(i);
            double windowStorey = mezzAbove != null && !double.IsNaN(levels[i + 2].ElevationMm) ? levels[i + 2].ElevationMm - levels[i].ElevationMm : levels[i].WallHeightMm;
            levels[i].Windows = WindowFinder.Find(wl, levels[i].Walls.Walls, tx, windowStorey, outline: levels[i].Outline, ceilingMm: mezzAbove != null ? null : levels[i].CeilingHeightMm,
                added: windowWalls, isMaterial: levels[i].IsMaterial, wallSizes: tol.WallTypeSizes, labelWords: rules.LabelWords);
            if (mezzAbove != null)
                levels[i].Windows = levels[i].Windows.Select(w => FacadeWalls.Contains(mezzAbove, w.Center) || EdgeDistance(mezzAbove, w.Center) < 300
                    ? w with { HeadMm = Math.Min(w.HeadMm, w.HeightsFromSurvey ? levels[i].WallHeightMm - 150 : Math.Min(levels[i].WallHeightMm - 150, Math.Max(w.SillMm + 300, levels[i].CeilingHeightMm))) } : w)
                    .Where(w => w.HeadMm - w.SillMm >= 300).ToList();
            if (windowWalls.Count > 0)
            {
                var withWindows = levels[i].Walls.Walls.Concat(windowWalls).ToList();
                levels[i].Walls = levels[i].Walls with { Walls = withWindows, ThicknessGroups = withWindows.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
            }
            var doorWalls = new List<WallCandidate>();
            // door linework far from the building (a stray block, a copy on the sheet border) is not a door here
            var nearBuilding = Near(levels[i].Walls.Walls, 3000);
            levels[i].Doors = DoorFinder.Find(doorInput[levels[i]].Where(s => nearBuilding(s.A) && nearBuilding(s.B)).ToList(), levels[i].Walls.Walls, levels[i].Windows, heights: heights, storeyMm: levels[i].WallHeightMm,
                arcs: levels[i].Plan.DoorArcs.Select(frame.ToModel).Where(a => nearBuilding(a.Center)).ToList(), added: doorWalls, wallSizes: tol.WallTypeSizes);
            if (doorWalls.Count > 0)
            {
                var before = levels[i].Walls.Walls.Concat(doorWalls).ToList(); // the list the doors' and windows' host numbers refer to
                var all = GlassScreens.CutForDoors(levels[i].Walls.Walls.ToList(), doorWalls).Concat(doorWalls).ToList();
                var at = all.Select((w, k) => (w, k)).GroupBy(x => x.w, ReferenceEqualityComparer.Instance).ToDictionary(g => g.Key!, g => g.First().k, ReferenceEqualityComparer.Instance);
                levels[i].Doors = levels[i].Doors.Where(d => at.ContainsKey(before[d.HostIndex])).Select(d => d with { HostIndex = at[before[d.HostIndex]] }).ToList();
                levels[i].Windows = levels[i].Windows.Where(w => at.ContainsKey(before[w.HostIndex])).Select(w => w with { HostIndex = at[before[w.HostIndex]] }).ToList();
                levels[i].Walls = levels[i].Walls with { Walls = all, ThicknessGroups = all.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
            }
            // door blocks on other layers ("doorl" on a joinery layer): doors where they swing in a wall's opening
            if (levels[i].Plan.DoorBlockLines.Count > 0)
            {
                var blockLines = AxisSnapper.Snap(levels[i].Plan.DoorBlockLines.Select(frame.ToModel), levels[i].Walls.Axes, tol.AngleSnapDeg).Where(s => nearBuilding(s.A) && nearBuilding(s.B)).ToList();
                var extra = DoorFinder.Find(blockLines, levels[i].Walls.Walls, levels[i].Windows, heights: heights, storeyMm: levels[i].WallHeightMm,
                        arcs: levels[i].Plan.DoorArcs.Select(frame.ToModel).Where(a => nearBuilding(a.Center)).ToList(), wallSizes: tol.WallTypeSizes)
                    .Where(d => d.Swing != null && !levels[i].Doors.Any(o => o.Center.DistanceTo(d.Center) < (o.WidthMm + d.WidthMm) / 2)).ToList();
                if (extra.Count > 0)
                {
                    levels[i].Doors = levels[i].Doors.Concat(extra).ToList();
                    notes.Add($"{levels[i].Name}: {extra.Count} doors from door blocks drawn on other layers (e.g. joinery).");
                }
            }
            // doors the survey names in words next to door linework with no door ("Sliding Door", "FD30")
            var named = DoorFinder.FromNotes(doorInput[levels[i]].Where(s => nearBuilding(s.A) && nearBuilding(s.B)).ToList(), levels[i].Walls.Walls, levels[i].Doors, levels[i].Windows, tx);
            if (named.Count > 0)
            {
                levels[i].Doors = levels[i].Doors.Concat(named).ToList();
                notes.Add($"{levels[i].Name}: {named.Count} doors where a note names one (sliding, folding, fire door) beside door linework.");
            }
            // a row of door leaves side by side in an outside wall, no wall between them, is a folding or sliding glazed
            // screen (onto a terrace or balcony): glazed panels from the floor, not doors
            var screen = GlazedScreens.FromDoorRows(levels[i].Doors, levels[i].Walls.Walls, levels[i].Outline);
            if (screen.Count > 0)
            {
                var gone = screen.SelectMany(s => s.Doors).ToHashSet();
                levels[i].Doors = levels[i].Doors.Where(d => !gone.Contains(d)).ToList();
                levels[i].Windows = levels[i].Windows.Concat(screen.SelectMany(s => s.Panels)).ToList();
                notes.Add($"{levels[i].Name}: {gone.Count} door leaves in {screen.Count} row(s) along outside walls read as folding/sliding glazed screens: glazed panels from the floor.");
            }
            // windows the survey notes as blocked up ("Blocked Window") are wall
            var blockedNotes = tx.Where(t => DoorFinder.IsBlockedNote(t.Item2) && t.Item2.Contains("window", StringComparison.OrdinalIgnoreCase)).Select(t => t.Item1).ToList();
            if (blockedNotes.Count > 0)
            {
                int before = levels[i].Windows.Count;
                levels[i].Windows = levels[i].Windows.Where(w => !blockedNotes.Any(b => b.DistanceTo(w.Center) <= Math.Max(1200, w.WidthMm))).ToList();
                if (levels[i].Windows.Count < before) notes.Add($"{levels[i].Name}: {before - levels[i].Windows.Count} windows noted as blocked up (\"Blocked Window\") left as wall.");
            }
            var filling = OpeningCuts.FillingOpenings(levels[i].Walls.Walls,
                levels[i].Windows.Select(w => (w.HostIndex, w.Center, w.WidthMm)).Concat(levels[i].Doors.Select(d => (d.HostIndex, d.Center, d.WidthMm))));
            if (filling.Count > 0)
            {
                var old = levels[i].Walls.Walls;
                var map = new int[old.Count];
                var kept = new List<WallCandidate>();
                for (int k = 0; k < old.Count; k++) { map[k] = filling.Contains(k) ? -1 : kept.Count; if (map[k] >= 0) kept.Add(old[k]); }
                levels[i].Doors = levels[i].Doors.Select(d => d with { HostIndex = map[d.HostIndex] }).ToList();
                levels[i].Windows = levels[i].Windows.Select(w => w with { HostIndex = map[w.HostIndex] }).ToList();
                levels[i].Walls = levels[i].Walls with { Walls = kept, ThicknessGroups = kept.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
                notes.Add($"{levels[i].Name}: {filling.Count} short walls across window or door openings (their reveals) left out.");
            }
            // a window drawn in a lining or inner leaf goes in the building's face just outside it, cutting through on the way
            var seats = FacadeStack.Seat(levels[i].Walls.Walls, levels[i].Windows.Select(w => (w.HostIndex, w.Center, w.WidthMm)).ToList(), levels[i].Outline, stacked);
            if (seats.Count > 0)
            {
                var wins = levels[i].Windows.ToList();
                foreach (var r in seats)
                    wins[r.Window] = wins[r.Window] with { HostIndex = r.Host, Center = r.Center, Outward = r.Out, AlsoCut = wins[r.Window].AlsoCut.Concat(r.AlsoCut).ToList() };
                levels[i].Windows = wins;
                notes.Add($"{levels[i].Name}: {seats.Count} windows drawn in an inner wall moved into the façade outside them (the face that lines up with the floors above and below); the inner walls are cut through.");
            }
            // a window must look out: a wall drawn outside it (a balcony balustrade, a parapet) is low, not a storey high
            var inFront = OpeningCuts.InFront(levels[i].Walls.Walls,
                levels[i].Windows.Select(w => (w.HostIndex, w.Center, w.WidthMm, w.SillMm)).ToList(), levels[i].Outline, levels[i].Doors.Select(d => d.HostIndex), out var through);
            // a thin wall outside a window set in a real wall (one standing on the floors below and above) is a glazed
            // balustrade or screen, even with glass drawn in it on the window layer: low, and its "windows" are its glass
            var screens = through.Where(t =>
            {
                var host = levels[i].Walls.Walls[levels[i].Windows[t.Window].HostIndex]; var front = levels[i].Walls.Walls[t.Wall];
                return stacked.GetValueOrDefault(host) >= FacadeStack.StackedShare && front.TypeThickness <= 200 && front.TypeThickness <= host.TypeThickness - 100;
            }).ToList();
            if (screens.Count > 0)
            {
                foreach (var t in screens)
                    inFront[t.Wall] = Math.Min(inFront.GetValueOrDefault(t.Wall, double.MaxValue), levels[i].Windows[t.Window].SillMm);
                var screenWalls = screens.Select(t => t.Wall).ToHashSet();
                through = through.Where(t => !screenWalls.Contains(t.Wall)).ToList();
                var glass = levels[i].Windows.Count(w => screenWalls.Contains(w.HostIndex));
                var kept = levels[i].Windows.Select((w, k) => (w, k)).Where(x => !screenWalls.Contains(x.w.HostIndex)).ToList();
                var newIndex = kept.Select((x, n) => (x.k, n)).ToDictionary(x => x.k, x => x.n);
                through = through.Where(t => newIndex.ContainsKey(t.Window)).Select(t => (newIndex[t.Window], t.Wall, t.At)).ToList();
                levels[i].Windows = kept.Select(x => x.w).ToList();
                notes.Add($"{levels[i].Name}: {screenWalls.Count} thin walls outside windows in the main walls read as glazed balustrades (low walls); {glass} panes drawn in them left out.");
            }
            if (through.Count > 0)
            {
                var wins = levels[i].Windows.ToList();
                foreach (var g in through.GroupBy(t => t.Window))
                    wins[g.Key] = wins[g.Key] with { AlsoCut = wins[g.Key].AlsoCut.Concat(g.Select(t => (t.Wall, t.At))).ToList() };
                levels[i].Windows = wins;
                notes.Add($"{levels[i].Name}: {through.Select(t => t.Window).Distinct().Count()} windows set in an inner wall also cut through the façade wall in front of them.");
            }
            if (inFront.Count > 0)
            {
                var ws = levels[i].Walls.Walls.ToList();
                foreach (var (k, sill) in inFront) ws[k] = ws[k] with { HeightMm = sill is >= 600 and <= ParapetMm ? sill : ParapetMm };
                levels[i].Walls = levels[i].Walls with { Walls = ws };
                notes.Add($"{levels[i].Name}: {inFront.Count} walls outside windows (balustrades, parapets) made low, up to the sill or {ParapetMm:0} mm, so the windows look out.");
            }
            // a wall with its own spot heights on top (inside its thickness) standing well under the storey: a parapet,
            // a terrace or boundary wall, a kerb: built to that height, not a storey high
            if (!double.IsNaN(levels[i].ElevationMm))
            {
                var spots = levels[i].Plan.Texts.Where(t => rules.Classify(t.Layer) == LayerRole.LevelText)
                    .Select(t => (P: frame.ToModel(t.Position), Mm: LevelEstimator.ParseMm(t.Value))).Where(s => s.Mm != null)
                    .Select(s => (s.P, Rel: s.Mm!.Value - levels[i].ElevationMm)).ToList();
                var hosts = levels[i].Windows.Select(w => w.HostIndex).Concat(levels[i].Doors.Select(d => d.HostIndex)).ToHashSet();
                var ws = levels[i].Walls.Walls.ToList();
                int lowered = 0;
                for (int k = 0; k < ws.Count; k++)
                {
                    var w = ws[k];
                    if (w.Arc != null || w.Curtain || w.HeightMm != null || hosts.Contains(k) || w.Length < 500) continue;
                    var c = w.Centerline; var n = c.Direction.Perp();
                    var on = spots.Where(s => { double a = (s.P - c.A).Dot(c.Direction), o = Math.Abs((s.P - c.A).Dot(n)); return a >= 0 && a <= c.Length && o <= w.TypeThickness / 2 + 50; }).ToList();
                    // every spot on it low, at least 0.3 m up (0.2 m is a kerb or a floor band), and enough of them to speak for
                    // its whole length (one spot at one end of a long party wall says nothing about the rest)
                    if (on.Count == 0 || on.Any(s => s.Rel < 300 || s.Rel > levels[i].WallHeightMm - 500)) continue;
                    if (w.Length > 4000)
                    {
                        var at = on.Select(s => (s.P - c.A).Dot(c.Direction)).ToList();
                        if (on.Count < Math.Ceiling(w.Length / 5000) || at.Max() - at.Min() < 0.5 * w.Length) continue;
                    }
                    ws[k] = w with { HeightMm = Math.Round(on.Max(s => s.Rel) / 10) * 10 };
                    lowered++;
                }
                if (lowered > 0)
                {
                    levels[i].Walls = levels[i].Walls with { Walls = ws };
                    notes.Add($"{levels[i].Name}: {lowered} walls with spot heights on their top built to them (parapets, terrace and boundary walls), not a storey high.");
                }
            }
            if (next == null) notes.Add(levels[i].WallHeightMm != DefaultTopWallHeightMm
                ? $"{levels[i].Name}: top level, walls {levels[i].WallHeightMm:0} mm high, up to the lowest roof-plan spot over it (eaves / flat roof)."
                : $"{levels[i].Name}: top level, walls given an assumed height of {DefaultTopWallHeightMm:0} mm.");
        }

        // a mezzanine covers part of the floor below; elsewhere that floor is double height, its walls running up past it
        for (int i = 0; i + 2 < levels.Count; i++)
        {
            if (Mezzanine(levels, i) is not Polygon2 mezz || levels[i].Outline is not Polygon2 below) continue;
            levels[i + 1].Partial = true;
            // the ceiling below is only under the mezzanine; the rest is open to the floor after
            if (mezz.IsSimple()) levels[i].CeilingOutline = mezz;
            int up = 0;
            var ws = levels[i].Walls.Walls.Select(w =>
            {
                var mid = WallGeometry.PointAt(w, WallGeometry.Length(w) / 2);
                if (FacadeWalls.Contains(mezz, mid) || EdgeDistance(mezz, mid) < 300) return w;
                up++;
                return w with { PastPartialFloor = true };
            }).ToList();
            levels[i].Walls = levels[i].Walls with { Walls = ws };
            notes.Add($"{levels[i + 1].Name} covers {mezz.Area / below.Area:P0} of the {levels[i].Name}: read as a mezzanine; {up} {levels[i].Name} walls outside it run up to the {levels[i + 2].Name}.");
        }

        // openings clear of the walls that run into their edges
        foreach (var l in levels)
        {
            var (moved, narrowed, rehosted, trimmed) = OpeningClearance.Fit(l);
            if (moved + narrowed + rehosted + trimmed > 0)
                notes.Add($"{l.Name}: openings kept clear of walls running into them: {trimmed} partitions stopped at the wall face, {moved} openings moved along their wall, {narrowed} narrowed to the clear width" +
                          (rehosted > 0 ? $", {rehosted} doors set into the drawn wall they lie in" : "") + ".");
        }

        notes.Add($"Survey base point E {frame.BasePoint.X / 1000:0.000} m, N {frame.BasePoint.Y / 1000:0.000} m; " +
                  $"model turned {frame.RotationDeg:0.00}° so the main walls are square (grid north = true north).");
        foreach (var l in levels) l.Details = RoofsAndDetails.Details(l, frame, rules);
        for (int i = 1; i < levels.Count; i++)
            levels[i].StairVoids = levels[i - 1].Stairs.Select(st => st.Footprint()).Where(p => p.Vertices.Count >= 3 && p.Area >= 0.5e6).ToList();
        // roofs only where the survey shows them: roof plans, and roof layers on the floor plans
        var roofs = new List<RoofCandidate>();
        var context = new List<RoofCandidate>();
        int noHeight = 0;
        foreach (var rp in roofPlans)
        {
            roofs.AddRange(RoofsAndDetails.FromPlan(rp, frame, rules, true, null, out int n));
            noHeight += n;
            notes.Add($"{rp.Name.FileName}: read as a roof plan.");
        }
        foreach (var l in levels)
        {
            roofs.AddRange(RoofsAndDetails.FromPlan(l.Plan, frame, rules, false, l.Outline, out int n));
            noHeight += n;
        }
        if (noHeight > 0) notes.Add($"{noHeight} roof areas have no spot height inside them and were left out.");
        // a roof-plan area just above a floor's own slab (under 1.2 m: tiles, decking, lead on a terrace or balcony) is that
        // floor's finish, not a roof of its own: built as one it is a second slab stacked on the floor's
        {
            int before = roofs.Count;
            roofs = roofs.Where(r =>
            {
                if (r.Outline.Vertices.Count < 3) return true;
                // a strip under 0.6 m wide (a gutter, a lead flashing, a coping) is not a roof of its own
                double bw = r.Outline.Vertices.Max(v => v.X) - r.Outline.Vertices.Min(v => v.X), bh = r.Outline.Vertices.Max(v => v.Y) - r.Outline.Vertices.Min(v => v.Y);
                if (Math.Min(bw, bh) < 600 || r.Outline.Area < 0.6 * 0.6 * 1e6) return false;
                var mid = new Vec2(r.Outline.Vertices.Average(v => v.X), r.Outline.Vertices.Average(v => v.Y));
                var under = levels.Where(l => !double.IsNaN(l.ElevationMm) && l.ElevationMm <= r.ElevationMm + 200 && l.Outline != null && FacadeWalls.Contains(l.Outline, mid))
                                  .OrderByDescending(l => l.ElevationMm).FirstOrDefault();
                return under == null || r.ElevationMm - under.ElevationMm > 1200;
            }).ToList();
            if (roofs.Count < before) notes.Add($"{before - roofs.Count} roof-plan areas left out: strips under 0.6 m wide (gutters, flashings), or just above a floor's slab (that floor's terrace or balcony finish).");
        }
        // a roof plan drawn with the roofs next door: only the roofs over this building's walls (any floor's) are kept
        if (neighbourTotal > 0 && NeighbourWalls.Envelope(levels.SelectMany(l => l.Walls.Walls)) is { } whole)
        {
            int before = roofs.Count;
            bool Ours(RoofCandidate r) => r.Outline.Vertices.Count < 3
                || NeighbourWalls.Outside(whole, new Vec2(r.Outline.Vertices.Average(v => v.X), r.Outline.Vertices.Average(v => v.Y))) <= 500;
            context = roofs.Where(r => !Ours(r)).ToList();
            roofs = roofs.Where(Ours).ToList();
            if (roofs.Count < before) notes.Add($"{before - roofs.Count} roof areas over the buildings next door: built as context (their own workset), not as this building's roofs.");
        }
        if (roofPlans.Count == 0 && roofs.Count == 0) notes.Add("No roof plan or roof layer: no roofs built.");
        // item E: elevation drawings split from their sheets, matched to the facade they show, fitted for height
        var elevations = new List<ElevationDrawing>();
        var fronts = new List<ContextFront>();
        if (elevationSheets.Count > 0)
        {
            var found = new List<(ElevationDrawing, List<(double, double, double)>)>();
            foreach (var sheet in elevationSheets)
            {
                try { found.AddRange(ElevationIndex.Split(sheet, rules, SectionReader.SheetLines(sheet, out _))); }
                catch (Exception ex) { notes.Add($"{sheet.Name.FileName}: elevation sheet could not be read ({ex.Message})."); }
            }
            elevations = ElevationIndex.Build(found, levels, frame);
            notes.Add($"Elevations: {elevations.Count} drawings on {elevationSheets.Count} sheets, {elevations.Count(e => e.Facade != null)} matched to a facade.");
            foreach (var e in elevations) notes.Add((e.Facade == null ? "CHECK " : "") + e.Note + ".");
            // item F: the matched drawings' windows set the sills and heads, and add the windows the plan missed
            try { notes.AddRange(ElevationWindows.Apply(elevations, levels, rules).Notes); }
            catch (Exception ex) { notes.Add($"Windows from the elevations not read ({ex.Message})."); }
            try
            {
                fronts = ElevationContext.Read(elevations, levels, rules);
                if (fronts.Count > 0)
                    notes.Add($"Buildings next door on the elevations: {fronts.Count} front steps, {fronts.Sum(f => f.Windows.Count)} windows, built as context (" + string.Join("; ", fronts.Select(f => f.Note)) + ").");
            }
            catch (Exception ex) { notes.Add($"Neighbours on the elevations not read ({ex.Message})."); }
        }
        ContextPlan? contextPlan = null;
        try
        {
            contextPlan = ElevationContext.Plan(fronts, context, levels);
            if (fronts.Count > 0 || context.Count > 0) notes.AddRange(contextPlan.Notes);
        }
        catch (Exception ex) { notes.Add($"Context not planned ({ex.Message}); the roof-plan blocks are built as they are."); }

        if (topStorey != null && tops.Count > 0)
        {
            // again with the roofs read: a flat roof along a flush facade makes it a parapet top
            var final = FacadeTops.Classify(levels, windowInput[topStorey].Lines, roofs, profiles, decisions);
            foreach (var f in final)
                if (tops.FirstOrDefault(t => t.Id == f.Id) is FacadeTopCall early && early.Class == "Mansard")
                { f.Class = early.Class; f.Confidence = early.Confidence; f.Evidence = early.Evidence; f.SkinLinesLeftOut = early.SkinLinesLeftOut; f.DormerFrontsKept = early.DormerFrontsKept; f.Eaves = early.Eaves; f.Kerb = early.Kerb; f.SlopeDeg = early.SlopeDeg; }
            tops = final;
            notes.Add("Facade tops (" + topStorey.Name + "): " + string.Join(", ", tops.GroupBy(t => t.Class).Select(g => $"{g.Count()} {g.Key}")) +
                      $"; {tops.Count(t => t.Confidence == "Low")} from the plan alone (see roof-edges.json and the build log for what would settle them).");
        }
        // self-checks: places to look at, listed for the review list and the report
        var checks = ModelChecks.Run(levels, frame, rules, FacadeStack.Shares(levels.Select(l => (l.Walls.Walls, l.Outline)).ToList()));
        return new SurveyModel { Frame = frame, Levels = levels, Skipped = skipped, Notes = notes, Rules = rules, Roofs = roofs, Context = context, ContextFronts = fronts, ContextPlan = contextPlan, Sections = sections, Checks = checks, Decisions = outcomes, DecisionsFile = decisions, FacadeTops = tops, SectionIndex = sectionIndex, Elevations = elevations };
    }

    /// <summary>Inside the box the walls span (their middle 98%, so one stray wall cannot stretch it), grown by a margin.</summary>
    static Func<Vec2, bool> Near(IReadOnlyList<WallCandidate> walls, double margin)
    {
        var pts = walls.SelectMany(w => new[] { w.Centerline.A, w.Centerline.B }).ToList();
        if (pts.Count < 10) return _ => true;
        var xs = pts.Select(p => p.X).OrderBy(x => x).ToList(); var ys = pts.Select(p => p.Y).OrderBy(y => y).ToList();
        int lo = (int)(0.01 * (pts.Count - 1)), hi = (int)(0.99 * (pts.Count - 1));
        double x0 = xs[lo] - margin, x1 = xs[hi] + margin, y0 = ys[lo] - margin, y1 = ys[hi] + margin;
        return p => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
    }

    /// <summary>
    /// Texts that sit inside rooms: names, spot heights, notes. Sill, head and door heights
    /// ("S 0.94", "H 2.58", "dh 2.03") are left out because they are placed in window and door recesses.
    /// </summary>
    static bool IsRoomMarker(SurveyText t, LayerRules rules)
    {
        if (string.IsNullOrWhiteSpace(t.Value)) return false;
        if (rules.Classify(t.Layer) is LayerRole.Wall or LayerRole.Window or LayerRole.Door) return false;
        return !SurveyHeights.IsOpeningNote(t.Value, rules.LabelWords);
    }

    /// <summary>A pocket closed off by a door leaf and its swing (drawn lines around open floor), not wall material.</summary>
    static bool IsDoorSwing(List<Vec2> cells, List<Segment2> doorLines)
    {
        double x0 = cells.Min(c => c.X) - 100, x1 = cells.Max(c => c.X) + 100, y0 = cells.Min(c => c.Y) - 100, y1 = cells.Max(c => c.Y) + 100;
        var near = doorLines.Where(d => Math.Max(d.A.X, d.B.X) >= x0 && Math.Min(d.A.X, d.B.X) <= x1 && Math.Max(d.A.Y, d.B.Y) >= y0 && Math.Min(d.A.Y, d.B.Y) <= y1).ToList();
        if (near.Count == 0) return false;
        int touching = cells.Count(c => near.Any(d => Dist(c, d) <= 60));
        return touching >= cells.Count * 0.05;
    }

    static double Dist(Vec2 p, Segment2 s)
    {
        var d = s.B - s.A; double l2 = d.Dot(d);
        double t = l2 < 1e-9 ? 0 : Math.Clamp((p - s.A).Dot(d) / l2, 0, 1);
        return p.DistanceTo(s.A + d * t);
    }

    /// <summary>Frame from the ground floor (or lowest plan): centre of its walls rounded to 1 m, and its main wall direction.</summary>
    /// <summary>
    /// The top storey's wall height from the roof plan: the lowest spot height over its outline standing 2.2-4.5 m above
    /// it (the eaves or a flat roof over the storey; higher spots are ridges and plant). Null with no roof plan or no such spot.
    /// </summary>
    static double? TopStoreyHeight(SurveyLevel top, IReadOnlyList<SurveyDrawing> roofPlans, SurveyFrame frame, LayerRules rules)
    {
        if (double.IsNaN(top.ElevationMm) || top.Outline == null) return null;
        var rel = roofPlans.SelectMany(rp => rp.Texts.Where(t => rules.Classify(t.Layer) == LayerRole.LevelText)
                .Select(t => (P: frame.ToModel(t.Position), Mm: LevelEstimator.ParseMm(t.Value))))
            .Where(s => s.Mm != null && FacadeWalls.Contains(top.Outline, s.P))
            .Select(s => s.Mm!.Value - top.ElevationMm).Where(r => r >= 2200 && r <= 4500).OrderBy(r => r).ToList();
        return rel.Count == 0 ? null : Math.Round(rel[0] / 10) * 10;
    }

    static SurveyFrame MakeFrame(List<SurveyDrawing> plans, Tolerances tol)
    {
        var reference = plans.OrderBy(p => Math.Abs(DrawingName.DescribeLevel(p.Name.LevelCode!).Order)).FirstOrDefault();
        if (reference == null || reference.WallLines.Count == 0) return new SurveyFrame(new Vec2(0, 0), 0);
        var lines = reference.WallLines;
        double minX = lines.Min(s => Math.Min(s.A.X, s.B.X)), maxX = lines.Max(s => Math.Max(s.A.X, s.B.X));
        double minY = lines.Min(s => Math.Min(s.A.Y, s.B.Y)), maxY = lines.Max(s => Math.Max(s.A.Y, s.B.Y));
        var centre = new Vec2(Math.Round((minX + maxX) / 2 / 1000) * 1000, Math.Round((minY + maxY) / 2 / 1000) * 1000);
        var axes = AxisSnapper.FindAxes(lines.Where(l => l.Length >= tol.MinSegmentLength).ToList(), tol.AngleSnapDeg);
        // refined on every plan's long lines, both directions, so the walls come out square in the model
        double rotation = AxisSnapper.RefineRotation(plans.SelectMany(p => p.WallLines), AxisSnapper.MainRotationDeg(axes));
        return new SurveyFrame(centre, Math.Round(rotation, 3));
    }
}
