namespace SurveyToRevit.Core;

/// <summary>
/// Curved walls. A curved wall is drawn with at least one curved face, but the other face
/// is often not a matching arc: a curved façade has its outer face as arcs and its inner
/// face as straight window recesses and piers. So each arc on a wall layer is measured
/// against the grid's wall material: at points along it, how far wall material runs in
/// from it, and on which side. An arc with wall material along it is a wall face; the wall
/// is that arc moved in by half the thickness. Pieces of one curve (ends within an opening's
/// width, same direction, a circle fits them all) become one wall, at the thickest type found
/// along a quarter of the run or more. Straight walls the reader made along the curve are
/// then dropped. Without a material map, two arcs round one centre a wall-thickness apart are used.
/// </summary>
public static class CurvedWalls
{
    public static List<WallCandidate> Add(List<WallCandidate> walls, IReadOnlyList<Arc2> arcs, Tolerances tol,
        Func<Vec2, bool>? isMaterial = null, Polygon2? outline = null, double minFaceLength = 400)
    {
        var faces = arcs.Where(a => a.Length >= minFaceLength && a.Sweep < 2 * Math.PI - 1e-6).ToList();
        var pieces = new List<Piece>();
        foreach (var a in faces)
        {
            // an arc with a matching arc a wall-thickness away gives the thickness exactly; otherwise measure the material
            var p = Paired(a, arcs, tol) ?? (isMaterial != null ? Measure(a, tol, isMaterial, outline) : null);
            if (p != null) pieces.Add(p);
        }
        if (pieces.Count == 0) return walls;
        // a wall drawn with both faces curved is found from each face: keep the longer
        pieces = pieces.OrderByDescending(p => p.Arc.Length).ToList();
        var kept = new List<Piece>();
        foreach (var p in pieces)
            if (!kept.Any(k => Covers(k, p))) kept.Add(p);

        var curved = new List<WallCandidate>();
        foreach (var run in Runs(kept, tol))
        {
            // short curved bits on their own are column faces and mouldings, not walls
            if (run.Sum(p => p.Arc.Length) < 1500) continue;
            var pts = run.SelectMany(p => Samples(p.Arc, 250)).ToList();
            var arc = run.Count == 1 ? run[0].Arc : FitArc(pts, run);
            if (arc == null) { foreach (var p in run) curved.Add(Wall(p.Arc, p.Measured, p.Type)); continue; }
            double total = run.Sum(p => p.Arc.Length);
            double t = run.GroupBy(p => p.Type).Where(g => g.Sum(p => p.Arc.Length) >= 0.25 * total)
                .Select(g => g.Key).DefaultIfEmpty(run.Max(p => p.Type)).Max();
            curved.Add(Wall(arc, run.Average(p => p.Measured), t));
        }

        // two curved walls overlapping along most of the shorter one (a face drawn twice, a lining on the piers): keep the longer
        var distinct = new List<WallCandidate>();
        foreach (var w in curved.OrderByDescending(w => w.Length * w.TypeThickness))
        {
            var pts = Samples(w.Arc!, 250).ToList();
            bool inside = distinct.Any(k => pts.Count(q => Math.Abs(q.DistanceTo(k.Arc!.Center) - k.Arc.Radius) < (k.TypeThickness + w.TypeThickness) / 2 + 100
                                                         && k.Arc.AngleFromStart(q) <= k.Arc.Sweep + 0.01) >= 0.7 * pts.Count);
            if (!inside) distinct.Add(w);
        }
        curved = distinct;

        // straight pieces lying in a curved wall's band are the reader's chords of it: drop them
        bool InBand(WallCandidate w) => curved.Any(cw =>
        {
            var a = cw.Arc!;
            double reach = cw.TypeThickness / 2 + 150;
            return new[] { w.Centerline.A, w.Centerline.B, w.Centerline.Mid }.All(p =>
                Math.Abs(p.DistanceTo(a.Center) - a.Radius) <= reach && a.AngleFromStart(p) <= a.Sweep + 0.02);
        });
        return walls.Where(w => w.Arc != null || !InBand(w)).Concat(curved).ToList();
    }

    static WallCandidate Wall(Arc2 arc, double measured, double type)
    {
        var chord = new Segment2(arc.PointAt(arc.Start), arc.PointAt(arc.End), "curved", "curved");
        return new WallCandidate(chord, measured, type, Confidence.Medium, "arc", "arc") { Arc = arc with { Layer = "curved" } };
    }

    /// <summary>A curved wall piece: <see cref="Arc"/> is its centre line.</summary>
    sealed record Piece(Arc2 Arc, double Measured, double Type);

