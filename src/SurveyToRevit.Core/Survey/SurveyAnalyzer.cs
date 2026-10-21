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
}

public sealed record SurveyLevel(
    string Code, string Name, double Order, double ElevationMm, LevelEstimate? Estimate,
    SurveyDrawing Plan, WallDetectionResult Walls)
{
    /// <summary>Wall height: up to the next level, or the default for the top level.</summary>
    public double WallHeightMm { get; set; }
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

        var levels = new List<SurveyLevel>();
        foreach (var p in chosen)
        {
            var (order, name) = DrawingName.DescribeLevel(p.Name.LevelCode!);
            var est = LevelEstimator.Estimate(p.SpotHeightsMm(rules));
            if (est == null) notes.Add($"{name}: no spot heights found; level height must be set by hand.");
            else if (est.Supporting < 0.4 * est.Total)
                notes.Add($"CHECK {name}: split or unclear floor level. Used {est.FloorLevelMm / 1000:0.00} m, but only {est.Supporting} of {est.Total} spot heights agree" +
                          (est.OtherLevels.Count > 0 ? $" (others: {string.Join(", ", est.OtherLevels.Select(o => $"{o.LevelMm / 1000:0.00}"))})" : "") +
                          ". Move the level in Revit if needed; its walls follow.");
            var markers = p.Texts.Where(t => !string.IsNullOrWhiteSpace(t.Value)).Select(t => t.Position).ToList();
            var walls = WallDetector.Detect(p.WallLines, tol, p.OpeningLines, markers);
            levels.Add(new SurveyLevel(p.Name.LevelCode!, name, order, est?.FloorLevelMm ?? double.NaN, est, p, walls));
        }
        levels = levels.OrderBy(l => l.Order).ThenBy(l => l.ElevationMm).ToList();

        for (int i = 0; i < levels.Count; i++)
        {
            var next = levels.Skip(i + 1).FirstOrDefault(l => !double.IsNaN(l.ElevationMm) && l.ElevationMm > levels[i].ElevationMm);
            levels[i].WallHeightMm = next != null ? next.ElevationMm - levels[i].ElevationMm : DefaultTopWallHeightMm;
            if (next == null) notes.Add($"{levels[i].Name}: top level, walls given an assumed height of {DefaultTopWallHeightMm:0} mm.");
        }

        var frame = MakeFrame(levels, tol);
        notes.Add($"Survey base point E {frame.BasePoint.X / 1000:0.000} m, N {frame.BasePoint.Y / 1000:0.000} m; " +
                  $"model turned {frame.RotationDeg:0.00}° so the main walls are square (grid north = true north).");
        return new SurveyModel { Frame = frame, Levels = levels, Skipped = skipped, Notes = notes };
    }

    /// <summary>Frame from the ground floor (or lowest plan): centre of its walls rounded to 1 m, and its main wall direction.</summary>
    static SurveyFrame MakeFrame(List<SurveyLevel> levels, Tolerances tol)
    {
        var refLevel = levels.OrderBy(l => Math.Abs(l.Order)).FirstOrDefault();
        if (refLevel == null || refLevel.Plan.WallLines.Count == 0) return new SurveyFrame(new Vec2(0, 0), 0);
        var lines = refLevel.Plan.WallLines;
        double minX = lines.Min(s => Math.Min(s.A.X, s.B.X)), maxX = lines.Max(s => Math.Max(s.A.X, s.B.X));
        double minY = lines.Min(s => Math.Min(s.A.Y, s.B.Y)), maxY = lines.Max(s => Math.Max(s.A.Y, s.B.Y));
        var centre = new Vec2(Math.Round((minX + maxX) / 2 / 1000) * 1000, Math.Round((minY + maxY) / 2 / 1000) * 1000);
        return new SurveyFrame(centre, Math.Round(AxisSnapper.MainRotationDeg(refLevel.Walls.Axes), 3));
    }
}
