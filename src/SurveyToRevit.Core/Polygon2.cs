namespace SurveyToRevit.Core;

/// <summary>A closed plan outline (e.g. a slab boundary), in mm. Last vertex is not repeated.</summary>
public sealed class Polygon2
{
    public IReadOnlyList<Vec2> Vertices { get; }

    public Polygon2(IEnumerable<Vec2> vertices) => Vertices = vertices.ToList();

    /// <summary>Positive when vertices run anticlockwise.</summary>
    public double SignedArea
    {
        get
        {
            double s = 0;
            for (int i = 0; i < Vertices.Count; i++)
                s += Vertices[i].Cross(Vertices[(i + 1) % Vertices.Count]);
            return s / 2.0;
        }
    }

    public double Area => Math.Abs(SignedArea);

    /// <summary>True when no two edges cross (Revit refuses slab and ceiling boundaries that cross themselves).</summary>
    public bool IsSimple()
    {
        int n = Vertices.Count;
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue; // neighbours through the closing edge
                Vec2 a = Vertices[i], b = Vertices[(i + 1) % n], c = Vertices[j], d = Vertices[(j + 1) % n];
                double d1 = (b - a).Cross(c - a), d2 = (b - a).Cross(d - a), d3 = (d - c).Cross(a - c), d4 = (d - c).Cross(b - c);
                if (d1 * d2 < 0 && d3 * d4 < 0) return false;
            }
        return true;
    }

    /// <summary>
    /// Builds a polygon from linework that should form a closed loop, e.g. a
    /// polyline exploded into segments. Returns null and a reason if it does not close.
    /// </summary>
    public static Polygon2? FromSegments(IReadOnlyList<Segment2> segs, double snapTol, out string? problem)
    {
        problem = null;
        if (segs.Count < 3) { problem = "Fewer than 3 edges."; return null; }
        var remaining = segs.ToList();
        var pts = new List<Vec2> { remaining[0].A };
        var cur = remaining[0].B;
        remaining.RemoveAt(0);
        while (remaining.Count > 0)
        {
            pts.Add(cur);
            int k = remaining.FindIndex(s => s.A.DistanceTo(cur) <= snapTol || s.B.DistanceTo(cur) <= snapTol);
            if (k < 0) { problem = $"Outline is open near {cur}."; return null; }
            var next = remaining[k];
            cur = next.A.DistanceTo(cur) <= snapTol ? next.B : next.A;
            remaining.RemoveAt(k);
        }
        if (cur.DistanceTo(pts[0]) > snapTol) { problem = $"Outline is open: gap of {cur.DistanceTo(pts[0]):0} mm."; return null; }
        return new Polygon2(pts);
    }

    /// <summary>Checks the outline can safely become a floor. Empty list means valid.</summary>
    public List<string> Validate(double minEdge = 1.0, double minAreaM2 = 0.5, double maxAreaM2 = 100_000)
    {
        var issues = new List<string>();
        int n = Vertices.Count;
        if (n < 3) { issues.Add("Fewer than 3 vertices."); return issues; }

        for (int i = 0; i < n; i++)
            if (Vertices[i].DistanceTo(Vertices[(i + 1) % n]) < minEdge)
                issues.Add($"Duplicate vertex at {Vertices[i]}.");

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (j == i + 1 || (i == 0 && j == n - 1)) continue; // neighbours share a vertex
                if (SegmentsCross(Vertices[i], Vertices[(i + 1) % n], Vertices[j], Vertices[(j + 1) % n]))
                    issues.Add($"Edges {i} and {j} cross (self-intersection).");
            }

        var m2 = Area / 1e6;
        if (m2 < minAreaM2) issues.Add($"Area {m2:0.##} m² is too small.");
        if (m2 > maxAreaM2) issues.Add($"Area {m2:0} m² is unreasonably large (check units).");
        return issues;
    }

    static bool SegmentsCross(Vec2 p1, Vec2 p2, Vec2 p3, Vec2 p4)
    {
        double d1 = (p4 - p3).Cross(p1 - p3), d2 = (p4 - p3).Cross(p2 - p3);
        double d3 = (p2 - p1).Cross(p3 - p1), d4 = (p2 - p1).Cross(p4 - p1);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
