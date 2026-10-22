namespace SurveyToRevit.Core.Survey;

/// <summary>A flat concept roof: an outline at a height above survey datum (mm).</summary>
public sealed record RoofCandidate(Polygon2 Outline, double ElevationMm, string Note);

/// <summary>A piece of detail built as a generic model: an outline extruded from a base (above its floor) by a height, in mm.</summary>
public sealed record DetailShape(LayerRole Role, Polygon2 Outline, double BaseMm, double HeightMm);

/// <summary>
/// Roofs and detail from what the plans show, the same way for any survey.
/// Roofs: only where a roof plan or a roof layer shows them, at their surveyed spot heights.
/// Detail: only odd pieces walls cannot show: closed shapes on column and chimney layers,
/// full storey height. Everything else on the plan is built as walls.
/// </summary>
public static class RoofsAndDetails
{
    /// <summary>
    /// Is this drawing a roof plan? Named so ("Roof Plan", level RF), or, with no floor in its name,
    /// mostly roof linework (as many roof-layer lines as half its wall lines, and at least 100).
    /// </summary>
    public static bool IsRoofPlan(SurveyDrawing d, LayerRules rules)
    {
        if (d.Name.LevelCode == "RF") return true;
        if (d.Name.LevelCode != null) return false;
        int roof = d.LayerCounts.Where(kv => rules.Classify(kv.Key) == LayerRole.Roof).Sum(kv => kv.Value);
        return roof >= 100 && roof * 2 >= d.WallLines.Count;
    }

    /// <summary>
    /// Roofs drawn on a plan, only where the drawing says so: on a roof plan, every area closed by
    /// roof and wall lines; on a floor plan, areas closed by roof-layer lines alone, outside the
    /// floor's own outline (a lower roof seen from above). Each is flat at the middle of the spot
    /// heights inside it; an area with no spot height is left out (its height would be a guess).
    /// </summary>
    public static List<RoofCandidate> FromPlan(SurveyDrawing d, SurveyFrame frame, LayerRules rules, bool roofPlan, Polygon2? floorOutline,
        out int withoutHeight, double cell = 50, double minArea = 2e6)
    {
        withoutHeight = 0;
        var lines = d.FeatureLines.Where(f => rules.Classify(f.Layer) == LayerRole.Roof);
        if (roofPlan) lines = lines.Concat(d.WallLines);
        var model = lines.Select(frame.ToModel).ToList();
        var result = new List<RoofCandidate>();
        if (model.Count < 3) return result;
        var spots = d.Texts.Where(t => rules.Classify(t.Layer) == LayerRole.LevelText)
            .Select(t => (P: frame.ToModel(t.Position), Mm: LevelEstimator.ParseMm(t.Value))).Where(x => x.Mm != null).Select(x => (x.P, Mm: x.Mm!.Value)).ToList();
        var grid = ShapeGrid.Around(model.SelectMany(s => new[] { s.A, s.B }), cell, 500);
        var drawn = grid.Draw(model);
        // the areas between the lines that the outside cannot reach
        var inside = ShapeGrid.Minus(grid.Enclosed(drawn), drawn);
        foreach (var piece in grid.Outlines(inside, minArea))
        {
            if (!roofPlan && floorOutline != null && FacadeWalls.Contains(floorOutline, Centroid(piece))) continue;
            var heights = spots.Where(sp => FacadeWalls.Contains(piece, sp.P)).Select(sp => sp.Mm).OrderBy(x => x).ToList();
            if (heights.Count == 0) { withoutHeight++; continue; }
            double h = heights[heights.Count / 2];
            result.Add(new RoofCandidate(piece, h, $"{(roofPlan ? "roof plan" : "roof layer")} {d.Name.FileName}, {heights.Count} spot heights {heights[0] / 1000:0.00}–{heights[^1] / 1000:0.00} m"));
        }
        return result;
    }

    static Vec2 Centroid(Polygon2 p) => new(p.Vertices.Average(v => v.X), p.Vertices.Average(v => v.Y));

