namespace SurveyToRevit.Core.Survey;

/// <summary>One step of a neighbour's front read off an elevation: a stretch of the facade line (model mm) and its height.</summary>
public sealed record ContextFront(string Facade, Segment2 Line, double BaseZ, double TopZ, List<ContextWindow> Windows, string Note)
{
    /// <summary>Unit vector from the front into the neighbour (the side this building's floors are on).</summary>
    public Vec2 Inward { get; init; }
    /// <summary>The steps of one neighbour's front on one drawing share it (facade id and side).</summary>
    public string Group { get; init; } = "";
}

/// <summary>A plain context wall (a block's side or back), from BaseZ (NaN: the ground floor) up to TopZ, model mm.</summary>
public sealed record ContextWall(Segment2 Line, double BaseZ, double TopZ, string Note);

/// <summary>What is built for the buildings next door: the fronts (with windows), the other walls, and the roofs.</summary>
public sealed record ContextPlan(List<ContextFront> Fronts, List<ContextWall> Walls, List<RoofCandidate> Roofs, List<string> Notes);

/// <summary>A neighbour's window: its centre on the front line (model mm) and its width, sill and head (model heights).</summary>
public sealed record ContextWindow(Vec2 Centre, double WidthMm, double SillZ, double HeadZ);

/// <summary>
/// The buildings next door, as an elevation draws them. A matched elevation (item E) shows more than this building: the
/// linework past either end of the facade, where a point 1.5 m in from the facade line is outside every floor's outline,
/// is a neighbour's front. Its skyline is the top of the linework in 250 mm strips, merged into steps (strips within
/// 400 mm of each other, steps under 1 m folded into the next) so aerials and pots don't make towers; its windows are the
/// drawing's window outlines there. Built as context next to the roof-plan blocks; deterministic, any survey with elevations.
/// </summary>
public static class ElevationContext
{
    public const double StripMm = 250, StepMm = 400, MinStepMm = 2000, InsideMm = 1500, MinFrontMm = 3000, MinHeightMm = 2500, CoverReachMm = 4000;

    static bool Skyline(LayerRole r) => r is not (LayerRole.Annotation or LayerRole.LevelText or LayerRole.HeightText
        or LayerRole.RoomName or LayerRole.Fitting or LayerRole.Area or LayerRole.Overhead);

