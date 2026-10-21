namespace SurveyToRevit.Core;

/// <summary>
/// Cleans raw survey linework: drops tiny pieces, and merges duplicate,
/// overlapping or slightly broken collinear lines into single lines.
/// </summary>
public static class SegmentCleaner
{
    public static List<Segment2> Clean(IEnumerable<Segment2> input, Tolerances tol)
    {
        var segs = input.Where(s => s.Length >= tol.MinSegmentLength).ToList();
        return MergeCollinear(segs, tol.ParallelAngleDeg, tol.CollinearOffset, tol.JoinGap);
    }

    public static List<Segment2> MergeCollinear(IReadOnlyList<Segment2> segs, double angleTolDeg, double offsetTol, double gapTol)
    {
        // O(n²) grouping is fine for plan-sized inputs; add a spatial index if large drawings get slow.
        var uf = new UnionFind(segs.Count);
        for (int i = 0; i < segs.Count; i++)
            for (int j = i + 1; j < segs.Count; j++)
                if (AreCollinearAndTouching(segs[i], segs[j], angleTolDeg, offsetTol, gapTol))
                    uf.Union(i, j);

        var result = new List<Segment2>();
        foreach (var group in Enumerable.Range(0, segs.Count).GroupBy(uf.Find))
        {
            var members = group.Select(k => segs[k]).ToList();
            result.Add(members.Count == 1 ? members[0] : MergeGroup(members));
        }
        return result;
    }

    public static double AngleDiffDeg(double a, double b)
    {
        var d = Math.Abs(a - b) % 180.0;
        return Math.Min(d, 180.0 - d);
    }

    /// <summary>Signed perpendicular distance of point p from the infinite line through s.</summary>
    public static double Offset(Segment2 s, Vec2 p) => s.Direction.Cross(p - s.A);

    /// <summary>Position of point p along segment s's axis, measured from s.A.</summary>
    public static double Along(Segment2 s, Vec2 p) => s.Direction.Dot(p - s.A);

    static bool AreCollinearAndTouching(Segment2 a, Segment2 b, double angleTol, double offsetTol, double gapTol)
    {
        if (AngleDiffDeg(a.AngleDeg, b.AngleDeg) > angleTol) return false;
        if (Math.Abs(Offset(a, b.A)) > offsetTol || Math.Abs(Offset(a, b.B)) > offsetTol) return false;
        double b0 = Along(a, b.A), b1 = Along(a, b.B);
        double lo = Math.Min(b0, b1), hi = Math.Max(b0, b1);
        // gap between [0, a.Length] and [lo, hi]
        double gap = Math.Max(lo - a.Length, -hi);
        return gap <= gapTol;
    }

    static Segment2 MergeGroup(List<Segment2> members)
    {
        var axis = members.OrderByDescending(m => m.Length).First();
        double tMin = double.MaxValue, tMax = double.MinValue, offSum = 0, wSum = 0;
        foreach (var m in members)
        {
            foreach (var p in new[] { m.A, m.B })
            {
                var t = Along(axis, p);
                tMin = Math.Min(tMin, t);
                tMax = Math.Max(tMax, t);
            }
            offSum += (Offset(axis, m.A) + Offset(axis, m.B)) * 0.5 * m.Length;
            wSum += m.Length;
        }
        var u = axis.Direction;
        var shift = u.Perp() * (offSum / wSum);
        var sources = string.Join(";", members.Select(m => m.Source).Where(s => s.Length > 0).Distinct());
        return new Segment2(axis.A + u * tMin + shift, axis.A + u * tMax + shift, axis.Layer, sources);
    }

    sealed class UnionFind
    {
        readonly int[] _p;
        public UnionFind(int n) { _p = Enumerable.Range(0, n).ToArray(); }
        public int Find(int x) { while (_p[x] != x) x = _p[x] = _p[_p[x]]; return x; }
        public void Union(int a, int b) { _p[Find(a)] = Find(b); }
    }
}
