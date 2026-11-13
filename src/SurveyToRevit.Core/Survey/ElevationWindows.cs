namespace SurveyToRevit.Core.Survey;

/// <summary>A window drawn on an elevation, in model terms: along its facade (mm from the facade line's start) and heights above datum.</summary>
public sealed record DrawnWindow(string Facade, double T0, double T1, double SillZ, double HeadZ)
{
    public double Centre => (T0 + T1) / 2;
    public double Width => T1 - T0;
}

/// <summary>What item F did, for the log and the index.</summary>
public sealed record ElevationWindowResult(int Drawn, int HeightsSet, int WidthsSet, int Added, int NoWall, List<string> Notes);

/// <summary>
/// Item F of MCP-AUDIT-AND-TOOLS.md: windows from the elevations. Where an elevation (item E) covers a facade:
/// - the windows the plan found there take their sill and head from the drawn outline (the plan's S/H notes are internal
///   heights), and the drawn width where the plan's differs by more than 50 mm;
/// - windows drawn that the plan doesn't have are added where a wall of that floor stands along the facade over the
///   window's whole height, tagged as from the elevation alone (Low).
/// Panes, bars and arched heads are not read yet (they go on the window type, item G).
/// Deterministic; nothing changes for a survey without elevations, or for facades no elevation matched.
/// </summary>
public static class ElevationWindows
{
    public const double WidthAgreeMm = 50, MinWidthMm = 300, MinHeightMm = 400, MaxHeightMm = 6000, GroupGapMm = 100;

    /// <summary>Window outlines on a matched drawing: its window-layer lines grouped (gaps under ~100 mm), each group's box mapped to the facade and to heights.</summary>
    public static List<DrawnWindow> Read(ElevationDrawing d, LayerRules rules)
    {
        var res = new List<DrawnWindow>();
        if (d.Facade == null || d.Parts.Count == 0) return res;
        int sg = d.Mirrored ? -1 : 1;
        var lines = d.Lines.Where(s => rules.Classify(s.Layer) == LayerRole.Window).ToList();
        foreach (var g in SectionReader.Islands(lines, GroupGapMm))
        {
            double x0 = g.Min(s => Math.Min(s.A.X, s.B.X)), x1 = g.Max(s => Math.Max(s.A.X, s.B.X));
            double y0 = g.Min(s => Math.Min(s.A.Y, s.B.Y)), y1 = g.Max(s => Math.Max(s.A.Y, s.B.Y));
            var part = d.Parts.FirstOrDefault(p => (x0 + x1) / 2 >= p.X0 && (x0 + x1) / 2 <= p.X1) ?? d.Parts[0];
            double z0 = part.A * y0 + part.B, z1 = part.A * y1 + part.B;
            double ta = d.ShiftMm + sg * d.Scale * x0, tb = d.ShiftMm + sg * d.Scale * x1;
            var w = new DrawnWindow(d.Facade, Math.Min(ta, tb), Math.Max(ta, tb), z0, z1);
            if (w.Width < MinWidthMm || z1 - z0 < MinHeightMm || z1 - z0 > MaxHeightMm) continue;
            res.Add(w);
        }
        return res;
    }

    /// <summary>The floor a drawn window belongs to: the highest whose floor is at or under its sill (300 mm of slack for a sill drawn at the floor).</summary>
    public static SurveyLevel? FloorOf(DrawnWindow w, IReadOnlyList<SurveyLevel> levels) =>
        levels.Where(l => !double.IsNaN(l.ElevationMm) && l.ElevationMm <= w.SillZ + 300).OrderByDescending(l => l.ElevationMm).FirstOrDefault();