    public static List<ContextFront> Read(IReadOnlyList<ElevationDrawing> elevations, IReadOnlyList<SurveyLevel> levels, LayerRules rules)
    {
        var res = new List<ContextFront>();
        var outlines = levels.SelectMany(l => (l.Outline != null ? new[] { l.Outline } : Array.Empty<Polygon2>()).Concat(l.MoreOutlines)).ToList();
        if (outlines.Count == 0) return res;
        double lowest = levels.Where(l => !double.IsNaN(l.ElevationMm)).Select(l => l.ElevationMm).DefaultIfEmpty(0).Min();
        // only a sure match places a neighbour: a drawing a bay off would put next door's front inside this building
        foreach (var d in elevations.Where(e => e.FacadeLine != null && e.Score >= ElevationIndex.SureScore && e.Parts.Count > 0 && e.Lines.Count > 0))
        {
            var fl = d.FacadeLine!;
            var facade = new Segment2(new Vec2(fl[0], fl[1]), new Vec2(fl[2], fl[3]));
            var dir = facade.Direction; double len = facade.Length;
            if (Inward(facade, outlines) is not Vec2 inward) continue;
            Vec2 At(double t) => facade.A + dir * t;
            // this building's where a point 1.5, 3 or 4.5 m in is inside a floor (a recess in the facade is still ours)
            bool Ours(double t) => outlines.Any(o => FacadeWalls.Contains(o, At(t) + inward * InsideMm)
                                                  || FacadeWalls.Contains(o, At(t) + inward * (2 * InsideMm))
                                                  || FacadeWalls.Contains(o, At(t) + inward * (3 * InsideMm)));
            int sg = d.Mirrored ? -1 : 1;
            double T(double x) => d.ShiftMm + sg * d.Scale * x;
            double Z(double x, double y)
            {
                var p = d.Parts.FirstOrDefault(q => x >= q.X0 && x <= q.X1) ?? d.Parts[0];
                return p.A * y + p.B;
            }
            // each line as a span along the facade and its heights
            var spans = d.Lines.Where(s => Skyline(rules.Classify(s.Layer)))
                .Select(s =>
                {
                    double ta = T(s.A.X), tb = T(s.B.X);
                    return (T0: Math.Min(ta, tb), T1: Math.Max(ta, tb), Z0: Math.Min(Z(s.A.X, s.A.Y), Z(s.B.X, s.B.Y)), Z1: Math.Max(Z(s.A.X, s.A.Y), Z(s.B.X, s.B.Y)));
                }).ToList();
            if (spans.Count == 0) continue;
            var windows = ElevationWindows.Read(d, rules);
            foreach (var side in new[] { -1, 1 })
            {
                // strips out from the facade's end, while the drawing has linework and the place is not this building's
                var strips = new List<(double T0, double T1, double Top, double Base)>();
                double from = side < 0 ? 0 : len;
                double reach = side < 0 ? -spans.Min(s => s.T0) : spans.Max(s => s.T1) - len;
                for (double k = 0; k + StripMm <= reach + 1; k += StripMm)
                {
                    double a = from + side * k, b = from + side * (k + StripMm);
                    double t0 = Math.Min(a, b), t1 = Math.Max(a, b);
                    if (Ours((t0 + t1) / 2)) { if (strips.Count > 0) break; continue; }
                    var here = spans.Where(s => s.T1 > t0 && s.T0 < t1).ToList();
                    if (here.Count == 0) { if (strips.Count > 0) break; continue; }
                    strips.Add((t0, t1, here.Max(s => s.Z1), here.Min(s => s.Z0)));
                }
                if (strips.Count * StripMm < MinFrontMm) continue;
                // a neighbour stands on the ground: down to this building's drawn foot when its own lowest line is higher
                // (a shopfront or basement area drawn on a layer left out), never under the lowest floor
                double foot = spans.Where(s => s.T1 > 0 && s.T0 < len).Select(s => s.Z0).DefaultIfEmpty(double.MaxValue).Min();
                double baseZ = Math.Max(lowest, Math.Min(strips.Min(s => s.Base), foot));
                // steps: strips of about one height together, short steps folded into the neighbour nearest in height
                var steps = new List<(double T0, double T1, double Top)>();
                foreach (var s in strips.OrderBy(s => s.T0))
                    if (steps.Count > 0 && Math.Abs(steps[^1].Top - s.Top) <= StepMm)
                        steps[^1] = (steps[^1].T0, s.T1, Math.Max(steps[^1].Top, s.Top));
                    else steps.Add((s.T0, s.T1, s.Top));
                for (bool again = true; again && steps.Count > 1;)
                {
                    again = false;
                    int i = Enumerable.Range(0, steps.Count).OrderBy(j => steps[j].T1 - steps[j].T0).First();
                    if (steps[i].T1 - steps[i].T0 >= MinStepMm) break;
                    int other = i == 0 ? 1 : i == steps.Count - 1 ? i - 1
                        : Math.Abs(steps[i - 1].Top - steps[i].Top) <= Math.Abs(steps[i + 1].Top - steps[i].Top) ? i - 1 : i + 1;
                    var o = steps[other];
                    steps[other] = (Math.Min(o.T0, steps[i].T0), Math.Max(o.T1, steps[i].T1), o.Top);
                    steps.RemoveAt(i); again = true;
                }
                foreach (var st in steps)
                {
                    if (st.Top - baseZ < MinHeightMm) continue;
                    var ws = windows.Where(w => w.Centre > st.T0 && w.Centre < st.T1 && w.SillZ >= baseZ - 50 && w.HeadZ <= st.Top - 100)
                        .Select(w => new ContextWindow(At(w.Centre), Math.Round(Math.Min(w.Width, st.T1 - st.T0 - 200)), Math.Round(w.SillZ), Math.Round(w.HeadZ)))
                        .Where(w => w.WidthMm >= ElevationWindows.MinWidthMm).ToList();
                    res.Add(new ContextFront(d.Facade ?? "", new Segment2(At(st.T0), At(st.T1)), Math.Round(baseZ), Math.Round(st.Top), ws,
                        $"{d.Title.Replace(" ", "")} {(side < 0 ? "left" : "right")} of {d.Facade}, {(st.T1 - st.T0) / 1000:0.0} m wide, top {st.Top / 1000:0.00} m, {ws.Count} windows")
                    { Inward = inward, Group = $"{d.Facade}{(side < 0 ? "<" : ">")}" });
                }
            }
        }
        return res;
    }

