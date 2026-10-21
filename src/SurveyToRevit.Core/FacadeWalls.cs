namespace SurveyToRevit.Core;

/// <summary>
/// Old façades read as many short pieces: piers between windows, thinner
/// panels under sills, pilasters. For a concept model each straight run of
/// façade becomes one wall. A façade piece is a wall with the outside of the
/// building just beyond one face. Pieces facing the same way whose outer faces
/// line up (within <c>faceSlack</c>) and follow on with gaps no wider than an
/// opening become one wall: outer face at the length-weighted median position,
/// the thickest thickness found along a quarter of the run or more, running from the first piece to the last.
/// </summary>
public static class FacadeWalls
{
    public static List<WallCandidate> Consolidate(List<WallCandidate> walls, Polygon2? outline, Tolerances tol, double faceSlack = 300)
    {
        if (outline == null || outline.Vertices.Count < 3) return walls;

        // Façade pieces, with the outward normal and the outer face's offset along it.
        var facade = new List<(int I, Vec2 N, double Face, double S0, double S1)>();
        for (int i = 0; i < walls.Count; i++)
        {
            var w = walls[i];
            var c = w.Centerline;
            var n = c.Direction.Perp();
            double probe = w.MeasuredThickness / 2 + 300;
            bool leftOut = !Contains(outline, c.Mid + n * probe), rightOut = !Contains(outline, c.Mid - n * probe);
            if (leftOut == rightOut) continue; // inside the building, or a free-standing scrap
            if (rightOut) n = n * -1;
            // canonical direction so pieces of one run share an axis: along = n rotated clockwise
            var u = new Vec2(n.Y, -n.X);
            double face = n.Dot(c.Mid) + w.MeasuredThickness / 2;
            double a = u.Dot(c.A), b = u.Dot(c.B);
            facade.Add((i, n, face, Math.Min(a, b), Math.Max(a, b)));
        }

        var taken = new bool[walls.Count];
        var result = new List<WallCandidate>();
        // Group by facing direction, then chain along the run.
        var used = new bool[facade.Count];
        for (int k = 0; k < facade.Count; k++)
        {
            if (used[k]) continue;
            var seed = facade[k];
            var group = new List<int> { k };
            used[k] = true;
            // grow: same facing, outer face within slack of the group's, overlapping or within an opening's width
            bool grew = true;
            while (grew)
            {
                grew = false;
                double lo = group.Min(g => facade[g].S0), hi = group.Max(g => facade[g].S1);
                double face = WeightedMedian(group.Select(g => (facade[g].Face, walls[facade[g].I].Length)));
                for (int m = 0; m < facade.Count; m++)
                {
                    if (used[m]) continue;
                    var f = facade[m];
                    if (SegmentCleaner.AngleDiffDeg(Angle(f.N), Angle(seed.N)) > tol.AngleSnapDeg || f.N.Dot(seed.N) < 0) continue;
                    if (Math.Abs(f.Face - face) > faceSlack) continue;
                    if (f.S0 > hi + tol.MaxOpeningWidth || f.S1 < lo - tol.MaxOpeningWidth) continue;
                    group.Add(m); used[m] = true; grew = true;
                }
            }
            if (group.Count < 2) continue;

            double from = group.Min(g => facade[g].S0), to = group.Max(g => facade[g].S1);
            double outer = WeightedMedian(group.Select(g => (facade[g].Face, walls[facade[g].I].Length)));
            // The masonry, not the thin panels under windows: the thickest type found along at least a quarter of the run.
            double total = group.Sum(g => walls[facade[g].I].Length);
            double t = group.GroupBy(g => walls[facade[g].I].TypeThickness)
                .Where(x => x.Sum(g => walls[facade[g].I].Length) >= 0.25 * total)
                .Select(x => x.Key).DefaultIfEmpty(group.Max(g => walls[facade[g].I].TypeThickness)).Max();
            var nn = seed.N;
            var uu = new Vec2(nn.Y, -nn.X);
            double mid = outer - t / 2;
            var center = new Segment2(uu * from + nn * mid, uu * to + nn * mid, "facade", "facade");
            foreach (var g in group) taken[facade[g].I] = true;
            result.Add(new WallCandidate(center, t, t, Confidence.Medium, "facade", "facade"));
        }
        for (int i = 0; i < walls.Count; i++)
            if (!taken[i]) result.Add(walls[i]);
        return result;
    }

    static double Angle(Vec2 v) => (Math.Atan2(v.Y, v.X) * 180 / Math.PI + 360) % 180;

    static double WeightedMedian(IEnumerable<(double Value, double Weight)> items)
    {
        var list = items.OrderBy(x => x.Value).ToList();
        double half = list.Sum(x => x.Weight) / 2, acc = 0;
        foreach (var (v, w) in list) { acc += w; if (acc >= half) return v; }
        return list[^1].Value;
    }

    /// <summary>Point in polygon (even-odd rule).</summary>
    public static bool Contains(Polygon2 poly, Vec2 p)
    {
        var v = poly.Vertices;
        bool inside = false;
        for (int i = 0, j = v.Count - 1; i < v.Count; j = i++)
            if ((v[i].Y > p.Y) != (v[j].Y > p.Y) &&
                p.X < (v[j].X - v[i].X) * (p.Y - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X)
                inside = !inside;
        return inside;
    }
}