    /// <summary>The wall behind a face arc, from how far wall material runs in from it; null if it is not a wall face.</summary>
    static Piece? Measure(Arc2 face, Tolerances tol, Func<Vec2, bool> isMaterial, Polygon2? outline)
    {
        // a façade arc: the wall is on the building side, whatever the grid says about the strip outside
        bool? facadeInward = null;
        if (outline != null)
        {
            var m = face.PointAt(face.Start + face.Sweep / 2);
            var nm = (m - face.Center) * (1 / face.Radius);
            bool inIn = FacadeWalls.Contains(outline, m - nm * 1500), outIn = FacadeWalls.Contains(outline, m + nm * 1500);
            if (inIn != outIn) facadeInward = inIn;
        }
        var inward = new List<double>();
        var outward = new List<double>();
        foreach (var angle in Angles(face, 250))
        {
            var p = face.PointAt(angle);
            var n = (p - face.Center) * (1 / face.Radius); // away from the centre
            inward.Add(Run(p, n * -1, tol, isMaterial));
            outward.Add(Run(p, n, tol, isMaterial));
        }
        int need = Math.Max(2, inward.Count / 2);
        var side = facadeInward is bool fi ? (fi ? inward : outward)
                 : inward.Count(d => d >= tol.MinWallThickness) >= outward.Count(d => d >= tol.MinWallThickness) ? inward : outward;
        // a run that never ends is open floor (a terrace, a room), not a wall
        var found = side.Where(d => d >= tol.MinWallThickness && d < tol.MaxWallThickness).ToList();
        if (found.Count < need) return null;
        // the masonry, not the thin panels under windows: the upper part of the measurements
        found.Sort();
        double measured = found[(int)Math.Round(0.75 * (found.Count - 1))];
        double type = WallDetector.SnapThickness(measured, tol.WallTypeSizes);
        double r = face.Radius + (side == inward ? -type / 2 : type / 2);
        return new Piece(face with { Radius = r }, measured, type);
    }

    /// <summary>How far wall material runs from a point in a direction (mm), up to the thickest wall.</summary>
    static double Run(Vec2 p, Vec2 dir, Tolerances tol, Func<Vec2, bool> isMaterial)
    {
        const double step = 25;
        double d = 30;
        if (!isMaterial(p + dir * d)) return 0;
        while (d < tol.MaxWallThickness && isMaterial(p + dir * (d + step))) d += step;
        return d + step / 2;
    }

    /// <summary>Without a material map: the nearest arc round the same centre a wall thickness away.</summary>
    static Piece? Paired(Arc2 face, IReadOnlyList<Arc2> arcs, Tolerances tol)
    {
        var partner = arcs.Where(b => b != face && b.Center.DistanceTo(face.Center) <= 300 && Overlap(face, b) >= 300)
            .Select(b => (b, gap: b.Radius - face.Radius))
            .Where(x => Math.Abs(x.gap) >= tol.MinWallThickness && Math.Abs(x.gap) <= tol.MaxWallThickness)
            .OrderBy(x => Math.Abs(x.gap)).FirstOrDefault();
        if (partner.b == null) return null;
        double t = Math.Abs(partner.gap), type = WallDetector.SnapThickness(t, tol.WallTypeSizes);
        return new Piece(face with { Radius = face.Radius + Math.Sign(partner.gap) * t / 2 }, t, type);
    }

    /// <summary>Length (mm, on a) over which two arcs round about the same centre face each other.</summary>
    static double Overlap(Arc2 a, Arc2 b)
    {
        double total = 0;
        foreach (var angle in Angles(a, 100))
            if (b.AngleFromStart(a.PointAt(angle)) <= b.Sweep) total += a.Length / Math.Max(2, Math.Ceiling(a.Length / 100));
        return total;
    }

    static bool Covers(Piece k, Piece p) =>
        Samples(p.Arc, 250).All(q => Math.Abs(q.DistanceTo(k.Arc.Center) - k.Arc.Radius) <= k.Type / 2 + 50 && k.Arc.AngleFromStart(q) <= k.Arc.Sweep + 0.01);

