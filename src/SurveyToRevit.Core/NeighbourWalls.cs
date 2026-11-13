namespace SurveyToRevit.Core;

/// <summary>
/// Walls read only from an outline layer (one that surveyors also use for the buildings next door, such as BUILDINGS)
/// that stand away from everything the survey drew inside this building: the neighbours' outlines, not ours.
/// "Inside this building" is the convex hull of the plan's own linework (walls on wall layers that are not outline
/// layers, doors, stairs), grown by a margin. With too little of that linework nothing is removed.
/// </summary>
public static class NeighbourWalls
{
    /// <summary>Line tags the reading gives walls it makes itself (PierWalls, FacadeWalls, WallDetector bridges and joins).</summary>
    static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase) { "pier", "facade", "bridge", "facade join", "window opening" };

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
            // read from the outline layer, or made by the reading itself from outline material (piers, façade runs, bridges,
            // joins: their line no longer carries a layer)
            bool fromOutline = !w.Curtain && (outlineLayer(w.Centerline.Layer) || Generated.Contains(w.Centerline.Layer));
            if (fromOutline && Outside(hull, w.Centerline.Mid) > marginMm) { removed++; continue; }
            kept.Add(w);
        }
        return kept;
    }

    /// <summary>
    /// The building's envelope: the convex hull of its walls' corners (each wall's centre line pushed out by half its
    /// thickness both ways), counter-clockwise; null with too few walls.
    /// </summary>
    public static List<Vec2>? Envelope(IEnumerable<WallCandidate> walls)
    {
        var pts = new List<Vec2>();
        foreach (var w in walls)
        {
            var n = w.Centerline.Direction.Perp() * (w.TypeThickness / 2);
            pts.Add(w.Centerline.A + n); pts.Add(w.Centerline.A - n); pts.Add(w.Centerline.B + n); pts.Add(w.Centerline.B - n);
        }
        if (pts.Count < 12) return null;
        var h = Hull(pts);
        return h.Count >= 3 ? h : null;
    }

    /// <summary>
    /// A floor outline cut back to the building's envelope (Sutherland-Hodgman against the convex envelope): the part of
    /// an outline that ran on over the buildings next door is dropped. Null when nothing would be left.
    /// </summary>
    public static Polygon2? Clip(Polygon2 outline, IReadOnlyList<Vec2> envelope)
    {
        var pts = outline.SignedArea < 0 ? outline.Vertices.Reverse().ToList() : outline.Vertices.ToList();
        for (int i = 0; i < envelope.Count && pts.Count > 0; i++)
        {
            var a = envelope[i]; var b = envelope[(i + 1) % envelope.Count]; var e = b - a;
            bool In(Vec2 p) => e.Cross(p - a) >= -1;
            Vec2 Cut(Vec2 p, Vec2 q) { double dp = e.Cross(p - a), dq = e.Cross(q - a); double t = dp / (dp - dq); return p + (q - p) * t; }
            var next = new List<Vec2>();
            for (int k = 0; k < pts.Count; k++)
            {
                var p = pts[k]; var q = pts[(k + 1) % pts.Count];
                if (In(q)) { if (!In(p)) next.Add(Cut(p, q)); next.Add(q); }
                else if (In(p)) next.Add(Cut(p, q));
            }
            pts = next;
        }
        // drop repeated points the cuts can leave
        var clean = new List<Vec2>();
        foreach (var p in pts) if (clean.Count == 0 || clean[^1].DistanceTo(p) > 1) clean.Add(p);
        if (clean.Count > 1 && clean[0].DistanceTo(clean[^1]) <= 1) clean.RemoveAt(clean.Count - 1);
        var poly = new Polygon2(clean);
        return clean.Count >= 3 && poly.Area > 1e6 ? poly : null;
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
