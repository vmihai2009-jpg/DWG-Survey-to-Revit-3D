namespace SurveyToRevit.Core.Survey;

/// <summary>How a wall stands against a roof's outline in plan (item D of MCP-AUDIT-AND-TOOLS.md).</summary>
public enum RoofWallPlace { Outside, Inner, Edge, Crossing }

/// <summary>
/// Item D, the plan part: a wall cut by a roof is either under it (an inner partition: stop it under the roof),
/// along its edge (an outer wall or parapet: leave it and let the person decide, it may be an upstand) or crossing
/// the edge (half in, half out: a clash to report). Revit-free and tested; the Revit side only attaches tops.
/// </summary>
public static class RoofWalls
{
    /// <summary>A wall line within this of the roof's outline is on its edge.</summary>
    public const double EdgeMm = 150;

    public static bool Inside(Polygon2 poly, Vec2 p)
    {
        bool inside = false;
        var v = poly.Vertices;
        for (int i = 0, j = v.Count - 1; i < v.Count; j = i++)
            if ((v[i].Y > p.Y) != (v[j].Y > p.Y) && p.X < (v[j].X - v[i].X) * (p.Y - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X)
                inside = !inside;
        return inside;
    }

    public static double ToEdge(Polygon2 poly, Vec2 p)
    {
        double best = double.MaxValue;
        var v = poly.Vertices;
        for (int i = 0, j = v.Count - 1; i < v.Count; j = i++)
        {
            var a = v[j]; var b = v[i]; var d = b - a;
            double len2 = d.X * d.X + d.Y * d.Y;
            double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / len2, 0, 1);
            best = Math.Min(best, p.DistanceTo(new Vec2(a.X + t * d.X, a.Y + t * d.Y)));
        }
        return best;
    }

    /// <summary>
    /// Samples the wall at its ends, quarters and middle. All inside and clear of the edge: Inner. All within
    /// EdgeMm plus half the wall of the edge: Edge. Some in, some out: Crossing. None in: Outside.
    /// </summary>
    public static RoofWallPlace Place(Segment2 wall, double thicknessMm, Polygon2 roof)
    {
        double tol = EdgeMm + thicknessMm / 2;
        int inner = 0, edge = 0, outside = 0;
        foreach (var t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var p = new Vec2(wall.A.X + t * (wall.B.X - wall.A.X), wall.A.Y + t * (wall.B.Y - wall.A.Y));
            if (ToEdge(roof, p) <= tol) edge++;
            else if (Inside(roof, p)) inner++;
            else outside++;
        }
        if (edge >= 4) return RoofWallPlace.Edge;
        if (outside == 0) return RoofWallPlace.Inner; // the ends may meet the outer wall: that's still under the roof
        if (inner == 0) return edge > 0 ? RoofWallPlace.Crossing : RoofWallPlace.Outside;
        return RoofWallPlace.Crossing;
    }
}
