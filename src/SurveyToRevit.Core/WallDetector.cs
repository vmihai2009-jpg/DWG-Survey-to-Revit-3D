namespace SurveyToRevit.Core;

public enum Confidence { Low, Medium, High }

/// <summary>A wall found in the plan: its centre line and thickness, in mm.</summary>
public sealed record WallCandidate(Segment2 Centerline, double MeasuredThickness, double TypeThickness, Confidence Confidence, string FaceA, string FaceB)
{
    public double Length => Centerline.Length;
    public string TypeName => $"CONCEPT_{TypeThickness:0}";
}

public sealed record WallDetectionResult(
    IReadOnlyList<WallCandidate> Walls,
    IReadOnlyList<Segment2> UnpairedSegments,
    IReadOnlyList<double> ThicknessGroups,
    int InputSegments,
    int CleanedSegments);

/// <summary>
/// Finds walls as pairs of parallel face lines a wall-thickness apart.
/// Each face line can belong to only one wall at any point along its length,
/// so the closest pairs are taken first. A line that pairs with nothing is
/// reported, never turned into a wall.
/// </summary>
public static class WallDetector
{
    public static WallDetectionResult Detect(IEnumerable<Segment2> rawSegments, Tolerances? tolerances = null)
    {
        var tol = tolerances ?? Tolerances.Default;
        var raw = rawSegments.ToList();
        var segs = SegmentCleaner.Clean(raw, tol);
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

        var pieces = new List<(Segment2 center, double thickness, Segment2 a, Segment2 b, double overlapShare)>();
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
                claims[i].Add((s, e));
                claims[j].Add(ToAxis(a, b, (s, e)));
                var u = a.Direction;
                var half = u.Perp() * (SegmentCleaner.Offset(a, b.Mid) * 0.5);
                var center = new Segment2(a.A + u * s + half, a.A + u * e + half, a.Layer, $"{a.Source}|{b.Source}");
                var shorter = Math.Min(a.Length, b.Length);
                pieces.Add((center, d, a, b, (e - s) / shorter));
            }
        }

        var groups = GroupThicknesses(pieces.Select(p => p.thickness), tol.ThicknessGrouping);
        var walls = pieces.Select(p =>
        {
            var type = groups.OrderBy(g => Math.Abs(g - p.thickness)).First();
            var conf = p.overlapShare >= 0.8 ? Confidence.High : p.overlapShare >= 0.4 ? Confidence.Medium : Confidence.Low;
            return new WallCandidate(p.center, p.thickness, type, conf, p.a.Source, p.b.Source);
        }).ToList();

        walls = JoinCollinearWalls(walls, tol);
        walls = ExtendToJunctions(walls, tol);

        var unpaired = segs.Where((_, k) => claims[k].Count == 0).ToList();
        return new WallDetectionResult(walls, unpaired, groups, raw.Count, segs.Count);
    }

    /// <summary>Clusters thicknesses within the grouping tolerance; each group value is rounded to 5 mm.</summary>
    public static List<double> GroupThicknesses(IEnumerable<double> thicknesses, double groupTol)
    {
        var groups = new List<List<double>>();
        foreach (var t in thicknesses.OrderBy(x => x))
        {
            if (groups.Count > 0 && t - groups[^1].Average() <= groupTol) groups[^1].Add(t);
            else groups.Add(new List<double> { t });
        }
        return groups.Select(g => Math.Round(g.Average() / 5.0) * 5.0).Distinct().ToList();
    }

    /// <summary>Joins collinear wall pieces of the same type split by a junction or a door gap.</summary>
    static List<WallCandidate> JoinCollinearWalls(List<WallCandidate> walls, Tolerances tol)
    {
        var result = new List<WallCandidate>();
        foreach (var byType in walls.GroupBy(w => w.TypeThickness))
        {
            var list = byType.ToList();
            var merged = SegmentCleaner.MergeCollinear(list.Select(w => w.Centerline).ToList(),
                tol.ParallelAngleDeg, tol.CollinearOffset, byType.Key + tol.JoinGap);
            foreach (var m in merged)
            {
                var parts = list.Where(w => SegmentCleaner.AngleDiffDeg(w.Centerline.AngleDeg, m.AngleDeg) <= tol.ParallelAngleDeg
                    && Math.Abs(SegmentCleaner.Offset(m, w.Centerline.Mid)) <= tol.CollinearOffset
                    && SegmentCleaner.Along(m, w.Centerline.Mid) >= -tol.CollinearOffset
                    && SegmentCleaner.Along(m, w.Centerline.Mid) <= m.Length + tol.CollinearOffset).ToList();
                var first = parts.Count > 0 ? parts[0] : list[0];
                var measured = parts.Count > 0 ? parts.Sum(p => p.MeasuredThickness * p.Length) / parts.Sum(p => p.Length) : first.MeasuredThickness;
                var conf = parts.Count > 0 ? parts.Min(p => p.Confidence) : first.Confidence;
                result.Add(first with { Centerline = m, MeasuredThickness = measured, Confidence = conf });
            }
        }
        return result;
    }

    /// <summary>
    /// Wall centre lines found from face pairs stop short at corners and T-junctions.
    /// Extend (or trim) each end to meet the centre line of a crossing wall nearby.
    /// </summary>
    static List<WallCandidate> ExtendToJunctions(List<WallCandidate> walls, Tolerances tol)
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
