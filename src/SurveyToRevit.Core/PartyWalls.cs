namespace SurveyToRevit.Core;

/// <summary>
/// Party and flank walls run the building's full height, but a survey often draws only their inside face on some floors
/// (the outer face is the building next door's) or leaves a stretch out (behind a lift, a riser). A thick wall on the
/// building's edge on the floor below or above is carried into a floor that lacks it over a stretch when that floor is
/// sandwiched (the floors below and above both have it there) or draws a line along the wall's inside face there.
/// </summary>
public static class PartyWalls
{
    public sealed record Added(int Floor, WallCandidate Wall, string Why);

    /// <param name="floors">bottom to top: each floor's walls and its unpaired (single) drawn lines, in model mm</param>
    public static List<Added> Fill(IReadOnlyList<(IReadOnlyList<WallCandidate> Walls, IReadOnlyList<Segment2> Lines)> floors,
        double minThickness = 100, double minLength = 2000, double edgeMm = 700, double minGap = 800)
    {
        var added = new List<Added>();
        var envelopes = floors.Select(f => NeighbourWalls.Envelope(f.Walls)).ToList();
        for (int i = 0; i < floors.Count; i++)
        {
            var env = envelopes[i];
            if (env == null) continue;
            var mine = floors[i].Walls;
            var sources = new List<WallCandidate>();
            if (i > 0) sources.AddRange(floors[i - 1].Walls);
            if (i + 1 < floors.Count) sources.AddRange(floors[i + 1].Walls);
            var done = new List<WallCandidate>();
            foreach (var w in sources.Where(w => w.Arc == null && !w.Curtain && w.TypeThickness >= minThickness && w.Length >= minLength)
                                     .OrderByDescending(w => w.Length))
            {
                // on this floor's edge, not an inner wall
                if (EdgeDistance(env, w.Centerline.Mid) > edgeMm + w.TypeThickness / 2) continue;
                if (done.Any(d => FacadeStack.Share(w, new[] { d }) > 0.5)) continue;
                foreach (var (lo, hi) in Uncovered(w, mine, minGap))
                {
                    var piece = Piece(w, lo, hi);
                    bool below = i > 0 && FacadeStack.Share(piece, floors[i - 1].Walls) >= 0.8;
                    bool above = i + 1 < floors.Count && FacadeStack.Share(piece, floors[i + 1].Walls) >= 0.8;
                    bool face = FaceDrawn(piece, floors[i].Lines) >= 0.5;
                    if (!(below && above) && !face) continue;
                    var made = piece with { Confidence = Confidence.Low, FaceA = "party wall", FaceB = below && above ? "floors below and above" : "inside face drawn" };
                    added.Add(new Added(i, made, below && above ? "the floors below and above have it" : "this floor draws its inside face"));
                    done.Add(made);
                }
            }
        }
        return added;
    }

    /// <summary>Stretches of w's length (along its line) that no parallel wall of the floor covers, at least minGap long.</summary>
    static List<(double lo, double hi)> Uncovered(WallCandidate w, IReadOnlyList<WallCandidate> floor, double minGap)
    {
        var c = w.Centerline; var dir = c.Direction; var n = dir.Perp(); double len = c.Length;
        var spans = new List<(double, double)>();
        foreach (var o in floor)
        {
            if (o.Arc != null || SegmentCleaner.AngleDiffDeg(o.Centerline.AngleDeg, c.AngleDeg) > 5) continue;
            double off = Math.Abs((o.Centerline.Mid - c.A).Dot(n));
            if (off > (w.TypeThickness + o.TypeThickness) / 2 - 20) continue;
            double a = (o.Centerline.A - c.A).Dot(dir), b = (o.Centerline.B - c.A).Dot(dir);
            spans.Add((Math.Max(0, Math.Min(a, b)), Math.Min(len, Math.Max(a, b))));
        }
        var gaps = new List<(double, double)>();
        double at = 0;
        foreach (var (a, b) in spans.Where(s => s.Item2 > s.Item1).OrderBy(s => s.Item1))
        {
            if (a - at >= minGap) gaps.Add((at, a));
            at = Math.Max(at, b);
        }
        if (len - at >= minGap) gaps.Add((at, len));
        return gaps;
    }

    static WallCandidate Piece(WallCandidate w, double lo, double hi)
    {
        var c = w.Centerline; var dir = c.Direction;
        return w with { Centerline = new Segment2(c.A + dir * lo, c.A + dir * hi, "party wall", "party wall") };
    }

    /// <summary>Share of the wall's length along which a single drawn line runs on one of its faces (within 60 mm).</summary>
    static double FaceDrawn(WallCandidate w, IReadOnlyList<Segment2> lines)
    {
        var c = w.Centerline; var dir = c.Direction; var n = dir.Perp(); double len = c.Length, half = w.TypeThickness / 2;
        var spans = new List<(double, double)>();
        foreach (var l in lines)
        {
            if (SegmentCleaner.AngleDiffDeg(l.AngleDeg, c.AngleDeg) > 3) continue;
            double off = Math.Abs((l.Mid - c.A).Dot(n));
            if (Math.Abs(off - half) > 60) continue;
            double a = (l.A - c.A).Dot(dir), b = (l.B - c.A).Dot(dir);
            spans.Add((Math.Max(0, Math.Min(a, b)), Math.Min(len, Math.Max(a, b))));
        }
        double covered = 0, at = 0;
        foreach (var (a, b) in spans.Where(s => s.Item2 > s.Item1).OrderBy(s => s.Item1))
        {
            if (b <= at) continue;
            covered += b - Math.Max(a, at); at = b;
        }
        return len > 0 ? covered / len : 0;
    }

    static double EdgeDistance(IReadOnlyList<Vec2> hull, Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i]; var d = hull[(i + 1) % hull.Count] - a; double len2 = d.Dot(d);
            double t = len2 == 0 ? 0 : Math.Clamp((p - a).Dot(d) / len2, 0, 1);
            best = Math.Min(best, p.DistanceTo(a + d * t));
        }
        return best;
    }
}
