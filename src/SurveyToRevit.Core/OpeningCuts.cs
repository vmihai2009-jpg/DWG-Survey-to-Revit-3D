namespace SurveyToRevit.Core;

/// <summary>
/// Where a window or door also has to be cut into walls other than its host: a lining or second skin
/// drawn as its own wall in front of or behind it, or the next wall along when the opening runs past
/// the end of its host. Each cut is only the part of the opening that overlaps that wall.
/// </summary>
public static class OpeningCuts
{
    /// <summary>Overlaps shorter than this are left (a wall end just touching a jamb).</summary>
    const double MinOverlapMm = 30;

    public static IEnumerable<(int Wall, Vec2 Center, double WidthMm)> Across(IReadOnlyList<WallCandidate> walls, int host, Vec2 center, double widthMm)
    {
        var h = walls[host];
        if (h.Arc != null) yield break;
        for (int k = 0; k < walls.Count; k++)
        {
            var w = walls[k];
            if (k == host || w.Arc != null || w.Curtain) continue;
            var c = w.Centerline;
            if (SegmentCleaner.AngleDiffDeg(c.AngleDeg, h.Centerline.AngleDeg) > 5) continue;
            if (Math.Abs(SegmentCleaner.Offset(c, center)) > (h.TypeThickness + w.TypeThickness) / 2 + 100) continue;
            double along = SegmentCleaner.Along(c, center);
            double from = Math.Max(0, along - widthMm / 2), to = Math.Min(c.Length, along + widthMm / 2);
            if (to - from < MinOverlapMm) continue;
            var mid = c.A + c.Direction * ((from + to) / 2);
            // on the wall's own line, level with the opening
            yield return (k, mid, to - from);
        }
    }

    /// <summary>
    /// Short walls running across an opening, through the depth of the outside wall: the window's reveals
    /// read as the two faces of a wall, which fills the opening. A window or door never sits inside another
    /// wall, so these are dropped. Walls that host an opening themselves are kept.
    /// </summary>
    public static HashSet<int> FillingOpenings(IReadOnlyList<WallCandidate> walls, IEnumerable<(int Host, Vec2 Center, double WidthMm)> openings)
    {
        var list = openings.ToList();
        var hosts = list.Select(o => o.Host).ToHashSet();
        var drop = new HashSet<int>();
        foreach (var (host, center, width) in list)
        {
            var h = walls[host];
            if (h.Arc != null) continue;
            var dir = h.Centerline.Direction;
            for (int k = 0; k < walls.Count; k++)
            {
                var w = walls[k];
                if (k == host || hosts.Contains(k) || w.Arc != null || w.Curtain || w.Length > MaxRevealWallMm) continue;
                if (SegmentCleaner.AngleDiffDeg(w.Centerline.AngleDeg, h.Centerline.AngleDeg) < 45) continue;
                // the middle half of the opening, on the host's centre line
                for (double t = -width / 4; t <= width / 4 + 1; t += Math.Max(10, width / 8))
                {
                    var p = center + dir * t;
                    double along = SegmentCleaner.Along(w.Centerline, p);
                    if (along > 0 && along < w.Centerline.Length && Math.Abs(SegmentCleaner.Offset(w.Centerline, p)) < w.TypeThickness / 2 - 10)
                    { drop.Add(k); break; }
                }
            }
        }
        return drop;
    }

    const double MaxRevealWallMm = 1500;

    /// <summary>
    /// Walls standing between windows and the outside: a window must look out, so a wall drawn on the
    /// outside of it, covering it, cannot be full height. What the survey cuts there is a balustrade,
    /// a parapet or the outer face of a deep reveal. Returns each such wall with the lowest sill of the
    /// windows it stands in front of. Walls that hold a window or door themselves are never included.
    /// </summary>
    public static Dictionary<int, double> InFront(IReadOnlyList<WallCandidate> walls,
        IReadOnlyList<(int Host, Vec2 Center, double WidthMm, double SillMm)> windows, Polygon2? outline, IEnumerable<int>? otherHosts = null)
        => InFront(walls, windows, outline, otherHosts, out _);

    /// <summary>
    /// As above; <paramref name="through"/> also lists, per window (by its place in <paramref name="windows"/>), the
    /// façade walls outside it that hold other windows themselves: a real outer wall, so the window is set in the wrong
    /// (inner) wall and must be cut through that façade too, at the point on it in front of the window.
    /// </summary>
    public static Dictionary<int, double> InFront(IReadOnlyList<WallCandidate> walls,
        IReadOnlyList<(int Host, Vec2 Center, double WidthMm, double SillMm)> windows, Polygon2? outline, IEnumerable<int>? otherHosts,
        out List<(int Window, int Wall, Vec2 At)> through)
    {
        var result = new Dictionary<int, double>();
        through = new();
        if (outline == null) return result;
        var hosts = windows.Select(w => w.Host).Concat(otherHosts ?? Enumerable.Empty<int>()).ToHashSet();
        for (int wi = 0; wi < windows.Count; wi++)
        {
            var (host, center, width, sill) = windows[wi];
            var h = walls[host];
            double along = WallGeometry.Along(h, center);
            double a = WallGeometry.AngleDegAt(h, along) * Math.PI / 180;
            var n = new Vec2(-Math.Sin(a), Math.Cos(a));
            foreach (var side in new[] { n, n * -1 })
            {
                var votes = new Dictionary<int, int>();
                foreach (var f in new[] { -0.35, -0.15, 0, 0.15, 0.35 })
                {
                    var p0 = WallGeometry.PointAt(h, along + f * width);
                    for (double d = h.TypeThickness / 2 + 20; d <= h.TypeThickness / 2 + 2000; d += 20)
                    {
                        var q = p0 + side * d;
                        int k = FirstBody(walls, q, host);
                        if (k < 0) continue;
                        // only a wall whose far side is outside the building stands between the window and the view
                        var far = q + side * (walls[k].TypeThickness + 400);
                        if (!walls[k].Curtain && !FacadeWalls.Contains(outline, far))
                            votes[k] = votes.GetValueOrDefault(k) + 1;
                        break;
                    }
                }
                foreach (var (k, v) in votes)
                {
                    if (v < 3) continue;
                    if (hosts.Contains(k)) through.Add((wi, k, WallGeometry.PointAt(walls[k], Math.Clamp(WallGeometry.Along(walls[k], center), 0, WallGeometry.Length(walls[k])))));
                    else result[k] = Math.Min(result.GetValueOrDefault(k, double.MaxValue), sill);
                }
            }
        }
        return result;
    }

    static int FirstBody(IReadOnlyList<WallCandidate> walls, Vec2 p, int skip)
    {
        for (int k = 0; k < walls.Count; k++)
        {
            if (k == skip) continue;
            var w = walls[k];
            double along = WallGeometry.Along(w, p);
            if (along >= 0 && along <= WallGeometry.Length(w) && Math.Abs(WallGeometry.Offset(w, p)) <= w.TypeThickness / 2) return k;
        }
        return -1;
    }
}
