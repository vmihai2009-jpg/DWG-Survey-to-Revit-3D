namespace SurveyToRevit.Core;

/// <summary>
/// Curves drawn as many short straight lines. Surveys often draw a curved wall or a bow as a chain of short lines; read
/// as they are, they become a staircase of short walls that nobody can edit as a curve. This finds chains of short lines
/// joined end to end, turning the same way a little at each joint, that stay on one circle, and gives one arc for each
/// (so <see cref="CurvedWalls"/> builds one curved wall). A T-junction or a corner ends a chain; zigzags and straight
/// runs cut into pieces are not curves.
/// </summary>
public static class ArcChains
{
    public static List<Arc2> Find(IReadOnlyList<Segment2> lines, double tolMm = 5, double maxPieceMm = 2000, double joinMm = 2,
        int minPieces = 3, double minTurnDeg = 0.1, double maxTurnDeg = 25, double minSweepDeg = 10, double minRadiusMm = 1000, double maxRadiusMm = 200000)
    {
        var res = new List<Arc2>();
        var cand = new HashSet<int>(Enumerable.Range(0, lines.Count).Where(i => lines[i].Length >= 1 && lines[i].Length <= maxPieceMm));
        if (cand.Count < minPieces) return res;

        var cell = new Dictionary<(long, long), List<(int piece, int end)>>();
        (long, long) Key(Vec2 p) => ((long)Math.Floor(p.X / joinMm), (long)Math.Floor(p.Y / joinMm));
        Vec2 End(int i, int e) => e == 0 ? lines[i].A : lines[i].B;
        for (int i = 0; i < lines.Count; i++)
            for (int e = 0; e < 2; e++)
            {
                var k = Key(End(i, e));
                if (!cell.TryGetValue(k, out var l)) cell[k] = l = new List<(int, int)>();
                l.Add((i, e));
            }
        List<(int piece, int end)> At(int i, int e)
        {
            var p = End(i, e); var (kx, ky) = Key(p); var found = new List<(int, int)>();
            for (long x = kx - 1; x <= kx + 1; x++)
                for (long y = ky - 1; y <= ky + 1; y++)
                    if (cell.TryGetValue((x, y), out var l))
                        foreach (var q in l) if (q.piece != i && End(q.piece, q.end).DistanceTo(p) <= joinMm) found.Add(q);
            return found;
        }
        (int piece, int end)? Next(int i, int e) { var n = At(i, e); return n.Count == 1 && cand.Contains(n[0].piece) ? n[0] : null; }

        var used = new HashSet<int>();
        var chains = new List<List<Vec2>>();
        void Walk(int start, bool fwd)
        {
            var pts = new List<Vec2> { fwd ? lines[start].A : lines[start].B };
            int i = start; bool f = fwd;
            while (used.Add(i))
            {
                pts.Add(f ? lines[i].B : lines[i].A);
                var nx = Next(i, f ? 1 : 0);
                if (nx == null) break;
                i = nx.Value.piece; f = nx.Value.end == 0;
            }
            if (pts.Count > minPieces) chains.Add(pts);
        }
        foreach (var i in cand.OrderBy(x => x))
            if (!used.Contains(i))
            {
                if (Next(i, 0) == null) Walk(i, true);
                else if (Next(i, 1) == null) Walk(i, false);
            }
        foreach (var i in cand.OrderBy(x => x)) if (!used.Contains(i)) Walk(i, true);

        double minTurn = minTurnDeg * Math.PI / 180, maxTurn = maxTurnDeg * Math.PI / 180;
        foreach (var pts in chains)
        {
            var turn = new double[pts.Count];
            for (int j = 1; j + 1 < pts.Count; j++)
            {
                double a1 = Math.Atan2(pts[j].Y - pts[j - 1].Y, pts[j].X - pts[j - 1].X), a2 = Math.Atan2(pts[j + 1].Y - pts[j].Y, pts[j + 1].X - pts[j].X);
                double d = a2 - a1; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                turn[j] = d;
            }
            bool Bends(int j, int sign) => Math.Abs(turn[j]) >= minTurn && Math.Abs(turn[j]) <= maxTurn && Math.Sign(turn[j]) == sign;
            int s = 0;
            while (s + minPieces < pts.Count)
            {
                int sign = Math.Sign(turn[s + 1]);
                if (sign == 0 || !Bends(s + 1, sign)) { s++; continue; }
                Arc2? best = null; int bestE = -1;
                for (int e = s + 2; e < pts.Count; e++)
                {
                    if (e - 1 > s + 1 && !Bends(e - 1, sign)) break;
                    if (e - s < minPieces) continue;
                    var fit = Fit(pts.GetRange(s, e - s + 1), sign, tolMm);
                    if (fit == null) break;
                    best = fit; bestE = e;
                }
                if (best != null && best.Sweep * 180 / Math.PI >= minSweepDeg && best.Radius >= minRadiusMm && best.Radius <= maxRadiusMm)
                { res.Add(best); s = bestE; }
                else s++;
            }
        }
        return res;
    }

    /// <summary>The circle through the points (least squares), as an anticlockwise arc, or null if a point is more than tol off it.</summary>
    public static Arc2? Fit(IReadOnlyList<Vec2> pts, int sign, double tolMm)
    {
        if (pts.Count < 3) return null;
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
        foreach (var p in pts)
        {
            double u = p.X - mx, v = p.Y - my;
            suu += u * u; svv += v * v; suv += u * v; suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
        }
        double det = suu * svv - suv * suv;
        if (Math.Abs(det) < 1e-6 * (suu * svv + 1)) return null;
        double bu = 0.5 * (suuu + suvv), bv = 0.5 * (svvv + svuu);
        var c = new Vec2((bu * svv - bv * suv) / det + mx, (suu * bv - suv * bu) / det + my);
        double r = pts.Average(p => p.DistanceTo(c));
        if (pts.Any(p => Math.Abs(p.DistanceTo(c) - r) > tolMm)) return null;
        double sweep = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double d = Math.Atan2(pts[i].Y - c.Y, pts[i].X - c.X) - Math.Atan2(pts[i - 1].Y - c.Y, pts[i - 1].X - c.X);
            while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
            sweep += d;
        }
        if (Math.Sign(sweep) != sign || Math.Abs(sweep) >= 2 * Math.PI - 1e-6) return null;
        double a0 = Math.Atan2(pts[0].Y - c.Y, pts[0].X - c.X);
        double start = sweep > 0 ? a0 : a0 + sweep;
        return new Arc2(c, r, start, start + Math.Abs(sweep), "chain");
    }
}