    public static List<DetailShape> Details(SurveyLevel l, SurveyFrame frame, LayerRules rules, double cell = 25)
    {
        var result = new List<DetailShape>();
        var byRole = l.Plan.FeatureLines.Select(frame.ToModel).GroupBy(s => rules.Classify(s.Layer)).ToDictionary(g => g.Key, g => g.ToList());
        double storey = l.WallHeightMm;
        var shapes = new (LayerRole Role, double Base, double Height, double MinArea, double MaxArea)[]
        {
            (LayerRole.Column, 0, storey, 0.02e6, 4e6),
            (LayerRole.Chimney, 0, storey, 0.05e6, 10e6),
        };
        foreach (var (role, bottom, height, minArea, maxArea) in shapes)
        {
            if (!byRole.TryGetValue(role, out var lines) || lines.Count < 3) continue;
            foreach (var cluster in Clusters(lines, 2000))
            {
                var grid = ShapeGrid.Around(cluster.SelectMany(s => new[] { s.A, s.B }), cell, 100);
                if ((long)grid.W * grid.H > 4_000_000) continue; // a spread-out layer, not shapes
                foreach (var p in grid.Outlines(grid.Enclosed(grid.Draw(cluster)), minArea).Where(p => p.Area <= maxArea))
                    result.Add(new DetailShape(role, p, bottom, height));
            }
        }
        // shapes on the wall layers that no wall fits (a corner pier, a round or half-round column,
        // a stepped buttress): generic models filled to the drawn outline, full storey. Thin slivers
        // (under 100 mm across everywhere) are left, as are pieces too big to be one shape.
        // a generic model cannot be cut by a window or door, so masonry where one stands is left to the wall
        var openings = l.Windows.Select(w => (w.HostIndex, w.Center, w.WidthMm)).Concat(l.Doors.Select(d => (d.HostIndex, d.Center, d.WidthMm)))
            .Where(o => o.HostIndex >= 0 && o.HostIndex < l.Walls.Walls.Count)
            .Select(o => OpeningPoints(l.Walls.Walls[o.HostIndex], o.Center, o.WidthMm)).ToList();
        foreach (var piece in l.Leftover)
        {
            if (piece.Count == 0) continue;
            var grid = ShapeGrid.Around(piece, 25, 100);
            var mask = grid.Mark(piece);
            // masonry in a window or door opening is the wall's (a generic model cannot be cut): taken
            // out, which also parts the piers either side of a window into their own shapes
            var inOpening = openings.SelectMany(o => o).ToList();
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) { var c = grid.Centre(i); if (inOpening.Any(q => Math.Abs(q.X - c.X) <= 60 && Math.Abs(q.Y - c.Y) <= 60)) mask[i] = false; }
            if (!grid.Grow(mask, -2).Any(x => x)) continue;
            foreach (var p in grid.Outlines(mask, 0.03e6).Where(p => p.Area <= 25e6))
                result.Add(new DetailShape(LayerRole.Wall, p, 0, storey));
        }
        return result;
    }

    // points across an opening's full width and through the wall's depth (and a little beyond, for reveals)
    static List<Vec2> OpeningPoints(WallCandidate host, Vec2 center, double width)
    {
        var dir = host.Arc == null ? host.Centerline.Direction : (center - host.Arc.Center).Perp().Normalized();
        var n = dir.Perp(); double half = host.TypeThickness / 2 + 100;
        var pts = new List<Vec2>();
        for (double a = -width / 2 + 100; a <= width / 2 - 100; a += 100)
            for (double b = -half; b <= half; b += 50) pts.Add(center + dir * a + n * b);
        return pts;
    }

    // lines near each other (within reach) as groups, so each grid stays small
    static List<List<Segment2>> Clusters(List<Segment2> lines, double reach)
    {
        var groups = new List<(double x0, double y0, double x1, double y1, List<Segment2> items)>();
        foreach (var s in lines)
        {
            double x0 = Math.Min(s.A.X, s.B.X), x1 = Math.Max(s.A.X, s.B.X), y0 = Math.Min(s.A.Y, s.B.Y), y1 = Math.Max(s.A.Y, s.B.Y);
            var hits = groups.Where(g => x0 <= g.x1 + reach && x1 >= g.x0 - reach && y0 <= g.y1 + reach && y1 >= g.y0 - reach).ToList();
            var items = new List<Segment2> { s };
            foreach (var h in hits) { items.AddRange(h.items); x0 = Math.Min(x0, h.x0); y0 = Math.Min(y0, h.y0); x1 = Math.Max(x1, h.x1); y1 = Math.Max(y1, h.y1); groups.Remove(h); }
            groups.Add((x0, y0, x1, y1, items));
        }
        return groups.Select(g => g.items).ToList();
    }
}
