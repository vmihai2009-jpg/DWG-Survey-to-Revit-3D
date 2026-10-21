namespace SurveyToRevit.Core;

/// <summary>
/// Room separation lines so that every room the drawing shows is closed in the model:
/// (1) lines on wall layers that did not become walls (a screen, a short return, a nib
/// drawn as one line) where they border a room, and (2) short gaps where a wall stops
/// just short of the next one. Same rules for any survey.
/// </summary>
public static class RoomBounds
{
    /// <summary>
    /// Blocks that close the short gaps where a wall stops just short of the next one, each as thick
    /// as the wall that stops (built as generic models: the walls then read as joined).
    /// </summary>
    public static List<Polygon2> GapFills(IReadOnlyList<WallCandidate> walls, Func<Vec2, bool>? isRoom, double maxGap = 600)
    {
        var fills = new List<Polygon2>();
        Separations(walls, Array.Empty<Segment2>(), isRoom, maxGap: maxGap, fills: fills);
        return fills;
    }

    static Polygon2 Block(Vec2 a, Vec2 b, double width)
    {
        var n = (b - a).Normalized().Perp() * (width / 2);
        return new Polygon2(new[] { a - n, b - n, b + n, a + n });
    }

    public static List<Segment2> Separations(IReadOnlyList<WallCandidate> walls, IReadOnlyList<Segment2> wallLines, Func<Vec2, bool>? isRoom,
        double minLength = 300, double maxGap = 600, double probe = 150, double parallelReach = 450, List<Polygon2>? fills = null)
    {
        var result = new List<Segment2>();
        if (isRoom == null) return result;

        // 1. drawn lines no wall covers, with a room on at least one side
        foreach (var l in wallLines.Where(l => l.Length >= minLength))
        {
            var n = l.Direction.Perp();
            // a line alongside a wall (its other face, a lining, a reveal) is that wall, not a room edge
            var alongside = walls.Where(w => w.Arc == null && SegmentCleaner.AngleDiffDeg(w.Centerline.AngleDeg, l.AngleDeg) < 10).ToList();
            // the uncovered stretches of the line, in 100 mm steps
            int steps = Math.Max(2, (int)Math.Ceiling(l.Length / 100));
            int runStart = -1;
            for (int i = 0; i <= steps; i++)
            {
                var p = l.A + (l.B - l.A) * ((double)i / steps);
                bool open = i < steps && !walls.Any(w => Gap(w, p) <= 50) && !alongside.Any(w => Gap(w, p) <= parallelReach)
                            && (isRoom(p + n * probe) || isRoom(p - n * probe));
                if (open && runStart < 0) runStart = i;
                if (!open && runStart >= 0)
                {
                    var a = l.A + (l.B - l.A) * ((double)runStart / steps);
                    var b = l.A + (l.B - l.A) * ((double)i / steps);
                    if (a.DistanceTo(b) >= minLength) result.Add(new Segment2(a, b, "room separation", "drawn line"));
                    runStart = -1;
                }
            }
        }

        // 2. a free wall end that stops short of another wall: closed straight across
        foreach (var w in walls)
        {
            double len = WallGeometry.Length(w);
            foreach (var (along, sign) in new[] { (0.0, -1.0), (len, 1.0) })
            {
                var p = WallGeometry.PointAt(w, along);
                double a = WallGeometry.AngleDegAt(w, along) * Math.PI / 180;
                var dir = new Vec2(Math.Cos(a), Math.Sin(a)) * sign;
                var tip = p + dir * (w.TypeThickness / 2);
                if (walls.Any(o => !ReferenceEquals(o, w) && Gap(o, tip + dir * 30) <= 10)) continue; // meets a wall
                // the nearest other wall within reach, straight ahead or to the side
                Vec2? hit = null; double best = maxGap;
                foreach (var o in walls)
                {
                    if (ReferenceEquals(o, w) || o.Arc != null) continue;
                    var q = Nearest(o, tip);
                    double d = q.DistanceTo(tip);
                    if (d > 20 && d < best && (q - tip).Dot(dir) > -w.TypeThickness / 2) { best = d; hit = q; }
                }
                if (hit is Vec2 h && isRoom((tip + h) * 0.5 + (h - tip).Perp().Normalized() * probe) | isRoom((tip + h) * 0.5 - (h - tip).Perp().Normalized() * probe))
                {
                    result.Add(new Segment2(tip, h, "room separation", "wall gap"));
                    fills?.Add(Block(tip - dir * 20, h + (h - tip).Normalized() * 20, w.TypeThickness));
                }
            }
        }
        return result;
    }

    /// <summary>Distance from a point to a wall's body (0 inside it).</summary>
    public static double Gap(WallCandidate w, Vec2 p)
    {
        double along = WallGeometry.Along(w, p), len = WallGeometry.Length(w);
        double off = Math.Max(0, Math.Abs(WallGeometry.Offset(w, p)) - w.TypeThickness / 2);
        double beyond = along < 0 ? -along : along > len ? along - len : 0;
        return Math.Sqrt(off * off + beyond * beyond);
    }

    // nearest point on a straight wall's face (the body edge) to p
    static Vec2 Nearest(WallCandidate w, Vec2 p)
    {
        var c = w.Centerline;
        double len = c.Length;
        double t = Math.Clamp((p - c.A).Dot(c.Direction), 0, len);
        var on = c.A + c.Direction * t;
        double off = (p - on).Dot(c.Direction.Perp());
        double side = Math.Sign(off) * Math.Min(Math.Abs(off), w.TypeThickness / 2);
        return on + c.Direction.Perp() * side;
    }
}