    public static ElevationWindowResult Apply(IReadOnlyList<ElevationDrawing> elevations, IReadOnlyList<SurveyLevel> levels, LayerRules rules)
    {
        var notes = new List<string>(); int drawn = 0, heights = 0, widths = 0, added = 0, noWall = 0;
        foreach (var d in elevations.Where(e => e.Facade != null && e.FacadeLine != null))
        {
            var fl = d.FacadeLine!;
            var facade = new Segment2(new Vec2(fl[0], fl[1]), new Vec2(fl[2], fl[3]));
            var dir = facade.Direction;
            Vec2 At(double t) => facade.A + dir * t;
            double T(Vec2 p) => (p.X - facade.A.X) * dir.X + (p.Y - facade.A.Y) * dir.Y;
            var found = Read(d, rules); drawn += found.Count;
            foreach (var g in found.GroupBy(w => FloorOf(w, levels)))
            {
                var level = g.Key; if (level == null) continue;
                var list = level.Windows.ToList(); bool changed = false;
                foreach (var w in g)
                {
                    // the plan's window on this facade at this place
                    int k = list.FindIndex(p => Math.Abs(SegmentCleaner.Offset(facade, p.Center)) <= ElevationIndex.FacadeReachMm
                                                && Math.Abs(T(p.Center) - w.Centre) <= Math.Max(300, w.Width / 2));
                    double sill = Math.Round(w.SillZ - level.ElevationMm), head = Math.Round(w.HeadZ - level.ElevationMm);
                    if (k >= 0)
                    {
                        var p = list[k];
                        // the plan's own surveyed sill and head (notes) stand; a drawn opening that would run through the floor
                        // above (a row's sill on the wrong storey) or is far narrower/wider than the plan's is not this window
                        if (p.HeightsFromSurvey || head > level.WallHeightMm - 50 || sill < -50
                            || Math.Abs(p.WidthMm - w.Width) > 0.15 * p.WidthMm) continue;
                        double width = Math.Abs(p.WidthMm - w.Width) > WidthAgreeMm ? Math.Round(w.Width) : p.WidthMm;
                        if (width != p.WidthMm) widths++;
                        list[k] = p with { SillMm = sill, HeadMm = head, WidthMm = width, HeightsFrom = "elevation " + d.Title };
                        heights++; changed = true;
                        continue;
                    }
                    // drawn only: a wall of this floor along the facade, under the window's whole width and as tall as its head
                    var walls = level.Walls.Walls;
                    int host = Enumerable.Range(0, walls.Count).Where(i => walls[i].Arc == null && walls[i].HeightMm == null
                            && Math.Abs(SegmentCleaner.Offset(facade, walls[i].Centerline.A)) <= ElevationIndex.FacadeReachMm
                            && Math.Abs(SegmentCleaner.Offset(facade, walls[i].Centerline.B)) <= ElevationIndex.FacadeReachMm
                            && Math.Min(T(walls[i].Centerline.A), T(walls[i].Centerline.B)) <= w.T0 && Math.Max(T(walls[i].Centerline.A), T(walls[i].Centerline.B)) >= w.T1
                            && head <= level.WallHeightMm + 50)
                        .OrderBy(i => Math.Abs(SegmentCleaner.Offset(facade, walls[i].Centerline.Mid))).DefaultIfEmpty(-1).First();
                    if (host < 0) { noWall++; continue; }
                    var c = walls[host].Centerline; var cd = c.Direction;
                    var onFacade = At(w.Centre);
                    var centre = c.A + cd * ((onFacade.X - c.A.X) * cd.X + (onFacade.Y - c.A.Y) * cd.Y);
                    var n = new Vec2(-dir.Y, dir.X);
                    var inside = level.Outline != null ? level.Outline.Vertices.Aggregate(new Vec2(0, 0), (a, v) => a + v) * (1.0 / level.Outline.Vertices.Count) : centre;
                    var outward = (inside - centre).X * n.X + (inside - centre).Y * n.Y > 0 ? new Vec2(-n.X, -n.Y) : n;
                    list.Add(new WindowCandidate(host, centre, Math.Round(w.Width), sill, head, false) { Outward = outward, FromElevation = true, HeightsFrom = "elevation " + d.Title });
                    added++; changed = true;
                }
                if (changed) level.Windows = list;
            }
        }
        if (drawn > 0)
            notes.Add($"Windows from the elevations: {drawn} drawn on matched facades; {heights} plan windows take the drawn sill and head ({widths} the drawn width too); {added} drawn only, added (tagged ElevationOnly)"
                      + (noWall > 0 ? $"; {noWall} drawn only with no wall of their floor along the facade to take them" : "") + ".");
        return new ElevationWindowResult(drawn, heights, widths, added, noWall, notes);
    }
}
