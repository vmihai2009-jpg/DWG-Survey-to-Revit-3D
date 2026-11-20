namespace SurveyToRevit.Core.Survey;

/// <summary>A flat concept roof: an outline at a height above survey datum (mm).</summary>
public sealed record RoofCandidate(Polygon2 Outline, double ElevationMm, string Note)
{
    /// <summary>A pitched roof's eaves height (mm above datum): every edge slopes up from it to the ridge at <see cref="ElevationMm"/>. Null: flat.</summary>
    public double? EavesMm { get; init; }
    /// <summary>The roof plan's spot heights (model mm), kept for a roof read from its planes so it can be fitted to the walls under it.</summary>
    public IReadOnlyList<(Vec2 P, double Mm)>? Heights { get; init; }
}

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
        var bare = new List<Polygon2>();
        foreach (var piece in grid.Outlines(inside, Math.Min(minArea, PitchFacetMinMm2)))
        {
            if (!roofPlan && floorOutline != null && FacadeWalls.Contains(floorOutline, Centroid(piece))) continue;
            var heights = spots.Where(sp => FacadeWalls.Contains(piece, sp.P)).Select(sp => sp.Mm).OrderBy(x => x).ToList();
            if (heights.Count == 0) { if (roofPlan) bare.Add(piece); else if (piece.Area >= minArea) withoutHeight++; continue; }
            if (piece.Area < minArea) continue;
            double h = heights[heights.Count / 2];
            result.Add(new RoofCandidate(piece, h, $"{(roofPlan ? "roof plan" : "roof layer")} {d.Name.FileName}, {heights.Count} spot heights {heights[0] / 1000:0.00}–{heights[^1] / 1000:0.00} m"));
        }
        // not on top of a roof read from its own spot heights
        result.AddRange(Pitched(bare, grid, spots, d.Name.FileName, minArea, ref withoutHeight)
            .Where(p => !result.Any(x => FacadeWalls.Contains(x.Outline, Centroid(p.Outline)) || FacadeWalls.Contains(p.Outline, Centroid(x.Outline)))).ToList());
        return result;
    }

    public const double PitchFacetMinMm2 = 0.5e6, EdgeReachMm = 400, PitchMinRiseMm = 150, BoxFill = 0.6, SplitMm = 1000;

    /// <summary>
    /// A pitched roof drawn on a roof plan: its planes are areas between the eaves, hip and ridge lines, and the surveyor
    /// writes the heights on those lines (eaves on the eaves, ridge on the ridge), so no plane has a spot height inside
    /// it. Neighbouring planes with none are joined across the lines between them into one roof; the spot heights on
    /// or in it (within <see cref="EdgeReachMm"/> of its edge) give the eaves (the lowest height two spots agree on)
    /// and the ridge (the highest two agree on). A rise of <see cref="PitchMinRiseMm"/> or more is a pitched roof,
    /// every edge sloping up from the eaves; less, flat. Squarish outlines (filling 85 % of their box along the
    /// building's axes) become that box; others keep their outline. Under two heights: left out, as before.
    /// </summary>
    static List<RoofCandidate> Pitched(List<Polygon2> bare, ShapeGrid grid, List<(Vec2 P, double Mm)> spots, string file, double minArea, ref int withoutHeight)
    {
        var res = new List<RoofCandidate>();
        if (bare.Count == 0) return res;
        // close the drawn lines between neighbouring planes (seams, battens, hips, the ridge: up to 300 mm of linework)
        int r = (int)Math.Ceiling(150 / grid.Cell);
        List<Polygon2> Join(IEnumerable<Polygon2> planes)
        {
            var mask = new bool[grid.W * grid.H];
            foreach (var b in planes) { var f = grid.Fill(b); for (int i = 0; i < mask.Length; i++) mask[i] |= f[i]; }
            return grid.Outlines(grid.Grow(grid.Grow(mask, r), -r), minArea);
        }
        List<(Vec2 P, double Mm)> On(Polygon2 region) => spots.Where(sp => FacadeWalls.Contains(region, sp.P) || EdgeDistance(region, sp.P) <= EdgeReachMm).ToList();
        var regions = new List<Polygon2>();
        foreach (var region in Join(bare))
        {
            // roofs at different heights joined across a gutter: heights in groups more than SplitMm apart are parted, each
            // plane going with the group of the spot height nearest it, and each group joined again on its own
            var hs0 = On(region).OrderBy(s => s.Mm).ToList();
            var groups = new List<List<(Vec2 P, double Mm)>>();
            foreach (var s in hs0) if (groups.Count > 0 && s.Mm - groups[^1][^1].Mm <= SplitMm) groups[^1].Add(s); else groups.Add(new() { s });
            if (groups.Count < 2) { regions.Add(region); continue; }
            var members = bare.Where(b => FacadeWalls.Contains(region, Centroid(b))).ToList();
            foreach (var g in groups.Where(g => g.Count >= 2))
            {
                var mine = members.Where(b => { var c = Centroid(b); return hs0.OrderBy(s => s.P.DistanceTo(c)).First() is var n && g.Contains(n); }).ToList();
                if (mine.Count > 0) regions.AddRange(Join(mine));
            }
        }
        // biggest first; a piece inside a bigger roof's box (a hip end parted by a double line) is that roof's
        var boxes = new List<Polygon2>();
        foreach (var region in regions.OrderByDescending(g => g.Area))
        {
            int members = bare.Count(b => FacadeWalls.Contains(region, Centroid(b)));
            double x0 = region.Vertices.Min(v => v.X), x1 = region.Vertices.Max(v => v.X), y0 = region.Vertices.Min(v => v.Y), y1 = region.Vertices.Max(v => v.Y);
            if (boxes.Any(b => FacadeWalls.Contains(b, Centroid(region)))) continue;
            // planes filling most of their box along the building's axes: the box (a roof's eaves are straight; the planes
            // between seams and hips leave ragged edges); otherwise the joined outline
            var outline = region.Area >= BoxFill * (x1 - x0) * (y1 - y0)
                ? new Polygon2(new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) }) : region;
            if (outline.Vertices.Count == 4) boxes.Add(outline);
            var hs = On(outline).Select(sp => sp.Mm).OrderBy(x => x).ToList();
            if (hs.Count < 2) { withoutHeight += Math.Max(1, members); continue; }
            string note = $"roof plan {file}, {members} planes";
            res.Add(WithHeights(outline, hs, note) with { Heights = spots });
        }
        return res;
    }

    /// <summary>
    /// A roof over an outline from the spot heights on it: eaves the lowest height two spots agree on (within 100 mm), the
    /// ridge the highest; a rise of <see cref="PitchMinRiseMm"/> or more on an outline of up to 12 corners is pitched.
    /// </summary>
    public static RoofCandidate WithHeights(Polygon2 outline, List<double> hs, string note)
    {
        double Agreed(IEnumerable<double> order) => order.FirstOrDefault(v => hs.Count(x => Math.Abs(x - v) <= 100) >= 2, order.First());
        double eaves = Agreed(hs), ridge = Agreed(Enumerable.Reverse(hs));
        double x0 = outline.Vertices.Min(v => v.X), x1 = outline.Vertices.Max(v => v.X), y0 = outline.Vertices.Min(v => v.Y), y1 = outline.Vertices.Max(v => v.Y);
        note += $", {hs.Count} spot heights {hs[0] / 1000:0.00}–{hs[^1] / 1000:0.00} m on its lines, x {x0 / 1000:0.0}..{x1 / 1000:0.0} y {y0 / 1000:0.0}..{y1 / 1000:0.0} m";
        if (ridge - eaves >= PitchMinRiseMm && outline.Vertices.Count <= 12)
            return new RoofCandidate(outline, ridge, note + $", pitched: eaves {eaves / 1000:0.00}, ridge {ridge / 1000:0.00} m") { EavesMm = eaves };
        return new RoofCandidate(outline, hs[hs.Count / 2], note);
    }

    public const double WallSnapMm = 2000;

    /// <summary>
    /// A roof box from a roof plan stands on the walls of the floor under it: each side within <see cref="WallSnapMm"/> of
    /// a parallel wall of that floor running along half the side or more moves onto the wall's outer face (the eaves
    /// strips and hip ends a roof plan draws as separate pieces are taken in), and the eaves and ridge are read again
    /// from the spot heights on the new box. Other roofs are returned as they are.
    /// </summary>
    public static RoofCandidate OnWalls(RoofCandidate r, IReadOnlyList<WallCandidate> walls)
    {
        if (r.Heights == null || r.Outline.Vertices.Count != 4) return r;
        double x0 = r.Outline.Vertices.Min(v => v.X), x1 = r.Outline.Vertices.Max(v => v.X), y0 = r.Outline.Vertices.Min(v => v.Y), y1 = r.Outline.Vertices.Max(v => v.Y);
        var straight = walls.Where(w => w.Arc == null && w.HeightMm == null && !w.Curtain && w.Centerline.Length >= 1000).ToList();
        double Side(double at, bool alongX, double lo, double hi, int outward)
        {
            double best = at, bestD = WallSnapMm;
            foreach (var w in straight)
            {
                var c = w.Centerline; var d = c.Direction;
                if ((alongX ? Math.Abs(d.Y) : Math.Abs(d.X)) > 0.05) continue;
                double pos = alongX ? c.Mid.Y : c.Mid.X;
                double a = alongX ? Math.Min(c.A.X, c.B.X) : Math.Min(c.A.Y, c.B.Y), b = alongX ? Math.Max(c.A.X, c.B.X) : Math.Max(c.A.Y, c.B.Y);
                if (Math.Min(b, hi) - Math.Max(a, lo) < 0.5 * (hi - lo)) continue;
                double face = pos + outward * w.TypeThickness / 2;
                if (Math.Abs(face - at) < bestD) { bestD = Math.Abs(face - at); best = face; }
            }
            return best;
        }
        double nx0 = Side(x0, false, y0, y1, -1), nx1 = Side(x1, false, y0, y1, 1), ny0 = Side(y0, true, x0, x1, -1), ny1 = Side(y1, true, x0, x1, 1);
        if (nx1 - nx0 < 1000 || ny1 - ny0 < 1000) return r;
        var box = new Polygon2(new[] { new Vec2(nx0, ny0), new Vec2(nx1, ny0), new Vec2(nx1, ny1), new Vec2(nx0, ny1) });
        var hs = r.Heights.Where(sp => FacadeWalls.Contains(box, sp.P) || EdgeDistance(box, sp.P) <= EdgeReachMm).Select(sp => sp.Mm).OrderBy(x => x).ToList();
        if (hs.Count < 2) return r;
        return WithHeights(box, hs, r.Note.Split(',')[0] + ", on the walls below") with { Heights = r.Heights };
    }

    static double EdgeDistance(Polygon2 p, Vec2 q)
    {
        double best = double.MaxValue; var v = p.Vertices;
        for (int i = 0; i < v.Count; i++)
        {
            var a = v[i]; var d = v[(i + 1) % v.Count] - a; double l2 = d.Dot(d);
            double t = l2 == 0 ? 0 : Math.Clamp((q - a).Dot(d) / l2, 0, 1);
            best = Math.Min(best, q.DistanceTo(a + d * t));
        }
        return best;
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
