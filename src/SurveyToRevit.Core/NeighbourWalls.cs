namespace SurveyToRevit.Core;

/// <summary>
/// Walls read only from an outline layer (one that surveyors also use for the buildings next door, such as BUILDINGS)
/// that stand away from everything the survey drew inside this building: the neighbours' outlines, not ours.
/// "Inside this building" is the convex hull of the plan's own linework (walls on wall layers that are not outline
/// layers, doors, stairs), grown by a margin. With too little of that linework nothing is removed.
/// </summary>
public static class NeighbourWalls
{
    public static List<WallCandidate> Remove(IReadOnlyList<WallCandidate> walls, IEnumerable<Vec2> ownLinework, Func<string, bool> outlineLayer,
        out int removed, double marginMm = 1500, int minPoints = 40)
    {
        removed = 0;
        var pts = ownLinework.ToList();
        if (pts.Count < minPoints) return walls.ToList();
        var hull = Hull(pts);
        if (hull.Count < 3) return walls.ToList();
        var kept = new List<WallCandidate>();
        foreach (var w in walls)
        {
            bool fromOutline = outlineLayer(w.Centerline.Layer) && !w.Curtain;
            if (fromOutline && Outside(hull, w.Centerline.Mid) > marginMm) { removed++; continue; }
            kept.Add(w);
        }
        return kept;
    }

    /// <summary>How far p lies outside the convex polygon (counter-clockwise); 0 inside.</summary>
    public static double Outside(IReadOnlyList<Vec2> hull, Vec2 p)
    {
        bool inside = true;
        for (int i = 0; i < hull.Count; i++)
            if ((hull[(i + 1) % hull.Count] - hull[i]).Cross(p - hull[i]) < 0) { inside = false; break; }
        if (inside) return 0;
        double best = double.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i]; var d = hull[(i + 1) % hull.Count] - a; double len2 = d.Dot(d);
            double t = len2 == 0 ? 0 : Math.Clamp((p - a).Dot(d) / len2, 0, 1);
            best = Math.Min(best, p.DistanceTo(a + d * t));
        }
        return best;
    }

    /// <summary>Convex hull, counter-clockwise (monotone chain).</summary>
    public static List<Vec2> Hull(List<Vec2> points)
    {
        var p = points.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (p.Count < 3) return p;
        var h = new List<Vec2>();
        foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
        {
            int start = h.Count;
            foreach (var v in pass)
            {
                while (h.Count >= start + 2 && (h[^1] - h[^2]).Cross(v - h[^2]) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(v);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }
}
