namespace SurveyToRevit.Core;

/// <summary>
/// Finds the main directions a building is drawn in and straightens lines that
/// are nearly, but not quite, on one of them. This stops slightly crooked survey
/// lines from producing many separate, oddly angled walls.
/// </summary>
public static class AxisSnapper
{
    /// <summary>
    /// Main directions in degrees, [0, 180), strongest first. The strongest
    /// direction always comes with its perpendicular.
    /// </summary>
    public static List<double> FindAxes(IReadOnlyList<Segment2> segs, double snapDeg, double minShare = 0.03, int maxAxes = 8)
    {
        var axes = new List<double>();
        var remaining = segs.Where(s => s.Length > 0).ToList();
        double total = remaining.Sum(s => s.Length);
        if (total == 0) return axes;

        while (axes.Count < maxAxes && remaining.Count > 0)
        {
            // Strongest direction: the angle with most line length within ±snapDeg (0.25° steps).
            double best = 0, bestW = 0;
            for (double a = 0; a < 180; a += 0.25)
            {
                var w = remaining.Where(s => SegmentCleaner.AngleDiffDeg(s.AngleDeg, a) <= snapDeg).Sum(s => s.Length);
                if (w > bestW) { bestW = w; best = a; }
            }
            if (bestW < total * minShare) break;
            var axis = WeightedMeanAngle(remaining.Where(s => SegmentCleaner.AngleDiffDeg(s.AngleDeg, best) <= snapDeg).ToList(), best);
            Add(axes, axis, snapDeg);
            if (axes.Count == 1) Add(axes, Fold(axis + 90), snapDeg);
            remaining = remaining.Where(s => axes.All(x => SegmentCleaner.AngleDiffDeg(s.AngleDeg, x) > snapDeg)).ToList();
        }
        return axes;
    }

    /// <summary>Rotates each line about its midpoint onto the nearest axis within snapDeg; others are left alone.</summary>
    public static List<Segment2> Snap(IEnumerable<Segment2> segs, IReadOnlyList<double> axes, double snapDeg)
    {
        return segs.Select(s =>
        {
            if (axes.Count == 0) return s;
            var axis = axes.OrderBy(a => SegmentCleaner.AngleDiffDeg(s.AngleDeg, a)).First();
            var diff = SegmentCleaner.AngleDiffDeg(s.AngleDeg, axis);
            if (diff > snapDeg || diff < 1e-9) return s;
            var rad = axis * Math.PI / 180;
            var u = new Vec2(Math.Cos(rad), Math.Sin(rad));
            if (u.Dot(s.B - s.A) < 0) u = u * -1;
            var half = u * (s.Length / 2);
            return s with { A = s.Mid - half, B = s.Mid + half };
        }).ToList();
    }

    /// <summary>The building's main direction folded into (-45°, 45°]: the rotation between drawing and building grid.</summary>
    public static double MainRotationDeg(IReadOnlyList<double> axes)
    {
        if (axes.Count == 0) return 0;
        var a = axes[0] % 90;
        if (a > 45) a -= 90;
        return a;
    }

    static double WeightedMeanAngle(List<Segment2> segs, double near)
    {
        double sum = 0, w = 0;
        foreach (var s in segs)
        {
            var a = s.AngleDeg;
            if (a - near > 90) a -= 180;
            if (near - a > 90) a += 180;
            sum += a * s.Length;
            w += s.Length;
        }
        return Fold(sum / w);
    }

    static double Fold(double a)
    {
        a %= 180;
        return a < 0 ? a + 180 : a;
    }

    static void Add(List<double> axes, double a, double snapDeg)
    {
        if (axes.All(x => SegmentCleaner.AngleDiffDeg(x, a) > snapDeg)) axes.Add(a);
    }
}
