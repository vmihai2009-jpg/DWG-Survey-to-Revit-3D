namespace SurveyToRevit.Core;

/// <summary>
/// Piers, corner blocks and chimney breasts are often drawn as one solid outline rather than
/// two face lines, so no line pair finds them and the model shows a gap. Wall material the
/// walls found so far do not cover, next to one of them, becomes a wall: lined up with that
/// neighbour, as long as the piece runs along it and as thick as it is across (snapped to the
/// wall types). Only compact blocks (up to an opening's width long, filling most of their box)
/// count. Works from the material map, so it is the same for any survey.
/// </summary>
public static class PierWalls
{
    public static List<WallCandidate> Fill(List<WallCandidate> walls, Func<Func<Vec2, bool>, List<List<Vec2>>>? leftover, Tolerances tol,
        double cell = 25, double reach = 300)
    {
        if (leftover == null || walls.Count == 0) return walls;
        var pieces = leftover(p => walls.Any(w => Covers(w, p, 30)));
        var result = new List<WallCandidate>(walls);
        foreach (var cells in pieces)
        {
            var c = new Vec2(cells.Average(p => p.X), cells.Average(p => p.Y));
            // the neighbour it belongs to: the nearest wall within reach of the piece
            WallCandidate? near = null; double best = reach;
            foreach (var w in walls)
            {
                double d = cells.Where((_, i) => i % 7 == 0).Min(p => Gap(w, p));
                if (d < best) { best = d; near = w; }
            }
            if (near == null) continue;
            double angle = WallGeometry.AngleDegAt(near, Math.Clamp(WallGeometry.Along(near, c), 0, WallGeometry.Length(near))) * Math.PI / 180;
            var u = new Vec2(Math.Cos(angle), Math.Sin(angle));
            var v = new Vec2(-u.Y, u.X);
            double a0 = cells.Min(p => (p - c).Dot(u)) - cell / 2, a1 = cells.Max(p => (p - c).Dot(u)) + cell / 2;
            double b0 = cells.Min(p => (p - c).Dot(v)) - cell / 2, b1 = cells.Max(p => (p - c).Dot(v)) + cell / 2;
            double length = a1 - a0, thick = b1 - b0;
            if (thick < tol.MinWallThickness || length < 150) continue;
            // a piece much longer across than along is a stub wall at right angles: turn it
            if (thick > 2 * length) { (u, v) = (v, u * -1); (a0, a1, b0, b1) = (-b1, -b0, a0, a1); (length, thick) = (thick, length); }
            if (thick > tol.MaxWallThickness + 200) continue;
            // a pier is a compact block: a long strip is a missed wall run (left for the unclear-area
            // check), and a ragged shape that fills little of its box is not a wall piece at all
            if (length > tol.MaxOpeningWidth) continue;
            if (cells.Count * cell * cell < 0.6 * length * thick) continue;
            var mid = c + u * ((a0 + a1) / 2) + v * ((b0 + b1) / 2);
            var line = new Segment2(mid - u * (length / 2), mid + u * (length / 2), "pier", "pier");
            // a chunky pier still takes a standard type (the largest), so it adds no new wall type
            double type = WallDetector.SnapThickness(tol.WallTypeSizes.Count > 0 ? Math.Min(thick, tol.WallTypeSizes[^1]) : thick, tol.WallTypeSizes);
            result.Add(new WallCandidate(line, thick, type, Confidence.Low, "pier", "pier"));
        }
        return result;
    }

    static bool Covers(WallCandidate w, Vec2 p, double slack)
    {
        double along = WallGeometry.Along(w, p);
        return along >= -slack && along <= WallGeometry.Length(w) + slack && Math.Abs(WallGeometry.Offset(w, p)) <= w.TypeThickness / 2 + slack;
    }

    // distance from a point to a wall's body (0 inside it)
    static double Gap(WallCandidate w, Vec2 p)
    {
        double along = WallGeometry.Along(w, p), len = WallGeometry.Length(w);
        double off = Math.Max(0, Math.Abs(WallGeometry.Offset(w, p)) - w.TypeThickness / 2);
        double beyond = along < 0 ? -along : along > len ? along - len : 0;
        return Math.Sqrt(off * off + beyond * beyond);
    }
}