    /// <summary>Pieces that follow on from each other along one curve.</summary>
    static List<List<Piece>> Runs(List<Piece> pieces, Tolerances tol)
    {
        var used = new bool[pieces.Count];
        var runs = new List<List<Piece>>();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (used[i]) continue;
            var run = new List<Piece> { pieces[i] };
            used[i] = true;
            for (bool grew = true; grew;)
            {
                grew = false;
                for (int j = 0; j < pieces.Count; j++)
                {
                    if (used[j] || !run.Any(r => FollowsOn(r.Arc, pieces[j].Arc, tol))) continue;
                    var trial = run.Append(pieces[j]).ToList();
                    if (FitArc(trial.SelectMany(p => Samples(p.Arc, 250)).ToList(), trial) == null) continue;
                    run = trial; used[j] = true; grew = true;
                }
            }
            runs.Add(run);
        }
        return runs;
    }

    static bool FollowsOn(Arc2 a, Arc2 b, Tolerances tol)
    {
        foreach (var (pa, ta) in Ends(a))
            foreach (var (pb, tb) in Ends(b))
                if (pa.DistanceTo(pb) <= tol.MaxOpeningWidth && Math.Abs(Math.Sin(ta - tb)) < Math.Sin(15 * Math.PI / 180) && Math.Cos(ta - tb) > 0)
                    return true;
        return false;
    }

    // end points with the curve's direction there (both ends point the same way round)
    static IEnumerable<(Vec2 P, double Tangent)> Ends(Arc2 a)
    {
        yield return (a.PointAt(a.Start), a.Start + Math.PI / 2);
        yield return (a.PointAt(a.End), a.End + Math.PI / 2);
    }

    /// <summary>
    /// One circle through all the points (algebraic least-squares fit), as an arc spanning them;
    /// null if the points are not on one circle within 60 mm, or the curve bends the other way from the pieces.
    /// </summary>
    static Arc2? FitArc(List<Vec2> pts, List<Piece> pieces)
    {
        if (pts.Count < 3) return null;
        double ox = pts.Average(p => p.X), oy = pts.Average(p => p.Y);
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var q in pts)
        {
            double x = q.X - ox, y = q.Y - oy, z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; sxz += x * z; syz += y * z; sz += z;
        }
        int n = pts.Count;
        // solve [sxx sxy sx; sxy syy sy; sx sy n] [D E F] = -[sxz syz sz]
        var m = new double[,] { { sxx, sxy, sx, -sxz }, { sxy, syy, sy, -syz }, { sx, sy, n, -sz } };
        for (int c = 0; c < 3; c++)
        {
            int piv = Enumerable.Range(c, 3 - c).OrderByDescending(r => Math.Abs(m[r, c])).First();
            if (Math.Abs(m[piv, c]) < 1e-9) return null;
            for (int k = 0; k < 4; k++) (m[c, k], m[piv, k]) = (m[piv, k], m[c, k]);
            for (int r = 0; r < 3; r++)
            {
                if (r == c) continue;
                double f = m[r, c] / m[c, c];
                for (int k = 0; k < 4; k++) m[r, k] -= f * m[c, k];
            }
        }
        double D = m[0, 3] / m[0, 0], E = m[1, 3] / m[1, 1], F = m[2, 3] / m[2, 2];
        var center = new Vec2(ox - D / 2, oy - E / 2);
        double r2 = D * D / 4 + E * E / 4 - F;
        if (r2 <= 0) return null;
        double radius = Math.Sqrt(r2);
        if (pts.Any(p => Math.Abs(p.DistanceTo(center) - radius) > 60)) return null;
        // the pieces must bend round this centre the same way they bend round their own
        if (pieces.Any(p => p.Arc.Center.DistanceTo(center) > p.Arc.Radius)) return null;
        var angles = pts.Select(p => Math.Atan2(p.Y - center.Y, p.X - center.X)).ToList();
        var sorted = angles.Select(Arc2.Norm).OrderBy(a => a).ToList();
        double bestGap = -1, start = sorted[0];
        for (int i = 0; i < sorted.Count; i++)
        {
            double next = i + 1 < sorted.Count ? sorted[i + 1] : sorted[0] + 2 * Math.PI;
            if (next - sorted[i] > bestGap) { bestGap = next - sorted[i]; start = Arc2.Norm(next); }
        }
        double sweep = 2 * Math.PI - bestGap;
        return new Arc2(center, Math.Round(radius / 10) * 10, start, start + sweep, "curved");
    }

    static IEnumerable<double> Angles(Arc2 a, double spacing)
    {
        int n = Math.Max(2, (int)Math.Ceiling(a.Length / spacing));
        for (int i = 0; i <= n; i++) yield return a.Start + a.Sweep * (0.05 + 0.9 * i / n);
    }

    static IEnumerable<Vec2> Samples(Arc2 a, double spacing)
    {
        int n = Math.Max(2, (int)Math.Ceiling(a.Length / spacing));
        for (int i = 0; i <= n; i++) yield return a.PointAt(a.Start + a.Sweep * i / n);
    }
}
