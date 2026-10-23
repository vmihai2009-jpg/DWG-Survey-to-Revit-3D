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
    /// <summary>The layer rules used (built-in, plus any layers.txt).</summary>
    public LayerRules Rules { get; init; } = LayerRules.Default;
    /// <summary>Survey sections read from section sheets (heights, and cut lines for section views).</summary>
    public IReadOnlyList<SurveySection> Sections { get; init; } = Array.Empty<SurveySection>();
    /// <summary>Places the self-checks found worth a look (see <see cref="ModelChecks"/>), most important first.</summary>
    public IReadOnlyList<CheckItem> Checks { get; init; } = Array.Empty<CheckItem>();
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
    /// <summary>A floor smaller than this share of the one below is a mezzanine (when the floor after is whole again, or it is named one).</summary>
    public const double MezzanineShare = 0.6;

    static readonly System.Text.RegularExpressions.Regex MezzanineName = new(@"mezz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The outline of the mezzanine over floor <paramref name="i"/>, or null. A mezzanine is a part floor within
    /// a storey: smaller than <see cref="MezzanineShare"/> of the floor below, with the floor after it whole again
    /// (or named a mezzanine). A building stepping back (every floor above smaller) has no mezzanine.
    /// </summary>
    static Polygon2? Mezzanine(IReadOnlyList<SurveyLevel> levels, int i)
    {
        if (i + 2 >= levels.Count || levels[i + 1].Outline is not Polygon2 m || levels[i].Outline is not Polygon2 below) return null;
        if (m.Area >= MezzanineShare * below.Area) return null;
        bool named = MezzanineName.IsMatch(levels[i + 1].Name);
        bool wholeAfter = levels[i + 2].Outline is Polygon2 after && after.Area >= MezzanineShare * below.Area;
        return named || wholeAfter ? m : null;
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

    public static SurveyModel Analyze(IEnumerable<string> paths, Tolerances? tol = null, LayerRules? rules = null)
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
        var plans = new List<SurveyDrawing>();
        var roofPlans = new List<SurveyDrawing>();
        var sectionSheets = new List<SurveyDrawing>();

        foreach (var path in paths.OrderBy(p => System.IO.Path.GetFileName(p)))
        {
            SurveyDrawing d;
            try { d = SurveyDrawing.Read(path, rules); }
            catch (Exception ex) { skipped.Add(new(System.IO.Path.GetFileName(path), $"could not be read: {ex.Message}")); continue; }

            if (RoofsAndDetails.IsRoofPlan(d, rules)) { roofPlans.Add(d); continue; }
            if (d.Name.LevelCode == null && SectionReader.IsSectionSheet(d)) { sectionSheets.Add(d); continue; }
            if (d.Name.LevelCode == null) { skipped.Add(new(d.Name.FileName, "not a single-floor plan (elevation, section or multi-level sheet)")); continue; }
            if (d.WallLines.Count < 30) { skipped.Add(new(d.Name.FileName, $"only {d.WallLines.Count} lines on wall layers: not a floor plan")); continue; }
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

        var frame = MakeFrame(chosen, tol);
        var sections = new List<SurveySection>();
        foreach (var sheet in sectionSheets)
        {
            try { sections.AddRange(SectionReader.Read(sheet, rules, frame, chosen.SelectMany(c => c.WallLines).ToList())); }
            catch (Exception ex) { notes.Add($"{sheet.Name.FileName}: section sheet could not be read ({ex.Message})."); }
        }
        foreach (var s in sections) notes.Add(s.Note);
        var levels = new List<SurveyLevel>();
        var windowInput = new Dictionary<SurveyLevel, (List<Segment2> Lines, List<(Vec2, string)> Texts)>(ReferenceEqualityComparer.Instance);
        var doorInput = new Dictionary<SurveyLevel, List<Segment2>>(ReferenceEqualityComparer.Instance);
        foreach (var p in chosen)
        {
            var (order, name) = DrawingName.DescribeLevel(p.Name.LevelCode!);
            var est = LevelEstimator.Estimate(p.SpotHeightsMm(rules));
            if (est == null) notes.Add($"{name}: no spot heights found; level height must be set by hand.");
            else if (est.Supporting < 0.4 * est.Total)
                notes.Add($"CHECK {name}: split or unclear floor level. Used {est.FloorLevelMm / 1000:0.00} m, but only {est.Supporting} of {est.Total} spot heights agree" +
                          (est.OtherLevels.Count > 0 ? $" (others: {string.Join(", ", est.OtherLevels.Select(o => $"{o.LevelMm / 1000:0.00}"))})" : "") +
                          ". Move the level in Revit if needed; its walls follow.");

            // Into model coordinates, straightened onto the building's directions.
            var lines = p.WallLines.Select(frame.ToModel).ToList();
            var axes = AxisSnapper.FindAxes(lines.Where(l => l.Length >= tol.MinSegmentLength).ToList(), tol.AngleSnapDeg);
            lines = AxisSnapper.Snap(lines, axes, tol.AngleSnapDeg);
            var openings = AxisSnapper.Snap(p.OpeningLines.Select(frame.ToModel), axes, tol.AngleSnapDeg);
            var markers = p.Texts.Where(t => IsRoomMarker(t, rules)).Select(t => frame.ToModel(t.Position)).ToList();
            var arcs = p.WallArcs.Select(frame.ToModel).ToList();

            var code = p.Name.LevelCode!;
            // The grid reading says where wall material is (robust to gaps in the linework);
            // walls are then the drawn face pairs with wall material between them (exact thickness).
            var raster = new PlanRaster { Debug = PlanDebug == null ? null : (w, h, c) => PlanDebug(code, w, h, c) }.Analyze(lines.Concat(arcs.SelectMany(a => a.Tessellate(100))).Concat(p.SmallWallArcs.Select(frame.ToModel).SelectMany(a => a.Tessellate(40))).ToList(), openings, markers, tol);
            var walls = WallDetector.Detect(lines, tol, openings, markers, raster.IsMaterial);
            var found = WallDetector.FromSingleLines(walls.UnpairedSegments, walls.Walls.ToList(), raster.Outline, tol, rules.FacesOnly.IsMatch);
            var facade = FacadeWalls.Consolidate(found, raster.Outline, tol);
            facade = WallDetector.CloseGaps(facade, tol, openings);
            facade = CurvedWalls.Add(facade, arcs, tol, raster.IsMaterial, raster.Outline);
            facade = WallDetector.BridgeBends(facade, tol, openings);
            facade = PierWalls.Fill(facade, raster.Leftover, tol);
            facade = WallDetector.CloseFacade(facade, raster.Outline, tol);
            facade = WallFit.Fit(facade, raster.IsMaterial, raster.Outline, tol);
            facade = facade.Where(w => w.Length >= tol.MinWallLength).ToList();
            // walls drawn on glazed-screen layers become curtain walls
            facade = facade.Select(w => rules.Classify(w.Centerline.Layer) == LayerRole.CurtainWall ? w with { Curtain = true } : w).ToList();
            // glazed screens the pair reader left (single lines, thin pairs)
            facade.AddRange(GlassScreens.Find(lines.Where(l => rules.Classify(l.Layer) == LayerRole.CurtainWall).ToList(), facade));
            walls = walls with { Walls = facade, ThicknessGroups = facade.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
            // unclear pockets now filled by a pier wall are explained
            var unclearLeft = raster.Unclear.Where(u => !facade.Any(w => w.FaceA == "pier" && FacadeWalls.Contains(u.Outline, w.Centerline.Mid))).ToList();
            var plan = raster with { Walls = walls, Unclear = unclearLeft };
            var level = new SurveyLevel(p.Name.LevelCode!, name, order, est?.FloorLevelMm ?? double.NaN, est, p, plan.Walls, plan.Outline, plan.Unclear);
            level.IsRoom = raster.IsRoom;
            level.IsMaterial = raster.IsMaterial;
            if (raster.Leftover != null)
                level.Leftover = raster.Leftover(pt => facade.Any(w => RoomBounds.Gap(w, pt) <= 30))
                    .Where(cells => cells.Count > 0 && !unclearLeft.Any(u => FacadeWalls.Contains(u.Outline, cells[cells.Count / 2]))) // unclear areas have their own generic model
                    .Where(cells => !IsDoorSwing(cells, openings.Where(o => rules.Classify(o.Layer) == LayerRole.Door).ToList()))
                    // drawn material only (not the outline's fill where it bridges a concave façade)
                    .Where(cells => raster.IsMaterial == null || cells.Count(raster.IsMaterial) >= 0.7 * cells.Count).ToList();
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

        // which outside walls line up with the façade on the floors below and above (the building's face)
        var stacked = FacadeStack.Shares(levels.Select(l => (l.Walls.Walls, l.Outline)).ToList());
        // floors with a mezzanine over part of them (see below): outside it they are double height
        Polygon2? MezzanineOver(int i) => Mezzanine(levels, i);
        for (int i = 0; i < levels.Count; i++)
        {
            var next = levels.Skip(i + 1).FirstOrDefault(l => !double.IsNaN(l.ElevationMm) && l.ElevationMm > levels[i].ElevationMm);
            levels[i].WallHeightMm = next != null ? next.ElevationMm - levels[i].ElevationMm : DefaultTopWallHeightMm;
            var (wl, tx) = windowInput[levels[i]];
            var heights = SurveyHeights.Read(tx);
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
                added: windowWalls, isMaterial: levels[i].IsMaterial, wallSizes: tol.WallTypeSizes);
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
            if (next == null) notes.Add($"{levels[i].Name}: top level, walls given an assumed height of {DefaultTopWallHeightMm:0} mm.");
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

        notes.Add($"Survey base point E {frame.BasePoint.X / 1000:0.000} m, N {frame.BasePoint.Y / 1000:0.000} m; " +
                  $"model turned {frame.RotationDeg:0.00}° so the main walls are square (grid north = true north).");
        foreach (var l in levels) l.Details = RoofsAndDetails.Details(l, frame, rules);
        for (int i = 1; i < levels.Count; i++)
            levels[i].StairVoids = levels[i - 1].Stairs.Select(st => st.Footprint()).Where(p => p.Vertices.Count >= 3 && p.Area >= 0.5e6).ToList();
        // roofs only where the survey shows them: roof plans, and roof layers on the floor plans
        var roofs = new List<RoofCandidate>();
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
        if (roofPlans.Count == 0 && roofs.Count == 0) notes.Add("No roof plan or roof layer: no roofs built.");
        // self-checks: places to look at, listed for the review list and the report
        var checks = ModelChecks.Run(levels, frame, rules, FacadeStack.Shares(levels.Select(l => (l.Walls.Walls, l.Outline)).ToList()));
        return new SurveyModel { Frame = frame, Levels = levels, Skipped = skipped, Notes = notes, Rules = rules, Roofs = roofs, Sections = sections, Checks = checks };
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
        return !SurveyHeights.IsOpeningNote(t.Value);
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
    static SurveyFrame MakeFrame(List<SurveyDrawing> plans, Tolerances tol)
    {
        var reference = plans.OrderBy(p => Math.Abs(DrawingName.DescribeLevel(p.Name.LevelCode!).Order)).FirstOrDefault();
        if (reference == null || reference.WallLines.Count == 0) return new SurveyFrame(new Vec2(0, 0), 0);
        var lines = reference.WallLines;
        double minX = lines.Min(s => Math.Min(s.A.X, s.B.X)), maxX = lines.Max(s => Math.Max(s.A.X, s.B.X));
        double minY = lines.Min(s => Math.Min(s.A.Y, s.B.Y)), maxY = lines.Max(s => Math.Max(s.A.Y, s.B.Y));
        var centre = new Vec2(Math.Round((minX + maxX) / 2 / 1000) * 1000, Math.Round((minY + maxY) / 2 / 1000) * 1000);
        var axes = AxisSnapper.FindAxes(lines.Where(l => l.Length >= tol.MinSegmentLength).ToList(), tol.AngleSnapDeg);
        return new SurveyFrame(centre, Math.Round(AxisSnapper.MainRotationDeg(axes), 3));
    }
}
