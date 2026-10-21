namespace SurveyToRevit.Core;

public enum Confidence { Low, Medium, High }

/// <summary>A wall found in the plan: its centre line and thickness, in mm.</summary>
public sealed record WallCandidate(Segment2 Centerline, double MeasuredThickness, double TypeThickness, Confidence Confidence, string FaceA, string FaceB)
{
    public double Length => Centerline.Length;
    public string TypeName => WallDetector.TypeNameFor(TypeThickness);
}

public sealed record WallDetectionResult(
    IReadOnlyList<WallCandidate> Walls,
    IReadOnlyList<Segment2> UnpairedSegments,
    IReadOnlyList<double> ThicknessGroups,
    int InputSegments,
    int CleanedSegments,
    IReadOnlyList<double> Axes);

/// <summary>
/// Finds walls as pairs of parallel face lines a wall-thickness apart.
/// Each face line can belong to only one wall at any point along its length,
/// so the closest pairs are taken first. A line that pairs with nothing is
/// reported, never turned into a wall.
/// </summary>
public static class WallDetector
{
    /// <param name="openings">Door and window linework, used as evidence to join wall pieces across openings.</param>
    /// <param name="spaceMarkers">Points known to be inside rooms (room names, spot heights). A "wall" containing one is rejected.</param>
    /// <param name="isMaterial">Optional: is this point wall material? A face pair whose middle is not is not a wall.</param>
    public static WallDetectionResult Detect(IEnumerable<Segment2> rawSegments, Tolerances? tolerances = null,
        IReadOnlyList<Segment2>? openings = null, IReadOnlyList<Vec2>? spaceMarkers = null, Func<Vec2, bool>? isMaterial = null)
    {
        var tol = tolerances ?? Tolerances.Default;
        var raw = rawSegments.ToList();
        var axes = AxisSnapper.FindAxes(raw.Where(r => r.Length >= tol.MinSegmentLength).ToList(), tol.AngleSnapDeg);
        var segs = SegmentCleaner.Clean(AxisSnapper.Snap(raw, axes, tol.AngleSnapDeg), tol);
        var claims = segs.Select(_ => new List<(double, double)>()).ToArray();

        var pairs = new List<(int i, int j, double d)>();
        for (int i = 0; i < segs.Count; i++)
            for (int j = i + 1; j < segs.Count; j++)
            {
                if (SegmentCleaner.AngleDiffDeg(segs[i].AngleDeg, segs[j].AngleDeg) > tol.ParallelAngleDeg) continue;
                var d = Math.Abs(SegmentCleaner.Offset(segs[i], segs[j].Mid));
                if (d >= tol.MinWallThickness - tol.CollinearOffset && d <= tol.MaxWallThickness)
                    pairs.Add((i, j, d));
            }

        var pieces = new List<Piece>();
        foreach (var (i, j, d) in pairs.OrderBy(p => p.d))
        {
            Segment2 a = segs[i], b = segs[j];
            double b0 = SegmentCleaner.Along(a, b.A), b1 = SegmentCleaner.Along(a, b.B);
            double lo = Math.Max(0, Math.Min(b0, b1)), hi = Math.Min(a.Length, Math.Max(b0, b1));
            if (hi - lo < tol.MinWallLength) continue;

            // Remove the parts of this overlap already used by a closer pair, on either face.
            var free = Subtract((lo, hi), claims[i]);
            free = free.SelectMany(iv => Subtract(iv, claims[j].Select(c => ToAxis(b, a, c)).ToList())).ToList();

            foreach (var (s, e) in free.Where(iv => iv.Item2 - iv.Item1 >= tol.MinWallLength))
            {
                if (spaceMarkers != null && ContainsMarker(a, b, s, e, spaceMarkers)) continue;
                var u = a.Direction;
                var half = u.Perp() * (SegmentCleaner.Offset(a, b.Mid) * 0.5);
                if (isMaterial != null && !isMaterial(a.A + u * ((s + e) / 2) + half)) continue;
                claims[i].Add((s, e));
                claims[j].Add(ToAxis(a, b, (s, e)));
                var center = new Segment2(a.A + u * s + half, a.A + u * e + half, a.Layer, $"{a.Source}|{b.Source}");
                var shorter = Math.Min(a.Length, b.Length);
                pieces.Add(new Piece(center, d, i, j, (e - s) / shorter, a.Source, b.Source));
            }
        }

        pieces = ConsolidateFaces(pieces, segs, tol);

        var walls = pieces.Select(p =>
        {
            var type = SnapThickness(p.Thickness, tol.WallTypeSizes);
            var conf = p.OverlapShare >= 0.8 ? Confidence.High : p.OverlapShare >= 0.4 ? Confidence.Medium : Confidence.Low;
            // Keep the face both pieces share in place: shift the centre line if the type is thinner or thicker than measured.
            var center = p.Center;
            if (p.FaceB < 0)
            {
                var face = segs[p.FaceA];
                var side = Math.Sign(SegmentCleaner.Offset(face, p.Center.Mid));
                var shift = face.Direction.Perp() * (side * (type - p.Thickness) / 2);
                center = center with { A = center.A + shift, B = center.B + shift };
            }
            return new WallCandidate(center, p.Thickness, type, conf, p.SourceA, p.SourceB);
        }).ToList();

        walls = JoinCollinearWalls(walls, tol, openings ?? Array.Empty<Segment2>());
        walls = ExtendToJunctions(walls, tol);

        var unpaired = segs.Where((_, k) => claims[k].Count == 0).ToList();
        var groups = walls.Select(w => w.TypeThickness).Distinct().OrderBy(t => t).ToList();
        return new WallDetectionResult(walls, unpaired, groups, raw.Count, segs.Count, axes);
    }

