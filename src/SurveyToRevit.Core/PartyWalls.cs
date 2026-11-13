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
        double minThickness = 100, double minLength = 2000, double edgeMm = 700, double minGap = 800,
        IReadOnlyList<HashSet<WallCandidate>>? withOpenings = null)
    {
        // a party wall has no windows: a façade (which steps back on an upper floor) is never carried
        bool Blank(int floor, WallCandidate w) => withOpenings == null || floor < 0 || floor >= withOpenings.Count || !withOpenings[floor].Contains(w);
        var added = new List<Added>();
        var envelopes = floors.Select(f => NeighbourWalls.Envelope(f.Walls)).ToList();
        for (int i = 0; i < floors.Count; i++)
        {
            var env = envelopes[i];
            if (env == null) continue;
            var mine = floors[i].Walls;
            var sources = new List<WallCandidate>();
            if (i > 0) sources.AddRange(floors[i - 1].Walls.Where(w => Blank(i - 1, w)));
            if (i + 1 < floors.Count) sources.AddRange(floors[i + 1].Walls.Where(w => Blank(i + 1, w)));
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

    /// <summary>
    /// Party walls drawn by their inside face only come out as thin walls (the other face assumed 100 mm away). A blank
    /// (no window or door linework) wall on the floor's edge, thinner than maxThinMm, that stands on the same line on the
    /// floor below or above too, is a party wall: thickened outward to partyMm, its surveyed inside face kept. Low confidence.
    /// </summary>
    public static List<(int Floor, WallCandidate Old, WallCandidate New)> Thicken(IReadOnlyList<IReadOnlyList<WallCandidate>> floors,
        IReadOnlyList<HashSet<WallCandidate>>? withOpenings = null, double partyMm = 215, double maxThinMm = 150, double minLength = 2000, double edgeMm = 300)
    {
        var res = new List<(int, WallCandidate, WallCandidate)>();
        for (int i = 0; i < floors.Count; i++)
        {
            if (NeighbourWalls.Envelope(floors[i]) is not { } env) continue;
            var centre = env.Aggregate(new Vec2(0, 0), (a, v) => a + v) * (1.0 / env.Count);
            foreach (var w in floors[i])
            {
                if (w.Arc != null || w.Curtain || w.HeightMm != null || w.TypeThickness > maxThinMm || w.Length < minLength) continue;
                if (withOpenings != null && i < withOpenings.Count && withOpenings[i].Contains(w)) continue;
                if (EdgeDistance(env, w.Centerline.Mid) > w.TypeThickness / 2 + edgeMm) continue;
                bool Stacked(int j) => j >= 0 && j < floors.Count && floors[j].Any(o => o.Arc == null
                    && SegmentCleaner.AngleDiffDeg(o.Centerline.AngleDeg, w.Centerline.AngleDeg) <= 3
                    && Math.Abs((o.Centerline.Mid - w.Centerline.A).Dot(w.Centerline.Direction.Perp())) <= 300
                    && FacadeStack.Share(w, new[] { o }) >= 0.3);
                if (!Stacked(i - 1) && !Stacked(i + 1)) continue;
                var c = w.Centerline; var n = c.Direction.Perp();
                var outward = (c.Mid - centre).Dot(n) >= 0 ? n : new Vec2(-n.X, -n.Y);
                var shift = outward * ((partyMm - w.TypeThickness) / 2);
                var made = w with
                {
                    Centerline = c with { A = c.A + shift, B = c.B + shift }, TypeThickness = partyMm, Confidence = Confidence.Low,
                    FaceB = "party wall: other face assumed " + partyMm + " mm from the drawn one",
                };
                res.Add((i, w, made));
            }
        }
        return res;
    }

    /// <summary>
    /// Outside corners left open: a facade that stops short of the party wall it meets (the survey draws the facade to its
    /// window reveal or pier, the party wall from its inside face). Two walls on the floor's edge, at 60° or more to each
    /// other, whose free ends (no other wall within 150 mm of them) are both within reachMm of where their lines cross, or one
    /// free end within reach while the other wall's line runs past the crossing, are run on to the crossing.
    /// </summary>
    public static List<WallCandidate> CloseCorners(IReadOnlyList<WallCandidate> walls, out int closed, double reachMm = 1500, double edgeMm = 700)
    {
        var list = walls.ToList(); closed = 0;
        if (NeighbourWalls.Envelope(list) is not { } env) return list;
        bool Edge(WallCandidate w) => w.Arc == null && !w.Curtain && w.HeightMm == null && EdgeDistance(env, w.Centerline.Mid) <= w.TypeThickness / 2 + edgeMm;
        bool Free(int i, Vec2 end) => !list.Where((o, k) => k != i && o.Arc == null).Any(o => Dist(end, o.Centerline) <= o.TypeThickness / 2 + list[i].TypeThickness / 2 + 150);
        for (int i = 0; i < list.Count; i++)
            for (int k = i + 1; k < list.Count; k++)
            {
                var wi = list[i]; var wk = list[k];
                if (!Edge(wi) || !Edge(wk) || SegmentCleaner.AngleDiffDeg(wi.Centerline.AngleDeg, wk.Centerline.AngleDeg) < 60) continue;
                if (Cross(wi.Centerline, wk.Centerline) is not Vec2 p) continue;
                // the end of each nearer the crossing; a wall whose line already runs through the crossing keeps its ends
                Vec2 Near(Segment2 s) => s.A.DistanceTo(p) <= s.B.DistanceTo(p) ? s.A : s.B;
                bool Through(Segment2 s) => Dist(p, s) <= 1;
                Vec2 ei = Near(wi.Centerline), ek = Near(wk.Centerline);
                bool gi = !Through(wi.Centerline) && ei.DistanceTo(p) <= reachMm && ei.DistanceTo(p) > wi.TypeThickness && Free(i, ei);
                bool gk = !Through(wk.Centerline) && ek.DistanceTo(p) <= reachMm && ek.DistanceTo(p) > wk.TypeThickness && Free(k, ek);
                if (!(gi && (gk || Through(wk.Centerline) || ek.DistanceTo(p) <= wk.TypeThickness)) && !(gk && (Through(wi.Centerline) || ei.DistanceTo(p) <= wi.TypeThickness))) continue;
                if (gi) list[i] = wi with { Centerline = ei == wi.Centerline.A ? wi.Centerline with { A = p } : wi.Centerline with { B = p } };
                if (gk) list[k] = wk with { Centerline = ek == wk.Centerline.A ? wk.Centerline with { A = p } : wk.Centerline with { B = p } };
                closed++;
            }
        return list;
    }

    static Vec2? Cross(Segment2 a, Segment2 b)
    {
        var d1 = a.B - a.A; var d2 = b.B - b.A; double den = d1.Cross(d2);
        if (Math.Abs(den) < 1e-9) return null;
        double t = (b.A - a.A).Cross(d2) / den;
        return a.A + d1 * t;
    }

    static double Dist(Vec2 p, Segment2 s)
    {
        var d = s.B - s.A; double l2 = d.Dot(d);
        double t = l2 == 0 ? 0 : Math.Clamp((p - s.A).Dot(d) / l2, 0, 1);
        return p.DistanceTo(s.A + d * t);
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
