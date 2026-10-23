namespace SurveyToRevit.Core;

/// <summary>
/// Real walls stand on each other: a façade lines up storey to storey. A line drawn half a metre outside
/// the façade on one floor only (a balcony edge, a terrace upstand, a mansard's foot), or a lining drawn
/// inside it, is not the building's face. Each wall gets the share of its length that overlaps a wall on
/// the floor below or above.
/// </summary>
public static class FacadeStack
{
    /// <summary>A wall this much stacked (share of its length) is part of the building's face.</summary>
    public const double StackedShare = 0.5;

    /// <summary>
    /// For each straight wall of each floor, the share of its length that stands on or under a wall of the
    /// nearest whole floor below or above (parallel, overlapping it in plan). Floors are given bottom to top.
    /// </summary>
    public static Dictionary<WallCandidate, double> Shares(IReadOnlyList<(IReadOnlyList<WallCandidate> Walls, Polygon2? Outline)> floors, double partialShare = 0.6)
    {
        // a part floor (a mezzanine, a plant room on the roof) is no guide to where the walls run
        var whole = new bool[floors.Count];
        double lastArea = 0;
        for (int i = 0; i < floors.Count; i++)
        {
            double area = floors[i].Outline?.Area ?? 0;
            whole[i] = area > 0 && area >= partialShare * lastArea;
            if (whole[i]) lastArea = area;
        }
        var result = new Dictionary<WallCandidate, double>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < floors.Count; i++)
        {
            var neighbours = new List<WallCandidate>();
            for (int j = i - 1; j >= 0; j--) if (whole[j]) { neighbours.AddRange(floors[j].Walls); break; }
            for (int j = i + 1; j < floors.Count; j++) if (whole[j]) { neighbours.AddRange(floors[j].Walls); break; }
            neighbours = neighbours.Where(Straight).ToList();
            foreach (var w in floors[i].Walls.Where(Straight)) result[w] = Share(w, neighbours);
        }
        return result;
    }

    static bool Straight(WallCandidate w) => w.Arc == null && !w.Curtain && w.Centerline.Length >= 1;

    /// <summary>Share of <paramref name="w"/>'s length that a parallel wall in <paramref name="others"/> overlaps in plan (by 20 mm or more across).</summary>
    public static double Share(WallCandidate w, IEnumerable<WallCandidate> others)
    {
        var c = w.Centerline; var dir = c.Direction;
        var spans = new List<(double, double)>();
        foreach (var g in others)
        {
            if (SegmentCleaner.AngleDiffDeg(g.Centerline.AngleDeg, c.AngleDeg) > 3) continue;
            double off = Math.Abs(SegmentCleaner.Offset(c, g.Centerline.Mid));
            if (off > (w.TypeThickness + g.TypeThickness) / 2 - 20) continue;
            double a = (g.Centerline.A - c.A).Dot(dir), b = (g.Centerline.B - c.A).Dot(dir);
            double lo = Math.Max(0, Math.Min(a, b)), hi = Math.Min(c.Length, Math.Max(a, b));
            if (hi > lo) spans.Add((lo, hi));
        }
        if (spans.Count == 0) return 0;
        double covered = 0, end = double.MinValue;
        foreach (var (lo, hi) in spans.OrderBy(s => s.Item1))
        {
            if (hi <= end) continue;
            covered += hi - Math.Max(lo, end);
            end = hi;
        }
        return Math.Min(1, covered / c.Length);
    }

    /// <summary>A re-seated window: its new host and centre, the walls it now also cuts (its old host among them), and the outward direction.</summary>
    public readonly record struct Reseat(int Window, int Host, Vec2 Center, Vec2 Out, List<(int Wall, Vec2 At)> AlsoCut);

    /// <summary>
    /// Windows drawn in an inner wall (a lining, the inner leaf) with the building's face just outside it.
    /// From the window, the walls crossed on the way out are one build-up when no gap between them is wider
    /// than <paramref name="maxGapMm"/>; the window belongs in the outermost of them when that one is a real
    /// wall (it stands on or under a wall on the next floors), not a balcony edge. The walls passed on the
    /// way are cut too. A wall further out across a gap is a balcony or a loggia's front, left as it is.
    /// </summary>
    public static List<Reseat> Seat(IReadOnlyList<WallCandidate> walls, IReadOnlyList<(int Host, Vec2 Center, double WidthMm)> windows,
        Polygon2? outline, IReadOnlyDictionary<WallCandidate, double> shares, double maxGapMm = 600)
    {
        var moves = new List<Reseat>();
        if (outline == null) return moves;
        bool Stacked(WallCandidate w) => shares.TryGetValue(w, out var s) && s >= StackedShare;
        for (int wi = 0; wi < windows.Count; wi++)
        {
            var (host, center, width) = windows[wi];
            var h = walls[host];
            if (h.Arc != null || h.Curtain) continue;
            var n = h.Centerline.Direction.Perp();
            foreach (var side in new[] { n, n * -1 })
            {
                // walk out from the window's centre and its quarter points; every probe must cross the same walls
                var crossings = new List<List<int>>();
                bool outside = true;
                foreach (var f in new[] { -0.25, 0.0, 0.25 })
                {
                    var p0 = center + h.Centerline.Direction * (f * width);
                    var crossed = new List<int>();
                    double gap = 0; int inWall = host; bool reached = false;
                    for (double d = 10; d <= 3000; d += 10)
                    {
                        var q = p0 + side * d;
                        int k = Body(walls, q, inWall);
                        if (k < 0)
                        {
                            inWall = -1;
                            gap += 10;
                            if (!FacadeWalls.Contains(outline, q + side * 300)) { reached = true; break; }
                            if (gap > maxGapMm) break;
                            continue;
                        }
                        if (k != inWall && k != host && !crossed.Contains(k)) crossed.Add(k);
                        inWall = k; gap = 0;
                    }
                    if (!reached) { outside = false; break; }
                    crossings.Add(crossed);
                }
                if (!outside || crossings.Any(c => c.Count == 0)) continue;
                if (crossings.Any(c => !c.SequenceEqual(crossings[0]))) continue;
                var path = crossings[0];
                int outer = path[^1];
                var ow = walls[outer];
                if (ow.Arc != null || ow.Curtain || !Stacked(ow) || ow.HeightMm != null || ow.TypeThickness < 100) continue;
                if (SegmentCleaner.AngleDiffDeg(ow.Centerline.AngleDeg, h.Centerline.AngleDeg) > 5) continue;
                // the whole window must fit along the outer wall
                double along = SegmentCleaner.Along(ow.Centerline, center);
                if (along - width / 2 < -50 || along + width / 2 > ow.Centerline.Length + 50) continue;
                var at = ow.Centerline.A + ow.Centerline.Direction * Math.Clamp(along, width / 2, Math.Max(width / 2, ow.Centerline.Length - width / 2));
                var cuts = new List<(int, Vec2)> { (host, center) };
                foreach (var k in path.Take(path.Count - 1))
                    cuts.Add((k, walls[k].Centerline.A + walls[k].Centerline.Direction * Math.Clamp(SegmentCleaner.Along(walls[k].Centerline, center), 0, walls[k].Centerline.Length)));
                moves.Add(new Reseat(wi, outer, at, side, cuts));
                break;
            }
        }
        return moves;
    }

    /// <summary>The wall whose body holds <paramref name="p"/>, preferring <paramref name="current"/>; -1 for none.</summary>
    static int Body(IReadOnlyList<WallCandidate> walls, Vec2 p, int current)
    {
        if (current >= 0 && In(walls[current], p)) return current;
        for (int k = 0; k < walls.Count; k++)
            if (k != current && In(walls[k], p)) return k;
        return -1;
    }

    static bool In(WallCandidate w, Vec2 p)
    {
        double along = WallGeometry.Along(w, p);
        return along >= 0 && along <= WallGeometry.Length(w) && Math.Abs(WallGeometry.Offset(w, p)) <= w.TypeThickness / 2 + 5;
    }
}
