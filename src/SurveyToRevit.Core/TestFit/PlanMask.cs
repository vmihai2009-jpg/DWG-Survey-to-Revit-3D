namespace SurveyToRevit.Core.TestFit;

/// <summary>
/// What a plan view actually shows of its linework: lines inside its crop, less what opaque filled and masking
/// regions drawn in the view cover. A region is a set of loops read even-odd (its holes stay visible).
/// </summary>
public static class PlanMask
{
    /// <summary>Whether a point is inside a region (even-odd over all its loops).</summary>
    public static bool Inside(IReadOnlyList<IReadOnlyList<Vec2>> region, Vec2 p)
    {
        var c = false;
        foreach (var l in region)
            for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                if ((l[i].Y > p.Y) != (l[j].Y > p.Y) && p.X < (l[j].X - l[i].X) * (p.Y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X) c = !c;
        return c;
    }

    /// <summary>
    /// The visible pieces of a polyline: inside the crop (when there is one) and outside every mask.
    /// </summary>
    public static List<List<Vec2>> Visible(IReadOnlyList<Vec2> line, IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec2>>> masks,
        IReadOnlyList<IReadOnlyList<Vec2>>? crop = null)
    {
        var pieces = new List<List<Vec2>>();
        List<Vec2>? cur = null;
        bool Seen(Vec2 p) => (crop == null || Inside(crop, p)) && !masks.Any(m => Inside(m, p));
        for (var k = 0; k + 1 < line.Count; k++)
        {
            Vec2 a = line[k], b = line[k + 1];
            // cut the segment where it crosses any region's edge, then keep the stretches whose middle is seen
            var ts = new List<double> { 0, 1 };
            foreach (var region in crop == null ? masks : masks.Append(crop))
                foreach (var l in region)
                    for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                        if (Cross(a, b, l[j], l[i]) is { } t) ts.Add(t);
            ts.Sort();
            for (var i = 0; i + 1 < ts.Count; i++)
            {
                if (ts[i + 1] - ts[i] < 1e-9) continue;
                Vec2 p = a + (b - a) * ts[i], q = a + (b - a) * ts[i + 1];
                if (Seen(a + (b - a) * ((ts[i] + ts[i + 1]) / 2)))
                {
                    if (cur == null || cur[^1].DistanceTo(p) > 1e-6) { cur = new List<Vec2> { p }; pieces.Add(cur); }
                    cur.Add(q);
                }
                else cur = null;
            }
        }
        return pieces;
    }

    /// <summary>Where along a→b (0..1) it crosses c→d, if it does.</summary>
    static double? Cross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y, den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return null;
        double t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / den, u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / den;
        return t > 0 && t < 1 && u >= 0 && u <= 1 ? t : null;
    }
}
