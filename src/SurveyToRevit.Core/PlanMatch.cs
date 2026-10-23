namespace SurveyToRevit.Core;

/// <summary>A turn about the origin then a shift, in plan (mm, radians), and how well it lines things up.</summary>
public readonly record struct PlanMove(double Angle, double Dx, double Dy, double Score)
{
    public Vec2 Apply(Vec2 p)
    {
        double c = Math.Cos(Angle), s = Math.Sin(Angle);
        return new Vec2(c * p.X - s * p.Y + Dx, s * p.X + c * p.Y + Dy);
    }
    public double AngleDeg => Angle * 180 / Math.PI;
    public double ShiftMm => Math.Sqrt(Dx * Dx + Dy * Dy);
}

/// <summary>
/// Finds where a model's walls sit on the survey's walls (for a model drawn somewhere else: its own origin, an old
/// survey, a consultant's model), as a turn and a shift. The turn is read from the main wall directions of both
/// (four candidates, a quarter turn apart), the shift from where most wall points agree, then both are refined on the
/// matched points. Score = share of the model's wall points within 100 mm of a survey wall afterwards.
/// </summary>
public static class PlanMatch
{
    public static PlanMove Fit(IReadOnlyList<Segment2> survey, IReadOnlyList<Segment2> model, double rangeMm = 300_000)
    {
        if (survey.Count == 0 || model.Count == 0) return new PlanMove(0, 0, 0, 0);
        var index = new PointIndex(survey, 100, 250);
        var coarseTarget = new HashSet<(int, int)>(Sample(survey, 500).Select(p => Cell(p, 500)));
        var moving = Sample(model, 500);
        var fine = Sample(model, 100);
        double baseTurn = Direction(survey) - Direction(model);

        PlanMove best = new(0, 0, 0, -1);
        for (int k = 0; k < 4; k++)
        {
            double angle = baseTurn + k * Math.PI / 2;
            double c = Math.Cos(angle), s = Math.Sin(angle);
            // the shift most model points agree on: every pairing of a model point with a survey point votes
            var votes = new Dictionary<(int, int), int>();
            var targets = coarseTarget.Select(t => new Vec2(t.Item1 * 500.0, t.Item2 * 500.0)).ToList();
            var step = Math.Max(1, moving.Count / 600);
            for (int i = 0; i < moving.Count; i += step)
            {
                var p = moving[i];
                var r = new Vec2(c * p.X - s * p.Y, s * p.X + c * p.Y);
                foreach (var t in targets)
                {
                    var d = t - r;
                    if (Math.Abs(d.X) > rangeMm || Math.Abs(d.Y) > rangeMm) continue;
                    var key = Cell(d, 500);
                    votes[key] = votes.TryGetValue(key, out var v) ? v + 1 : 1;
                }
            }
            if (votes.Count == 0) continue;
            // the best few shifts, each refined; keep the one that lines up most
            foreach (var top in votes.OrderByDescending(v => v.Value).Take(5))
            {
                var move = Refine(new PlanMove(angle, top.Key.Item1 * 500.0, top.Key.Item2 * 500.0, 0), fine, index);
                if (move.Score > best.Score) best = move;
            }
        }
        // the turn kept within ±180°
        double a = Math.IEEERemainder(best.Angle, 2 * Math.PI);
        return best with { Angle = a };
    }

    /// <summary>Closest-point refinement (turn and shift together), from wide matches to 100 mm ones.</summary>
    static PlanMove Refine(PlanMove move, List<Vec2> points, PointIndex index)
    {
        foreach (var reach in new[] { 1000.0, 600, 400, 250, 150, 150, 100, 100 })
        {
            var pairs = new List<(Vec2 P, Vec2 Q)>();
            foreach (var p in points)
            {
                var q = index.Nearest(move.Apply(p), reach);
                if (q is Vec2 qq) pairs.Add((p, qq));
            }
            if (pairs.Count < 10) break;
            // best rigid fit of the pairs (2D Procrustes)
            double mpx = pairs.Average(x => x.P.X), mpy = pairs.Average(x => x.P.Y), mqx = pairs.Average(x => x.Q.X), mqy = pairs.Average(x => x.Q.Y);
            double sxx = 0, sxy = 0;
            foreach (var (p, q) in pairs)
            {
                double px = p.X - mpx, py = p.Y - mpy, qx = q.X - mqx, qy = q.Y - mqy;
                sxx += px * qx + py * qy;
                sxy += px * qy - py * qx;
            }
            double angle = Math.Atan2(sxy, sxx), c = Math.Cos(angle), s = Math.Sin(angle);
            move = new PlanMove(angle, mqx - (c * mpx - s * mpy), mqy - (s * mpx + c * mpy), 0);
        }
        int near = points.Count(p => index.Nearest(move.Apply(p), 100) != null);
        return move with { Score = points.Count == 0 ? 0 : near / (double)points.Count };
    }

    /// <summary>The main wall direction (radians, folded to a quarter turn), weighted by length.</summary>
    internal static double Direction(IReadOnlyList<Segment2> walls)
    {
        // directions folded ×4 so walls at right angles agree, then averaged as vectors
        double x = 0, y = 0;
        foreach (var w in walls)
        {
            var d = w.B - w.A;
            double a = Math.Atan2(d.Y, d.X) * 4, l = w.Length;
            x += Math.Cos(a) * l; y += Math.Sin(a) * l;
        }
        return Math.Atan2(y, x) / 4;
    }

    static List<Vec2> Sample(IEnumerable<Segment2> walls, double step)
    {
        var points = new List<Vec2>();
        foreach (var w in walls)
        {
            int n = Math.Max(1, (int)Math.Ceiling(w.Length / step));
            for (int i = 0; i <= n; i++) points.Add(w.A + (w.B - w.A) * (i / (double)n));
        }
        return points;
    }

    static (int, int) Cell(Vec2 p, double size) => ((int)Math.Round(p.X / size), (int)Math.Round(p.Y / size));

    /// <summary>Survey walls found by place; a point's match is the nearest point on a wall (not on a sample, which would pull it along).</summary>
    sealed class PointIndex
    {
        readonly Dictionary<(int, int), List<Segment2>> _cells = new();
        readonly double _size;
        public PointIndex(IEnumerable<Segment2> walls, double step, double size)
        {
            _size = size;
            foreach (var w in walls)
                foreach (var p in Sample(new[] { w }, step))
                {
                    var key = ((int)Math.Floor(p.X / size), (int)Math.Floor(p.Y / size));
                    if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<Segment2>();
                    if (list.Count == 0 || !ReferenceEquals(list[^1], w)) list.Add(w);
                }
        }
        static Vec2 OnWall(Segment2 w, Vec2 p)
        {
            var d = w.B - w.A;
            double l2 = d.Dot(d);
            double t = l2 == 0 ? 0 : Math.Clamp((p - w.A).Dot(d) / l2, 0, 1);
            return w.A + d * t;
        }
        public Vec2? Nearest(Vec2 p, double reach)
        {
            int r = (int)Math.Ceiling(reach / _size);
            int cx = (int)Math.Floor(p.X / _size), cy = (int)Math.Floor(p.Y / _size);
            Vec2? best = null; double bestD = reach;
            for (int i = cx - r; i <= cx + r; i++)
                for (int j = cy - r; j <= cy + r; j++)
                    if (_cells.TryGetValue((i, j), out var list))
                        foreach (var w in list)
                        {
                            var q = OnWall(w, p);
                            double d = q.DistanceTo(p);
                            if (d <= bestD) { bestD = d; best = q; }
                        }
            return best;
        }
    }
}