    sealed record Piece(Segment2 Center, double Thickness, int FaceA, int FaceB, double OverlapShare, string SourceA, string SourceB);

    /// <summary>
    /// Old façades have recesses for windows and piers between them, so one long
    /// face line pairs with many short lines behind it, giving alternating thick
    /// and thin pieces. Pieces on the same side of one continuous face, with gaps
    /// no wider than an opening, become one wall of the most common thickness,
    /// keeping that face exactly in place. Pieces left inside the new wall are dropped.
    /// </summary>
    static List<Piece> ConsolidateFaces(List<Piece> pieces, List<Segment2> segs, Tolerances tol)
    {
        var used = new HashSet<Piece>();
        var merged = new List<Piece>();
        var byFace = pieces
            .SelectMany(p => new[] { (face: p.FaceA, p), (face: p.FaceB, p) })
            .GroupBy(x => (x.face, side: Math.Sign(SegmentCleaner.Offset(segs[x.face], x.p.Center.Mid))))
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => segs[g.Key.face].Length);

        foreach (var g in byFace)
        {
            var face = segs[g.Key.face];
            var items = g.Select(x => x.p).Where(p => !used.Contains(p))
                .Select(p => (p, s: Math.Min(SegmentCleaner.Along(face, p.Center.A), SegmentCleaner.Along(face, p.Center.B)),
                                  e: Math.Max(SegmentCleaner.Along(face, p.Center.A), SegmentCleaner.Along(face, p.Center.B))))
                .OrderBy(x => x.s).ToList();

            var cluster = new List<(Piece p, double s, double e)>();
            void Flush()
            {
                if (cluster.Count >= 2)
                {
                    // most common thickness by length
                    var t = cluster.GroupBy(c => SnapThickness(c.p.Thickness, tol.WallTypeSizes))
                                   .OrderByDescending(x => x.Sum(c => c.e - c.s)).First()
                                   .Select(c => c.p.Thickness).Average();
                    double s0 = cluster.Min(c => c.s), e0 = cluster.Max(c => c.e);
                    var u = face.Direction;
                    var off = u.Perp() * (g.Key.side * t / 2);
                    var line = new Segment2(face.A + u * s0 + off, face.A + u * e0 + off, face.Layer, face.Source);
                    var len = cluster.Sum(c => c.e - c.s);
                    merged.Add(new Piece(line, t, g.Key.face, -1, cluster.Min(c => c.p.OverlapShare), cluster[0].p.SourceA, cluster[0].p.SourceB));
                    foreach (var c in cluster) used.Add(c.p);
                }
                cluster.Clear();
            }
            foreach (var it in items)
            {
                if (cluster.Count > 0 && it.s - cluster.Max(c => c.e) > tol.MaxOpeningWidth) Flush();
                cluster.Add(it);
            }
            Flush();
        }

