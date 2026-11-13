namespace SurveyToRevit.Core.Survey;

/// <summary>One step of a neighbour's front read off an elevation: a stretch of the facade line (model mm) and its height.</summary>
public sealed record ContextFront(string Facade, Segment2 Line, double BaseZ, double TopZ, List<ContextWindow> Windows, string Note);

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
    public const double StripMm = 250, StepMm = 400, MinStepMm = 2000, InsideMm = 1500, MinFrontMm = 3000, MinHeightMm = 2500;

    static bool Skyline(LayerRole r) => r is not (LayerRole.Annotation or LayerRole.LevelText or LayerRole.HeightText
        or LayerRole.RoomName or LayerRole.Fitting or LayerRole.Area or LayerRole.Overhead);

    public static List<ContextFront> Read(IReadOnlyList<ElevationDrawing> elevations, IReadOnlyList<SurveyLevel> levels, LayerRules rules)
    {
        var res = new List<ContextFront>();
        var outlines = levels.SelectMany(l => (l.Outline != null ? new[] { l.Outline } : Array.Empty<Polygon2>()).Concat(l.MoreOutlines)).ToList();
        if (outlines.Count == 0) return res;
        var centre = outlines[0].Vertices.Aggregate(new Vec2(0, 0), (a, v) => a + v) * (1.0 / outlines[0].Vertices.Count);
        double lowest = levels.Where(l => !double.IsNaN(l.ElevationMm)).Select(l => l.ElevationMm).DefaultIfEmpty(0).Min();
        // only a sure match places a neighbour: a drawing a bay off would put next door's front inside this building
        foreach (var d in elevations.Where(e => e.FacadeLine != null && e.Score >= ElevationIndex.SureScore && e.Parts.Count > 0 && e.Lines.Count > 0))
        {
            var fl = d.FacadeLine!;
            var facade = new Segment2(new Vec2(fl[0], fl[1]), new Vec2(fl[2], fl[3]));
            var dir = facade.Direction; double len = facade.Length;
            var n = new Vec2(-dir.Y, dir.X);
            var inward = (centre - facade.Mid).X * n.X + (centre - facade.Mid).Y * n.Y > 0 ? n : new Vec2(-n.X, -n.Y);
            Vec2 At(double t) => facade.A + dir * t;
            bool Ours(double t) => outlines.Any(o => FacadeWalls.Contains(o, At(t) + inward * InsideMm));
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
                double baseZ = Math.Max(lowest, strips.Min(s => s.Base));
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
                        $"{d.Title.Replace(" ", "")} {(side < 0 ? "left" : "right")} of {d.Facade}, {(st.T1 - st.T0) / 1000:0.0} m wide, top {st.Top / 1000:0.00} m, {ws.Count} windows"));
                }
            }
        }
        return res;
    }

    /// <summary>A roof-plan block's wall that an elevation front already shows (parallel within 10°, within 1.5 m, half its length alongside): left out.</summary>
    public static bool Covered(Segment2 edge, IEnumerable<ContextFront> fronts)
    {
        double over = 0;
        foreach (var f in fronts)
        {
            var d = f.Line.Direction; var e = edge.Direction;
            if (Math.Abs(d.X * e.X + d.Y * e.Y) < Math.Cos(10 * Math.PI / 180)) continue;
            if (Math.Abs(SegmentCleaner.Offset(f.Line, edge.Mid)) > 1500) continue;
            double T(Vec2 p) => (p.X - f.Line.A.X) * d.X + (p.Y - f.Line.A.Y) * d.Y;
            double a = Math.Min(T(edge.A), T(edge.B)), b = Math.Max(T(edge.A), T(edge.B));
            over += Math.Max(0, Math.Min(b, f.Line.Length) - Math.Max(a, 0));
        }
        return over >= 0.5 * edge.Length;
    }
}
