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
}
