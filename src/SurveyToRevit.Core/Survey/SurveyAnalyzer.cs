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
    /// <summary>Wall height: up to the next level, or the default for the top level.</summary>
    public double WallHeightMm { get; set; }
    /// <summary>Ceiling height above this floor (mm). Surveys rarely give it, so it is a default to adjust.</summary>
    public double CeilingHeightMm { get; set; }
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
}

/// <summary>
/// Turns a set of survey DWGs into levels, walls and a coordinate frame,
/// without Revit. One floor plan per level is used; duplicates (e.g. a fire
/// plan of the same floor) are skipped and reported.
/// </summary>
public static class SurveyAnalyzer
{
    public const double DefaultTopWallHeightMm = 3000;

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
        var plans = new List<SurveyDrawing>();

        foreach (var path in paths.OrderBy(p => System.IO.Path.GetFileName(p)))
        {
            SurveyDrawing d;
            try { d = SurveyDrawing.Read(path, rules); }
            catch (Exception ex) { skipped.Add(new(System.IO.Path.GetFileName(path), $"could not be read: {ex.Message}")); continue; }

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
            var raster = new PlanRaster { Debug = PlanDebug == null ? null : (w, h, c) => PlanDebug(code, w, h, c) }.Analyze(lines.Concat(arcs.SelectMany(a => a.Tessellate(100))).ToList(), openings, markers, tol);
            var walls = WallDetector.Detect(lines, tol, openings, markers, raster.IsMaterial);
            var found = WallDetector.FromSingleLines(walls.UnpairedSegments, walls.Walls.ToList(), raster.Outline, tol);
            var facade = FacadeWalls.Consolidate(found, raster.Outline, tol);
            facade = WallDetector.CloseGaps(facade, tol, openings);
            facade = CurvedWalls.Add(facade, arcs, tol, raster.IsMaterial, raster.Outline);
            facade = WallDetector.BridgeBends(facade, tol, openings);
            facade = facade.Where(w => w.Length >= tol.MinWallLength).ToList();
            walls = walls with { Walls = facade, ThicknessGroups = facade.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
            var plan = raster with { Walls = walls };
            var level = new SurveyLevel(p.Name.LevelCode!, name, order, est?.FloorLevelMm ?? double.NaN, est, p, plan.Walls, plan.Outline, plan.Unclear);
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

        for (int i = 0; i < levels.Count; i++)
        {
            var next = levels.Skip(i + 1).FirstOrDefault(l => !double.IsNaN(l.ElevationMm) && l.ElevationMm > levels[i].ElevationMm);
            levels[i].WallHeightMm = next != null ? next.ElevationMm - levels[i].ElevationMm : DefaultTopWallHeightMm;
            levels[i].CeilingHeightMm = DefaultCeilingHeight(levels[i].WallHeightMm);
            var (wl, tx) = windowInput[levels[i]];
            levels[i].Windows = WindowFinder.Find(wl, levels[i].Walls.Walls, tx, levels[i].WallHeightMm);
            levels[i].Doors = DoorFinder.Find(doorInput[levels[i]], levels[i].Walls.Walls, levels[i].Windows);
            if (next == null) notes.Add($"{levels[i].Name}: top level, walls given an assumed height of {DefaultTopWallHeightMm:0} mm.");
        }

        notes.Add($"Survey base point E {frame.BasePoint.X / 1000:0.000} m, N {frame.BasePoint.Y / 1000:0.000} m; " +
                  $"model turned {frame.RotationDeg:0.00}° so the main walls are square (grid north = true north).");
        return new SurveyModel { Frame = frame, Levels = levels, Skipped = skipped, Notes = notes };
    }

    /// <summary>
    /// Texts that sit inside rooms: names, spot heights, notes. Sill/head heights
    /// ("S 0.94", "H 2.58") are left out because they are placed in window recesses.
    /// </summary>
    static bool IsRoomMarker(SurveyText t, LayerRules rules)
    {
        if (string.IsNullOrWhiteSpace(t.Value)) return false;
        if (rules.Classify(t.Layer) is LayerRole.Wall or LayerRole.Window or LayerRole.Door) return false;
        return !System.Text.RegularExpressions.Regex.IsMatch(t.Value.Trim(), @"^(S|H|AH|ASH|D)\s*[\d.]|^(S|H)\s+Not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
