using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Interior;

/// <summary>
/// Where the survey sits in a project that already holds the building's exterior: the survey frame that puts the
/// survey's walls onto the existing façade (in the project's own internal coordinates, which are left as they are),
/// and the datum that puts the survey's floor heights onto the existing levels.
/// </summary>
public sealed record InteriorPlacement(SurveyFrame Frame, double DatumMm, double Score, IReadOnlyList<string> Notes)
{
    /// <summary>Good enough to build on: most of the existing façade lies on the survey's wall lines.</summary>
    public bool Good => Score >= InteriorFit.MinScore;
}

public static class InteriorFit
{
    /// <summary>Share of the existing façade (at the cut heights) that must lie within 100 mm of a survey wall line.</summary>
    public const double MinScore = 0.35;
    /// <summary>Heights the existing model is cut at, above each floor (mm): low, at window height, and just under the ceiling.</summary>
    public static readonly double[] CutHeightsMm = { 300, 1200 };
    /// <summary>Survey floor heights and existing levels this close (mm) are the same floor when finding the datum.</summary>
    public const double LevelMatchMm = 30;

    /// <summary>
    /// The datum (internal mm = survey mm - datum) that lands most survey floors on existing levels. Falls back to
    /// the project's shared elevation when fewer than two floors agree.
    /// </summary>
    public static (double DatumMm, int Matched, string Note) Datum(IReadOnlyList<double> surveyFloorsMm, IReadOnlyList<ExistingLevel> levels, double? sharedDatumMm)
    {
        double bestDatum = double.NaN; int best = 0;
        foreach (var s in surveyFloorsMm)
            foreach (var l in levels)
            {
                double datum = s - l.ElevationMm;
                int matched = surveyFloorsMm.Count(f => levels.Any(x => Math.Abs(f - datum - x.ElevationMm) <= LevelMatchMm));
                bool closerToShared = sharedDatumMm is double sd && !double.IsNaN(bestDatum) && Math.Abs(datum - sd) < Math.Abs(bestDatum - sd);
                if (matched > best || matched == best && closerToShared) { best = matched; bestDatum = datum; }
            }
        if (best >= 2)
        {
            // the middle of the matched floors' differences, so one floor a few mm out does not tilt the rest
            var diffs = surveyFloorsMm.SelectMany(f => levels.Where(x => Math.Abs(f - bestDatum - x.ElevationMm) <= LevelMatchMm).Select(x => f - x.ElevationMm).Take(1)).OrderBy(d => d).ToList();
            double datum = diffs[diffs.Count / 2];
            return (datum, best, $"{best} of {surveyFloorsMm.Count} survey floors sit on the existing levels (within {LevelMatchMm:0} mm) with the survey's {datum / 1000:0.000} m at the project's 0.");
        }
        if (sharedDatumMm is double shared)
            return (shared, best, $"The survey floors do not line up with the existing levels; the project's shared elevation ({shared / 1000:0.000} m at its 0) is used.");
        if (surveyFloorsMm.Count > 0 && levels.Count > 0)
        {
            double ground = surveyFloorsMm.OrderBy(Math.Abs).First();
            var level = levels.OrderBy(l => Math.Abs(l.ElevationMm)).First();
            return (ground - level.ElevationMm, 1, $"The survey floors do not line up with the existing levels; the survey floor nearest 0 is put on '{level.Name}'. Check the heights.");
        }
        return (0, 0, "No levels to fit the heights to; the survey's heights are used as they are.");
    }