    /// <summary>
    /// A roof-plan block's wall that an elevation front already shows (parallel within 10°, within 4 m: the roof outline
    /// often stands back from or overhangs the front; half its length alongside): left out.
    /// </summary>
    public static bool Covered(Segment2 edge, IEnumerable<ContextFront> fronts)
    {
        double over = 0;
        foreach (var f in fronts)
        {
            var d = f.Line.Direction; var e = edge.Direction;
            if (Math.Abs(d.X * e.X + d.Y * e.Y) < Math.Cos(10 * Math.PI / 180)) continue;
            if (Math.Abs(SegmentCleaner.Offset(f.Line, edge.Mid)) > CoverReachMm) continue;
            double T(Vec2 p) => (p.X - f.Line.A.X) * d.X + (p.Y - f.Line.A.Y) * d.Y;
            double a = Math.Min(T(edge.A), T(edge.B)), b = Math.Max(T(edge.A), T(edge.B));
            over += Math.Max(0, Math.Min(b, f.Line.Length) - Math.Max(a, 0));
        }
        return over >= 0.5 * edge.Length;
    }

    /// <summary>The side of a facade line the building is on, by floor-outline votes (1 m either side); null when no floor tells.</summary>
    public static Vec2? Inward(Segment2 line, IReadOnlyList<Polygon2> outlines)
    {
        var dir = line.Direction; var n = new Vec2(-dir.Y, dir.X);
        int vote = 0;
        foreach (var o in outlines)
        {
            bool a = FacadeWalls.Contains(o, line.Mid + n * 1000), b = FacadeWalls.Contains(o, line.Mid - n * 1000);
            if (a != b) vote += a ? 1 : -1;
        }
        return vote == 0 ? null : vote > 0 ? n : new Vec2(-n.X, -n.Y);
    }

    /// <summary>Lies alongside one of the lines: parallel within 5°, within reach, and at least half its length beside them together.</summary>
    public static bool Alongside(Segment2 edge, IEnumerable<Segment2> lines, double reachMm)
    {
        double over = 0; var e = edge.Direction;
        foreach (var l in lines)
        {
            var d = l.Direction;
            if (l.Length < 1 || Math.Abs(d.X * e.X + d.Y * e.Y) < Math.Cos(5 * Math.PI / 180)) continue;
            if (Math.Abs(SegmentCleaner.Offset(l, edge.Mid)) > reachMm) continue;
            double T(Vec2 p) => (p - l.A).Dot(d);
            double a = Math.Min(T(edge.A), T(edge.B)), b = Math.Max(T(edge.A), T(edge.B));
            over += Math.Max(0, Math.Min(b, l.Length) - Math.Max(a, 0));
        }
        return over >= 0.5 * edge.Length;
    }

    public const double PartyReachMm = 800, MinDepthMm = 4000, MaxDepthMm = 20000, PairMinMm = 3000, SnapMm = 1500, PartyLineMm = 5000, PartyGapMm = 215;

