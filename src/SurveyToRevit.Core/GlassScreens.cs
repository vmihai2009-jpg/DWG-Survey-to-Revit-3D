namespace SurveyToRevit.Core;

/// <summary>
/// Glazed screens and partitions drawn on a glazing layer that the face-pair reader did not make
/// into a wall (a single line, or two lines closer than a wall): each band of parallel glass lines
/// becomes one curtain wall along its middle. A door drawn in the screen later cuts it (see
/// <see cref="CutForDoors"/>). Same rules for any survey.
/// </summary>
public static class GlassScreens
{
    public static List<WallCandidate> Find(IReadOnlyList<Segment2> glass, IReadOnlyList<WallCandidate> walls, double minLength = 600, double bandWidth = 150)
    {
        var segs = glass.Where(s => s.Length >= 150).OrderByDescending(s => s.Length).ToList();
        var used = new bool[segs.Count];
        var result = new List<WallCandidate>();
        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i]) continue;
            var main = segs[i]; used[i] = true;
            var dir = main.Direction;
            double lo = 0, hi = main.Length, offMin = 0, offMax = 0;
            // gather parallel lines in the same band (the screen's two faces, frames, pieces end to end)
            for (bool grew = true; grew;)
            {
                grew = false;
                for (int j = 0; j < segs.Count; j++)
                {
                    if (used[j]) continue;
                    var s = segs[j];
                    if (SegmentCleaner.AngleDiffDeg(s.AngleDeg, main.AngleDeg) > 3) continue;
                    double oa = SegmentCleaner.Offset(main, s.A), ob = SegmentCleaner.Offset(main, s.B);
                    double o0 = Math.Min(oa, ob), o1 = Math.Max(oa, ob);
                    if (Math.Max(o1, offMax) - Math.Min(o0, offMin) > bandWidth) continue;
                    double a0 = Math.Min(SegmentCleaner.Along(main, s.A), SegmentCleaner.Along(main, s.B)), a1 = Math.Max(SegmentCleaner.Along(main, s.A), SegmentCleaner.Along(main, s.B));
                    if (a0 > hi + 100 || a1 < lo - 100) continue;
                    used[j] = true; grew = true;
                    lo = Math.Min(lo, a0); hi = Math.Max(hi, a1); offMin = Math.Min(offMin, o0); offMax = Math.Max(offMax, o1);
                }
            }
            if (hi - lo < minLength) continue;
            var n = dir.Perp();
            double mid = (offMin + offMax) / 2;
            // offsets are measured along the line's left normal (Perp)
            var a = main.A + dir * lo + n * mid;
            var b = main.A + dir * hi + n * mid;
            var c = new Segment2(a, b, main.Layer, main.Layer);
            // already a wall here (the reader paired the screen's faces, or a wall runs along it)
            int steps = Math.Max(2, (int)(c.Length / 100)), inside = 0;
            for (int k = 0; k <= steps; k++)
            {
                var p = c.A + (c.B - c.A) * ((double)k / steps);
                if (walls.Any(w => w.Arc == null && SegmentCleaner.AngleDiffDeg(w.Centerline.AngleDeg, c.AngleDeg) <= 10
                                   && Math.Abs(WallGeometry.Offset(w, p)) <= w.TypeThickness / 2 + 60
                                   && WallGeometry.Along(w, p) >= -50 && WallGeometry.Along(w, p) <= w.Centerline.Length + 50)) inside++;
            }
            if (inside > 0.3 * (steps + 1)) continue;
            double thick = Math.Max(50, offMax - offMin);
            result.Add(new WallCandidate(c, thick, 100, Confidence.Medium, main.Layer, main.Layer) { Curtain = true });
        }
        return result;
    }

    /// <summary>Curtain walls are cut where a door's wall crosses them, so the door's wall stands in the gap.</summary>
    public static List<WallCandidate> CutForDoors(List<WallCandidate> walls, IReadOnlyList<WallCandidate> doorWalls)
    {
        var result = new List<WallCandidate>();
        foreach (var w in walls)
        {
            if (!w.Curtain || w.Arc != null) { result.Add(w); continue; }
            var cuts = doorWalls.Where(d => SegmentCleaner.AngleDiffDeg(d.Centerline.AngleDeg, w.Centerline.AngleDeg) <= 10
                                            && Math.Abs(SegmentCleaner.Offset(w.Centerline, d.Centerline.Mid)) <= 200)
                .Select(d => (A: SegmentCleaner.Along(w.Centerline, d.Centerline.A), B: SegmentCleaner.Along(w.Centerline, d.Centerline.B)))
                .Select(x => (Lo: Math.Min(x.A, x.B), Hi: Math.Max(x.A, x.B)))
                .Where(x => x.Hi > 0 && x.Lo < w.Centerline.Length).OrderBy(x => x.Lo).ToList();
            if (cuts.Count == 0) { result.Add(w); continue; }
            double at = 0; var c = w.Centerline;
            foreach (var (lo, hi) in cuts.Append((c.Length, c.Length)))
            {
                if (lo - at >= 300) result.Add(w with { Centerline = c with { A = c.A + c.Direction * at, B = c.A + c.Direction * lo } });
                at = Math.Max(at, hi);
            }
        }
        return result;
    }
}