    /// <summary>
    /// Fits the survey onto the existing model: the datum from the levels, then (with <see cref="PlanMatch"/>) the turn
    /// and shift that put the existing model's cut outline onto the survey's wall lines, floor by floor; the floor that
    /// fits best decides, the others confirm. A placement from the project's shared coordinates is kept exactly when
    /// the fit agrees with it (within 20 mm and 0.05°).
    /// </summary>
    public static InteriorPlacement Fit(SurveyModel model, ExistingBuilding existing)
    {
        var notes = new List<string>();
        var floors = model.Levels.Where(l => l.Include && !double.IsNaN(l.ElevationMm)).ToList();
        var (datum, _, datumNote) = Datum(floors.Select(l => l.ElevationMm).ToList(), existing.Levels, existing.SharedDatumMm);
        notes.Add(datumNote);

        var frame0 = model.Frame;
        // the existing façade on each floor, against the survey's wall lines on it (both centred, so far-off origins fit too)
        var sets = new List<(SurveyLevel Level, List<Segment2> Survey, List<Segment2> Existing)>();
        foreach (var l in floors)
        {
            var cut = CutAt(existing, l.ElevationMm - datum + 1200);
            if (cut.Count < 10) continue;
            sets.Add((l, l.Plan.WallLines.Select(frame0.ToModel).ToList(), cut));
        }
        if (sets.Count == 0)
        {
            notes.Add("The existing model has nothing at the survey's floor heights to fit to; the survey's own placement is used.");
            return new InteriorPlacement(frame0, datum, 0, notes);
        }
        var cs = Centre(sets.SelectMany(s => s.Survey));
        var ce = Centre(sets.SelectMany(s => s.Existing));
        PlanMove best = new(0, 0, 0, -1);
        string bestFloor = "";
        // the floors with most façade first; a few are enough
        foreach (var s in sets.OrderByDescending(s => s.Existing.Sum(x => x.Length)).Take(4))
        {
            var move = PlanMatch.Fit(Shift(s.Survey, cs * -1), Shift(s.Existing, ce * -1));
            if (move.Score > best.Score) { best = move; bestFloor = s.Level.Name; }
        }
        var frame = Compose(frame0, best, cs, ce);
        foreach (var shared in existing.SharedFrames)
            if (Same(shared, frame)) { frame = shared; notes.Add("The project's shared coordinates already place the survey: kept as they are."); break; }

        // how well every floor fits with the chosen placement
        var perFloor = sets.Select(s => (s.Level.Name, Score: Score(s.Level.Plan.WallLines.Select(frame.ToModel).ToList(), s.Existing))).ToList();
        double score = perFloor.Max(p => p.Score);
        notes.Add($"Survey placed on the existing model from the {bestFloor}: survey grid north {frame.RotationDeg:0.00}° from the project's north, " +
                  $"survey base point at ({frame.ToModel(frame0.BasePoint).X:0}, {frame.ToModel(frame0.BasePoint).Y:0}) mm in the project. " +
                  "Share of the existing façade on the survey's lines: " + string.Join(", ", perFloor.Select(p => $"{p.Name} {p.Score:P0}")) + ".");
        return new InteriorPlacement(frame, datum, score, notes);
    }

    /// <summary>Survey floors this close (mm) to one of the project's own levels are built on that level.</summary>
    public const double LevelSnapMm = 300;

    /// <summary>
    /// Puts each survey floor on the project's own level nearest to it (within <see cref="LevelSnapMm"/>), so the
    /// interior sits on the existing model's floors: the floor's height becomes that level's. Floors with no level
    /// near get a level of their own when built. Returns a line per floor for the log.
    /// </summary>
    public static List<string> SnapLevels(SurveyModel model, IReadOnlyList<ExistingLevel> levels, double datumMm)
    {
        var log = new List<string>();
        var own = levels.Where(l => l.Own).ToList();
        var used = new HashSet<ExistingLevel>();
        foreach (var f in model.Levels.Where(l => l.Include && !double.IsNaN(l.ElevationMm)).OrderBy(l => l.ElevationMm))
        {
            var z = f.ElevationMm - datumMm;
            var near = own.Where(l => !used.Contains(l) && Math.Abs(l.ElevationMm - z) <= LevelSnapMm).OrderBy(l => Math.Abs(l.ElevationMm - z)).FirstOrDefault();
            if (near == null) { log.Add($"{f.Name}: no existing level within {LevelSnapMm:0} mm of {f.ElevationMm / 1000:0.000} m; a level of its own is made."); continue; }
            used.Add(near);
            double diff = near.ElevationMm - z;
            f.ElevationMm = near.ElevationMm + datumMm;
            log.Add($"{f.Name}: on the existing level '{near.Name}'" + (Math.Abs(diff) >= 1 ? $" ({diff:+0;-0} mm from the survey's {(z + datumMm) / 1000:0.000} m)." : "."));
        }
        return log;
    }