    /// <summary>
    /// The context to build, as one coherent set:
    /// - each neighbour's front (its steps on one drawing) becomes a block: paired with the front of the same neighbour on
    ///   the opposite elevation (parallel, facing the other way, behind it, overlapping half) the block runs between them;
    ///   alone, it runs back as deep as this building does beside it (4-20 m). Sides, back and any stretch of the front no
    ///   step covers are plain walls; the roof is a slab at the highest step.
    /// - roof-plan blocks (over the neighbours) mostly inside a front block are left out (the elevation draws more);
    ///   the others keep their walls, less the ones a front block already has.
    /// - no context wall is built along this building's own outline (a party wall is this building's: built once).
    /// </summary>
    public static ContextPlan Plan(IReadOnlyList<ContextFront> fronts, IReadOnlyList<RoofCandidate> blocks, IReadOnlyList<SurveyLevel> levels)
    {
        var notes = new List<string>(); var walls = new List<ContextWall>(); var roofs = new List<RoofCandidate>();
        // this building's own lines: its facades and every wall it builds (party walls included)
        var ours = ElevationIndex.Facades(levels).Select(f => f.Line)
            .Concat(levels.SelectMany(l => l.Walls.Walls.Where(w => w.Arc == null).Select(w => w.Centerline))).ToList();
        var outlines = levels.SelectMany(l => (l.Outline != null ? new[] { l.Outline } : Array.Empty<Polygon2>()).Concat(l.MoreOutlines)).ToList();
        var ourWalls = levels.SelectMany(l => l.Walls.Walls.Where(w => w.Arc == null).Select(w => (Line: w.Centerline, T: w.TypeThickness))).ToList();
        int party = 0;
        // a wall along this building's own (the party line) is the neighbour's half of it: set just outside this building's
        // wall face, towards the neighbour (inside: a point inside the neighbour's block), so no floor where this building
        // lacks its wall is left open, and the two never overlap
        void Wall(Segment2 s, double baseZ, double topZ, string note, Vec2? inside = null)
        {
            if (s.Length < 300) return;
            if (Alongside(s, ours, PartyReachMm))
            {
                party++;
                if (inside is not Vec2 ins) return;
                var n = s.Direction.Perp(); if ((ins - s.Mid).Dot(n) < 0) n = new Vec2(-n.X, -n.Y);
                // against the outermost face of this building's walls along it (floors differ), its own half-thickness on
                // only walls standing on the party line (their thickness spans it, 200 mm slack), not stray walls beyond it
                double face = ourWalls.Where(w => Alongside(w.Line, new[] { s }, w.T / 2 + 200)
                                                  && Math.Abs((w.Line.Mid - s.Mid).Dot(n)) <= w.T / 2 + 200)
                    .Select(w => (w.Line.Mid - s.Mid).Dot(n) + w.T / 2).DefaultIfEmpty(PartyGapMm - 100).Max();
                double gap = Math.Max(face, 0) + 100;
                s = new Segment2(s.A + n * gap, s.B + n * gap);
                note += " (its side of the party wall)";
            }
            walls.Add(new ContextWall(s, baseZ, topZ, note));
        }
        var groups = fronts.Where(f => f.Group != "").GroupBy(f => f.Group).Select(g =>
        {
            var l = g.ToList(); var u = l[0].Line.Direction; var o = l[0].Line.A;
            double T(Vec2 p) => (p - o).Dot(u);
            return (Fronts: l, U: u, N: l[0].Inward, O: o, A: l.Min(f => Math.Min(T(f.Line.A), T(f.Line.B))), B: l.Max(f => Math.Max(T(f.Line.A), T(f.Line.B))));
        }).ToList();
        var used = new HashSet<int>(); var boxes = new List<(Polygon2 Box, List<Segment2> Edges)>();
        var lowered = new Dictionary<ContextFront, double>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < groups.Count; i++)
        {
            if (used.Contains(i)) continue;
            var g = groups[i]; used.Add(i);
            double a = g.A, b = g.B, depth = 0; int pair = -1;
            for (int j = 0; j < groups.Count; j++)
            {
                if (used.Contains(j)) continue;
                var h = groups[j];
                if (Math.Abs(h.U.Dot(g.U)) < Math.Cos(5 * Math.PI / 180) || h.N.Dot(g.N) > -0.9) continue;
                double d = (h.O - g.O).Dot(g.N);
                if (d < PairMinMm || d > MaxDepthMm) continue;
                double ha = Math.Min((h.O + h.U * h.A - g.O).Dot(g.U), (h.O + h.U * h.B - g.O).Dot(g.U));
                double hb = Math.Max((h.O + h.U * h.A - g.O).Dot(g.U), (h.O + h.U * h.B - g.O).Dot(g.U));
                double over = Math.Min(b, hb) - Math.Max(a, ha);
                if (over < 0.5 * Math.Min(b - a, hb - ha)) continue;
                pair = j; depth = d; a = Math.Min(a, ha); b = Math.Max(b, hb); break;
            }
            if (pair >= 0) used.Add(pair);
            else
            {
                // as deep as this building beside it
                double far = outlines.SelectMany(o => o.Vertices).Select(v => (v - g.O).Dot(g.N)).DefaultIfEmpty(0).Max();
                depth = Math.Clamp(far, MinDepthMm, MaxDepthMm);
            }
            var all = pair >= 0 ? g.Fronts.Concat(groups[pair].Fronts).ToList() : g.Fronts;
            double top = all.Max(f => f.TopZ), baseZ = all.Min(f => f.BaseZ);
            Vec2 P(double t, double s) => g.O + g.U * t + g.N * s;
            // a side within 1.5 m of a wall of this building runs onto it (the party wall: no slot between them)
            double Snap(double t)
            {
                var side = new Segment2(P(t, 0), P(t, depth));
                // this building's lines there, grouped by position (250 mm); a party line is where 5 m or more of them
                // line up (floors repeat it), not a short stray wall nearer the neighbour
                var at = ours.Where(l => Alongside(l, new[] { side }, SnapMm))
                    .Select(l => (X: (l.Mid - g.O).Dot(g.U), L: l.Length)).Where(p => Math.Abs(p.X - t) <= SnapMm).OrderBy(p => p.X).ToList();
                var lines = new List<(double Sum, double W, double L)>();
                foreach (var p in at)
                    if (lines.Count > 0 && p.X - lines[^1].Sum / lines[^1].W <= 250) lines[^1] = (lines[^1].Sum + p.X * p.L, lines[^1].W + p.L, lines[^1].L + p.L);
                    else lines.Add((p.X * p.L, p.L, p.L));
                var near = lines.Where(c => c.L >= PartyLineMm).Select(c => c.Sum / c.W).OrderBy(x => Math.Abs(x - t)).ToList();
                return near.Count > 0 ? near[0] : t;
            }
            a = Snap(a); b = Snap(b);
            var box = new Polygon2(new[] { P(a, 0), P(b, 0), P(b, depth), P(a, depth) });
            var edges = new List<Segment2> { new(P(a, 0), P(b, 0)), new(P(b, 0), P(b, depth)), new(P(b, depth), P(a, depth)), new(P(a, depth), P(a, 0)) };
            boxes.Add((box, edges));
            string note = $"Context block: {g.Fronts[0].Note.Split(',')[0]}" + (pair >= 0 ? $" and {groups[pair].Fronts[0].Note.Split(',')[0]}" : $", {depth / 1000:0.0} m deep (as this building)");
            // the front lines' gaps (no step drawn there), the sides, and the back when no front stands there
            foreach (var (line, mine) in new[] { (edges[0], g.Fronts), (edges[2], pair >= 0 ? groups[pair].Fronts : new List<ContextFront>()) })
                foreach (var gap in Gaps(line, mine.Select(f => f.Line)))
                    Wall(gap, baseZ, top, note);
            var mid = P((a + b) / 2, depth / 2);
            Wall(edges[1], baseZ, top, note, mid); Wall(edges[3], baseZ, top, note, mid);
            // a front that starts higher than the block (its lowest drawn line is up a storey) goes down to the block's foot
            foreach (var f in all) lowered[f] = baseZ;
            roofs.Add(new RoofCandidate(box, top, note));
            notes.Add($"{note}: corners {string.Join(" ", box.Vertices.Select(v => $"{v.X:0},{v.Y:0}"))}, top {top / 1000:0.00} m.");
        }
        int dropped = 0;
        foreach (var r in blocks)
        {
            var vs = r.Outline.Vertices;
            if (boxes.Any(bx => vs.Count(v => FacadeWalls.Contains(bx.Box, v) || bx.Edges.Any(e => Dist(v, e) <= 500)) >= 0.5 * vs.Count)) { dropped++; continue; }
            for (int i = 0; i < vs.Count; i++)
            {
                var e = new Segment2(vs[i], vs[(i + 1) % vs.Count]);
                if (Alongside(e, boxes.SelectMany(bx => bx.Edges), CoverReachMm)) continue;
                Wall(e, double.NaN, r.ElevationMm, "Context wall: " + r.Note, vs.Aggregate(new Vec2(0, 0), (s, v) => s + v) * (1.0 / vs.Count));
            }
            roofs.Add(r with { Note = "Context roof: " + r.Note });
        }
        notes.Add($"Context: {boxes.Count} blocks from the elevations' fronts, {roofs.Count - boxes.Count} roof-plan blocks"
                  + (dropped > 0 ? $" ({dropped} inside a front block left out)" : "") + (party > 0 ? $"; {party} walls along this building's party walls set just outside them (the neighbour's half)" : "") + ".");
        return new ContextPlan(fronts.Select(f => lowered.TryGetValue(f, out var z) && z < f.BaseZ ? f with { BaseZ = z } : f).ToList(), walls, roofs, notes);
    }

    static double Dist(Vec2 p, Segment2 s)
    {
        var d = s.B - s.A; double l2 = d.Dot(d);
        double t = l2 == 0 ? 0 : Math.Clamp((p - s.A).Dot(d) / l2, 0, 1);
        return p.DistanceTo(s.A + d * t);
    }

    /// <summary>The parts of a line no segment along it covers (pieces 300 mm or longer).</summary>
    static IEnumerable<Segment2> Gaps(Segment2 line, IEnumerable<Segment2> covers)
    {
        var u = line.Direction; double len = line.Length;
        var spans = covers.Select(c => (Math.Max(0, Math.Min((c.A - line.A).Dot(u), (c.B - line.A).Dot(u))), Math.Min(len, Math.Max((c.A - line.A).Dot(u), (c.B - line.A).Dot(u)))))
            .Where(s => s.Item2 > s.Item1).OrderBy(s => s.Item1).ToList();
        double at = 0;
        foreach (var (s0, s1) in spans)
        {
            if (s0 - at >= 300) yield return new Segment2(line.A + u * at, line.A + u * s0);
            at = Math.Max(at, s1);
        }
        if (len - at >= 300) yield return new Segment2(line.A + u * at, line.B);
    }

    /// <summary>
    /// A block's walls the fronts replace: of its covered edges, only the one nearest each front's line (a shallow block's
    /// back wall, also within reach, stays).
    /// </summary>
    public static HashSet<int> CoveredEdges(IReadOnlyList<Segment2> edges, IReadOnlyList<ContextFront> fronts)
    {
        var res = new HashSet<int>();
        foreach (var f in fronts)
        {
            int best = -1; double bestOff = double.MaxValue;
            for (int i = 0; i < edges.Count; i++)
            {
                if (!Covered(edges[i], new[] { f }) && !Covered(edges[i], fronts.Where(o => o.Facade == f.Facade))) continue;
                double off = Math.Abs(SegmentCleaner.Offset(f.Line, edges[i].Mid));
                if (off < bestOff) { bestOff = off; best = i; }
            }
            if (best >= 0) res.Add(best);
        }
        return res;
    }
}