        var result = new List<Piece>(merged);
        foreach (var p in pieces.Where(p => !used.Contains(p)))
            if (!merged.Any(m => Inside(m, p.Center.Mid))) result.Add(p);
        return result;
    }

    static bool Inside(Piece wall, Vec2 point)
    {
        var t = SegmentCleaner.Along(wall.Center, point);
        return t >= 0 && t <= wall.Center.Length && Math.Abs(SegmentCleaner.Offset(wall.Center, point)) < wall.Thickness / 2;
    }

    /// <summary>
    /// Walls drawn with one line only (common on ground floors and in older surveys):
    /// lines inside the building, long enough, and not a face of a wall already found,
    /// become thin walls centred on the line, marked low confidence.
    /// </summary>
    public static List<WallCandidate> FromSingleLines(IEnumerable<Segment2> unpaired, List<WallCandidate> walls, Polygon2? outline, Tolerances tol)
    {
        var result = new List<WallCandidate>(walls);
        var t = tol.SingleLineWallThickness;
        foreach (var s in unpaired.Where(s => s.Length >= tol.MinSingleLineWall))
        {
            if (outline != null && !FacadeWalls.Contains(outline, s.Mid)) continue;
            bool isFace = result.Any(w =>
            {
                var c = w.Centerline;
                if (SegmentCleaner.AngleDiffDeg(c.AngleDeg, s.AngleDeg) > tol.AngleSnapDeg) return false;
                if (Math.Abs(SegmentCleaner.Offset(c, s.Mid)) > w.TypeThickness / 2 + Math.Max(w.MeasuredThickness - w.TypeThickness, 0) / 2 + 50) return false;
                double a = SegmentCleaner.Along(c, s.A), b = SegmentCleaner.Along(c, s.B);
                double overlap = Math.Min(c.Length, Math.Max(a, b)) - Math.Max(0, Math.Min(a, b));
                return overlap > 0.3 * s.Length;
            });
            if (!isFace) result.Add(new WallCandidate(s with { Source = "single line" }, t, t, Confidence.Low, s.Source, ""));
        }
        return result;
    }

    /// <summary>Nearest standard size; beyond the list, the nearest 100 mm.</summary>
    public static double SnapThickness(double measured, IReadOnlyList<double> sizes)
    {
        if (sizes.Count == 0) return Math.Round(measured / 10) * 10;
        if (measured > sizes[^1]) return Math.Round(measured / 100) * 100;
        return sizes.OrderBy(x => Math.Abs(x - measured)).ThenBy(x => x).First();
    }

    // Is any marker point strictly between faces a and b, within [s, e] along a?
    static bool ContainsMarker(Segment2 a, Segment2 b, double s, double e, IReadOnlyList<Vec2> markers)
    {
        var off = SegmentCleaner.Offset(a, b.Mid);
        double lo = Math.Min(0, off), hi = Math.Max(0, off);
        foreach (var m in markers)
        {
            var t = SegmentCleaner.Along(a, m);
            if (t <= s || t >= e) continue;
            var o = SegmentCleaner.Offset(a, m);
            if (o > lo + 1 && o < hi - 1) return true;
        }
        return false;
    }

    public static string TypeNameFor(double thicknessMm) => $"Concept Wall {thicknessMm:0}mm";

    /// <summary>
    /// Joins collinear wall pieces of the same type into one wall when the gap
    /// between them is a junction (up to a wall thickness), or a door or window
    /// shown by opening linework in the gap.
    /// </summary>
    internal static List<WallCandidate> JoinCollinearWalls(List<WallCandidate> walls, Tolerances tol, IReadOnlyList<Segment2> openings, double plainGap = 0)
    {
        var list = walls.ToList();
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < list.Count && !merged; i++)
                for (int j = i + 1; j < list.Count && !merged; j++)
                {
                    var (w1, w2) = (list[i], list[j]);
                    if (w1.TypeThickness != w2.TypeThickness) continue;
                    Segment2 a = w1.Centerline, b = w2.Centerline;
                    if (SegmentCleaner.AngleDiffDeg(a.AngleDeg, b.AngleDeg) > tol.ParallelAngleDeg) continue;
                    if (Math.Abs(SegmentCleaner.Offset(a, b.A)) > tol.CollinearOffset || Math.Abs(SegmentCleaner.Offset(a, b.B)) > tol.CollinearOffset) continue;
                    double b0 = SegmentCleaner.Along(a, b.A), b1 = SegmentCleaner.Along(a, b.B);
                    double lo = Math.Min(b0, b1), hi = Math.Max(b0, b1);
                    double gapStart = lo > a.Length ? a.Length : hi, gapEnd = lo > a.Length ? lo : 0;
                    double gap = gapEnd - gapStart; // <= 0 when touching or overlapping
                    bool join = gap <= Math.Max(w1.TypeThickness + tol.JoinGap, plainGap)
                        || (gap <= tol.MaxOpeningWidth && HasOpeningIn(a, gapStart, gapEnd, w1.TypeThickness, openings));
                    if (!join) continue;

                    var u = a.Direction;
                    double s = Math.Min(0, lo), e = Math.Max(a.Length, hi);
                    var line = new Segment2(a.A + u * s, a.A + u * e, a.Layer, a.Source);
                    double total = w1.Length + w2.Length;
                    list[i] = w1 with
                    {
                        Centerline = line,
                        MeasuredThickness = (w1.MeasuredThickness * w1.Length + w2.MeasuredThickness * w2.Length) / total,
                        Confidence = w1.Confidence < w2.Confidence ? w1.Confidence : w2.Confidence,
                    };
                    list.RemoveAt(j);
                    merged = true;
                }
        }
        return list;
    }

    // Is there door/window linework inside the gap between two wall pieces?
    static bool HasOpeningIn(Segment2 axis, double gapStart, double gapEnd, double thickness, IReadOnlyList<Segment2> openings)
    {
        foreach (var o in openings)
            foreach (var p in new[] { o.A, o.B, o.Mid })
            {
                var t = SegmentCleaner.Along(axis, p);
                if (t > gapStart && t < gapEnd && Math.Abs(SegmentCleaner.Offset(axis, p)) <= thickness / 2 + 100) return true;
            }
        return false;
    }

    /// <summary>
    /// Wall centre lines found from face pairs stop short at corners and T-junctions.
    /// Extend (or trim) each end to meet the centre line of a crossing wall nearby.
    /// </summary>
    internal static List<WallCandidate> ExtendToJunctions(List<WallCandidate> walls, Tolerances tol)
    {
        var lines = walls.Select(w => w.Centerline).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            Vec2 a = lines[i].A, b = lines[i].B;
            for (int k = 0; k < lines.Length; k++)
            {
                if (k == i || SegmentCleaner.AngleDiffDeg(lines[i].AngleDeg, lines[k].AngleDeg) < 10) continue;
                var reach = Math.Max(walls[i].TypeThickness, walls[k].TypeThickness) + tol.JoinGap;
                if (Intersect(lines[i], lines[k]) is not Vec2 p) continue;
                // the junction must lie on (or just beyond) the other wall
                var t = SegmentCleaner.Along(lines[k], p);
                if (t < -reach || t > lines[k].Length + reach) continue;
                if (p.DistanceTo(a) <= reach && p.DistanceTo(a) < p.DistanceTo(b)) a = p;
                else if (p.DistanceTo(b) <= reach) b = p;
            }
            lines[i] = lines[i] with { A = a, B = b };
        }
        return walls.Select((w, i) => w with { Centerline = lines[i] }).ToList();
    }

    /// <summary>
    /// Concept walls should run continuously: pieces of one wall either side of a gap up to
    /// <see cref="Tolerances.DoorGap"/> (door openings, missing linework) are joined, and
    /// wall ends left hanging run on to the next wall across their path within the same distance.
    /// </summary>
    public static List<WallCandidate> CloseGaps(List<WallCandidate> walls, Tolerances tol, IReadOnlyList<Segment2> openings)
    {
        var list = JoinCollinearWalls(walls, tol, openings, tol.DoorGap);
        list = ExtendToJunctions(list, tol);
        var lines = list.Select(w => w.Centerline).ToArray();
        bool Free(int i, Vec2 end) => !lines.Where((_, k) => k != i).Any(o => DistanceToSegment(end, o) <= list[i].TypeThickness / 2 + 2 * tol.JoinGap);
        for (int i = 0; i < lines.Length; i++)
        {
            Vec2 a = lines[i].A, b = lines[i].B;
            var u = lines[i].Direction;
            foreach (bool atB in new[] { false, true })
            {
                var end = atB ? b : a;
                if (!Free(i, end)) continue;
                var dir = atB ? u : u * -1;
                Vec2? best = null; double bestD = tol.DoorGap;
                for (int k = 0; k < lines.Length; k++)
                {
                    if (k == i || SegmentCleaner.AngleDiffDeg(lines[i].AngleDeg, lines[k].AngleDeg) < 10) continue;
                    if (Intersect(lines[i], lines[k]) is not Vec2 p) continue;
                    double ahead = (p - end).Dot(dir), t = SegmentCleaner.Along(lines[k], p);
                    if (ahead <= 0 || ahead >= bestD || t < -tol.JoinGap || t > lines[k].Length + tol.JoinGap) continue;
                    best = p; bestD = ahead;
                }
                if (best is Vec2 q) { if (atB) b = q; else a = q; }
            }
            lines[i] = lines[i] with { A = a, B = b };
        }
        return list.Select((w, i) => w with { Centerline = lines[i] }).ToList();
    }

    static double DistanceToSegment(Vec2 p, Segment2 s)
    {
        var d = s.B - s.A; double len2 = d.Dot(d);
        double t = len2 == 0 ? 0 : Math.Clamp((p - s.A).Dot(d) / len2, 0, 1);
        return p.DistanceTo(s.A + d * t);
    }

    public static Vec2? Intersect(Segment2 s1, Segment2 s2)
    {
        var r = s1.B - s1.A;
        var q = s2.B - s2.A;
        var den = r.Cross(q);
        if (Math.Abs(den) < 1e-9) return null;
        var t = (s2.A - s1.A).Cross(q) / den;
        return s1.A + r * t;
    }

    static List<(double, double)> Subtract((double s, double e) iv, List<(double, double)> cuts)
    {
        var parts = new List<(double, double)> { iv };
        foreach (var (cs, ce) in cuts)
        {
            double lo = Math.Min(cs, ce), hi = Math.Max(cs, ce);
            parts = parts.SelectMany(p =>
            {
                if (hi <= p.Item1 || lo >= p.Item2) return new[] { p };
                var outp = new List<(double, double)>();
                if (lo > p.Item1) outp.Add((p.Item1, lo));
                if (hi < p.Item2) outp.Add((hi, p.Item2));
                return outp.ToArray();
            }).ToList();
        }
        return parts;
    }

    // Convert an interval on 'from' segment's axis to 'to' segment's axis (both lines parallel).
    static (double, double) ToAxis(Segment2 from, Segment2 to, (double s, double e) iv)
    {
        var u = from.Direction;
        double s = SegmentCleaner.Along(to, from.A + u * iv.s), e = SegmentCleaner.Along(to, from.A + u * iv.e);
        return (Math.Min(s, e), Math.Max(s, e));
    }
}
