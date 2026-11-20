namespace SurveyToRevit.Core.Survey;

/// <summary>
/// An opening is as wide as the gap the survey draws in its wall, jamb to jamb, not as its door leaf or window frame. A door
/// read from its symbol is the leaf's width (hinge to swing end), a window its frame lines; the wall's face lines stop at
/// the jambs, usually 50-150 mm outside them (the frame and reveal). For each window and door: the ends of the host wall's
/// face lines (parallel, within the wall's band) just outside each side of the opening are its jambs; where both sides
/// have one, the opening takes the drawn gap (centre and width). Nothing changes where a face runs straight through, only
/// one side is drawn, or the gap is far from the opening read. Deterministic, any survey.
/// </summary>
public static class OpeningJambs
{
    public const double OutMm = 300, InMm = 150, MaxWidenMm = 500, MinShrink = 0.85;

    public static (int Doors, int Windows) Fit(SurveyLevel level, IReadOnlyList<Segment2> faceLines)
    {
        var walls = level.Walls.Walls;
        int nd = 0, nw = 0;
        (Vec2 Centre, double Width)? Gap(int host, Vec2 c, double w)
        {
            if (host < 0 || host >= walls.Count) return null;
            var h = walls[host];
            if (h.Arc != null || h.Curtain) return null;
            var d = h.Centerline.Direction; var n = d.Perp(); double band = h.TypeThickness / 2 + 80;
            double? left = null, right = null;
            foreach (var l in faceLines)
            {
                if (l.Length < 80 || SegmentCleaner.AngleDiffDeg(l.AngleDeg, h.Centerline.AngleDeg) > 5) continue;
                double va = (l.A - c).Dot(n), vb = (l.B - c).Dot(n);
                if (Math.Abs(va) > band || Math.Abs(vb) > band) continue;
                double ua = (l.A - c).Dot(d), ub = (l.B - c).Dot(d), lo = Math.Min(ua, ub), hi = Math.Max(ua, ub);
                // a line across the opening alone is its threshold or head; a face running on well past both sides: no gap drawn
                if (lo < -w / 4 && hi > w / 4)
                {
                    if (lo >= -w / 2 - OutMm && hi <= w / 2 + OutMm) continue;
                    return null;
                }
                if (hi <= -w / 2 + InMm && hi >= -w / 2 - OutMm && lo < hi - 50) left = left is double x ? Math.Max(x, hi) : hi;
                if (lo >= w / 2 - InMm && lo <= w / 2 + OutMm && hi > lo + 50) right = right is double y ? Math.Min(y, lo) : lo;
            }
            if (left is not double L || right is not double R) return null;
            double width = Math.Round((R - L) / 10) * 10;
            if (width < 400 || width < MinShrink * w || width > w + MaxWidenMm) return null;
            if (Math.Abs(width - w) < 10 && Math.Abs((L + R) / 2) < 10) return null;
            return (c + d * ((L + R) / 2), width);
        }
        level.Doors = level.Doors.Select(o =>
        {
            if (Gap(o.HostIndex, o.Center, o.WidthMm) is not { } g) return o;
            nd++; var shift = g.Centre - o.Center;
            return o with { Center = g.Centre, WidthMm = g.Width, Hinge = o.Hinge + shift, Swing = o.Swing + shift };
        }).ToList();
        level.Windows = level.Windows.Select(o =>
        {
            if (Gap(o.HostIndex, o.Center, o.WidthMm) is not { } g) return o;
            nw++;
            return o with { Center = g.Centre, WidthMm = g.Width };
        }).ToList();
        return (nd, nw);
    }
}
