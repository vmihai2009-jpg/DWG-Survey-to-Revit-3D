namespace SurveyToRevit.Core;

/// <summary>
/// Fits the walls to the wall material the drawing shows, so the model has one wall where the
/// survey has one mass of masonry. Surveys often draw a thick wall with extra lines inside it
/// (a plinth or cornice line, the back of window reveals), and the line-pair reader then finds a
/// thin wall on one face and nothing for the rest. Here each wall is widened to the material
/// that runs alongside it, and walls left inside another wall, or overlapping one side by side,
/// are removed or cut back, so no two walls occupy the same place. Same rules for any survey.
/// </summary>
public static class WallFit
{
    public static List<WallCandidate> Fit(List<WallCandidate> walls, Func<Vec2, bool>? isMaterial, Polygon2? outline, Tolerances tol)
    {
        var result = isMaterial == null || outline == null ? walls : walls.Select(w => Widen(w, isMaterial, outline, tol)).ToList();
        return RemoveOverlaps(result, tol);
    }

    /// <summary>
    /// Widens an outside wall to the material along both its faces (inside the building closed
    /// pockets such as WC cubicles can read as material, so only façades are widened): the material depth is sampled
    /// every 100 mm (away from the ends, where cross walls join) and the median is taken, so
    /// window reveals and the odd notch do not count.
    /// </summary>
    static WallCandidate Widen(WallCandidate w, Func<Vec2, bool> isMaterial, Polygon2 outline, Tolerances tol, double step = 15, double minExtra = 40)
    {
        if (w.Arc != null || w.Curtain || w.Centerline.Length < 800) return w;
        var c = w.Centerline;
        var n = c.Direction.Perp();
        if (!IsFacade(c.Mid, n, w.TypeThickness / 2, outline)) return w;
        double half = w.TypeThickness / 2;
        double room = tol.MaxWallThickness - w.TypeThickness;
        if (room < minExtra) return w;
        var extra = new double[2];
        for (int side = 0; side < 2; side++)
        {
            double sign = side == 0 ? 1 : -1;
            var depths = new List<double>();
            for (double t = 300; t <= c.Length - 300; t += 100)
            {
                var foot = c.A + c.Direction * t;
                double d = 0;
                while (d < room && isMaterial(foot + n * (sign * (half + d + step)))) d += step;
                depths.Add(d);
            }
            if (depths.Count < 3) return w;
            depths.Sort();
            double median = depths[depths.Count / 2];
            if (median >= minExtra) extra[side] = median;
        }
        if (extra[0] == 0 && extra[1] == 0) return w;
        if (extra[0] + extra[1] > room) return w;
        double thick = w.TypeThickness + extra[0] + extra[1];
        // a short wall at a corner reads the corner's whole mass as its depth: left as drawn
        if (thick > 0.5 * c.Length) return w;
        double type = WallDetector.SnapThickness(thick, tol.WallTypeSizes);
        var shift = n * ((extra[0] - extra[1]) / 2);
        return w with { Centerline = c with { A = c.A + shift, B = c.B + shift }, MeasuredThickness = thick, TypeThickness = type };
    }

    /// <summary>
    /// Walls side by side in the same place: a wall whose centre line runs inside another
    /// parallel wall is cut back to the part outside it (dropped if little is left). The thicker
    /// wall wins, then the longer.
    /// </summary>
    public static List<WallCandidate> RemoveOverlaps(List<WallCandidate> walls, Tolerances tol, double minPiece = 200)
    {
        var order = walls.Select((w, i) => (w, i)).OrderByDescending(x => x.w.TypeThickness).ThenByDescending(x => x.w.Length).ToList();
        var kept = new List<WallCandidate>();
        foreach (var (w, _) in order)
        {
            if (w.Arc != null) { kept.Add(w); continue; }
            var c = w.Centerline;
            var covers = kept.Where(k => k.Arc == null && SegmentCleaner.AngleDiffDeg(k.Centerline.AngleDeg, c.AngleDeg) <= 8).ToList();
            // the stretches of this wall's centre line not inside a kept parallel wall
            int steps = Math.Max(2, (int)Math.Ceiling(c.Length / 50));
            var free = new bool[steps + 1];
            for (int s = 0; s <= steps; s++)
            {
                var p = c.A + (c.B - c.A) * ((double)s / steps);
                free[s] = !covers.Any(k => Inside(k, p));
            }
            if (free.All(f => f))
            {
                // a short piece lying across inside another wall (a reveal or nib drawn within it)
                int inOther = Enumerable.Range(0, steps + 1).Count(s => kept.Any(k => k.Arc == null && Inside(k, c.A + (c.B - c.A) * ((double)s / steps))));
                if (inOther < 0.8 * (steps + 1)) kept.Add(w);
                continue;
            }
            int start = -1;
            for (int s = 0; s <= steps + 1; s++)
            {
                bool f = s <= steps && free[s];
                if (f && start < 0) start = s;
                if (!f && start >= 0)
                {
                    var a = c.A + (c.B - c.A) * ((double)start / steps);
                    var b = c.A + (c.B - c.A) * ((double)(s - 1) / steps);
                    // run on to the covering wall's face so the pieces still meet it
                    if (start > 0) a -= c.Direction * (c.Length / steps);
                    if (s - 1 < steps) b += c.Direction * (c.Length / steps);
                    if (a.DistanceTo(b) >= minPiece) kept.Add(w with { Centerline = c with { A = a, B = b } });
                    start = -1;
                }
            }
        }
        return kept;
    }

    // the outline's edge lies within 1.5 m of the wall on one side only
    static bool IsFacade(Vec2 mid, Vec2 n, double half, Polygon2 outline)
    {
        bool plus = false, minus = false;
        for (double d = half + 100; d <= half + 1500; d += 100)
        {
            plus |= !FacadeWalls.Contains(outline, mid + n * d);
            minus |= !FacadeWalls.Contains(outline, mid - n * d);
        }
        return plus != minus;
    }

    static bool Inside(WallCandidate w, Vec2 p)
    {
        double along = WallGeometry.Along(w, p), len = WallGeometry.Length(w);
        return along > 1 && along < len - 1 && Math.Abs(WallGeometry.Offset(w, p)) < w.TypeThickness / 2 - 5;
    }
}