    /// <summary>Existing outlines at a height: every element's cut.</summary>
    public static List<Segment2> CutAt(ExistingBuilding existing, double zMm) => existing.Cut(zMm).SelectMany(c => c.Outline).ToList();

    /// <summary>Share of the existing outline's points within 100 mm of a survey line.</summary>
    public static double Score(IReadOnlyList<Segment2> survey, IReadOnlyList<Segment2> existing)
    {
        var cells = new Dictionary<(int, int), List<Segment2>>();
        foreach (var w in survey)
            foreach (var key in Cells(w, 250)) { if (!cells.TryGetValue(key, out var list)) cells[key] = list = new(); list.Add(w); }
        int near = 0, all = 0;
        foreach (var e in existing)
        {
            int n = Math.Max(1, (int)Math.Ceiling(e.Length / 100));
            for (int i = 0; i <= n; i++)
            {
                var p = e.A + (e.B - e.A) * (i / (double)n);
                all++;
                var key = ((int)Math.Floor(p.X / 250), (int)Math.Floor(p.Y / 250));
                bool hit = false;
                for (int dx = -1; dx <= 1 && !hit; dx++)
                    for (int dy = -1; dy <= 1 && !hit; dy++)
                        if (cells.TryGetValue((key.Item1 + dx, key.Item2 + dy), out var list))
                            hit = list.Any(w => Distance(w, p) <= 100);
                if (hit) near++;
            }
        }
        return all == 0 ? 0 : near / (double)all;
    }

    static IEnumerable<(int, int)> Cells(Segment2 w, double size)
    {
        int n = Math.Max(1, (int)Math.Ceiling(w.Length / (size / 2)));
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i <= n; i++)
        {
            var p = w.A + (w.B - w.A) * (i / (double)n);
            var key = ((int)Math.Floor(p.X / size), (int)Math.Floor(p.Y / size));
            if (seen.Add(key)) yield return key;
        }
    }

    internal static double Distance(Segment2 s, Vec2 p)
    {
        var d = s.B - s.A; double l2 = d.Dot(d);
        double t = l2 < 1e-9 ? 0 : Math.Clamp((p - s.A).Dot(d) / l2, 0, 1);
        return p.DistanceTo(s.A + d * t);
    }

    static Vec2 Centre(IEnumerable<Segment2> segs)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var s in segs)
            foreach (var p in new[] { s.A, s.B }) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return new Vec2(Math.Round((x0 + x1) / 2), Math.Round((y0 + y1) / 2));
    }

    static List<Segment2> Shift(IEnumerable<Segment2> segs, Vec2 by) => segs.Select(s => s with { A = s.A + by, B = s.B + by }).ToList();

    static Vec2 Rotate(Vec2 p, double rad) => new(p.X * Math.Cos(rad) - p.Y * Math.Sin(rad), p.X * Math.Sin(rad) + p.Y * Math.Cos(rad));

    /// <summary>
    /// The frame that maps survey grid points straight into the project: the move found maps the existing model
    /// (centred on ce) onto the survey as first placed (frame0, centred on cs), so the project point of a survey
    /// point is ce + move⁻¹(frame0(p) - cs).
    /// </summary>
    internal static SurveyFrame Compose(SurveyFrame frame0, PlanMove move, Vec2 cs, Vec2 ce)
    {
        double r0 = frame0.RotationDeg * Math.PI / 180, r = r0 + move.Angle;
        var b = frame0.BasePoint + Rotate(cs + new Vec2(move.Dx, move.Dy), r0) - Rotate(ce, r);
        return new SurveyFrame(b, r * 180 / Math.PI);
    }

    static bool Same(SurveyFrame a, SurveyFrame b)
    {
        if (Math.Abs(Math.IEEERemainder(a.RotationDeg - b.RotationDeg, 360)) > 0.05) return false;
        // compare where both put a point on the building (the survey base point of one of them)
        return a.ToModel(b.BasePoint).DistanceTo(b.ToModel(b.BasePoint)) <= 20
            && a.ToModel(b.BasePoint + new Vec2(20_000, 20_000)).DistanceTo(b.ToModel(b.BasePoint + new Vec2(20_000, 20_000))) <= 20;
    }

}
