namespace SurveyToRevit.Core.Survey;

/// <summary>What a check found to look at.</summary>
public enum CheckKind
{
    /// <summary>A door symbol on the door layer with no door made from it.</summary>
    UnusedDoorSymbol,
    /// <summary>A room no door or opening leads into.</summary>
    UnreachableRoom,
    /// <summary>A window missing from a column of windows that runs up the façade.</summary>
    WindowRhythmGap,
    /// <summary>A window set in a different plane from the windows above and below it.</summary>
    WindowOffPlane,
    /// <summary>A wall that stops just short of another wall.</summary>
    LooseWallEnd,
    /// <summary>Two walls running through each other.</summary>
    CrossingWalls,
    /// <summary>An outside wall on this floor only, no wall above or below it.</summary>
    FacadeNotStacked,
    /// <summary>An outside wall well outside the building's outline seen by the LiDAR.</summary>
    OutsideLidar,
    /// <summary>A mezzanine edge read from the survey's void notes and dashed lines.</summary>
    MezzanineEdge,
}

/// <summary>One item for the review list: where (model mm, on a level) and what to look at.</summary>
public sealed record CheckItem(string LevelCode, CheckKind Kind, Vec2 At, string Message)
{
    /// <summary>How much it matters, 1 (look first) to 3; the review list is sorted by this.</summary>
    public int Priority => Kind switch
    {
        CheckKind.UnreachableRoom or CheckKind.UnusedDoorSymbol or CheckKind.OutsideLidar => 1,
        CheckKind.WindowRhythmGap or CheckKind.WindowOffPlane or CheckKind.CrossingWalls => 2,
        _ => 3,
    };

    public static string Describe(CheckKind k) => k switch
    {
        CheckKind.UnusedDoorSymbol => "Door symbol with no door",
        CheckKind.UnreachableRoom => "Room with no way in",
        CheckKind.WindowRhythmGap => "Gap in a column of windows",
        CheckKind.WindowOffPlane => "Window out of line with those above and below",
        CheckKind.LooseWallEnd => "Wall stops short of the next one",
        CheckKind.CrossingWalls => "Walls run through each other",
        CheckKind.FacadeNotStacked => "Outside wall on this floor only",
        CheckKind.OutsideLidar => "Outside wall beyond the LiDAR outline",
        CheckKind.MezzanineEdge => "Mezzanine edge from the survey",
        _ => k.ToString(),
    };
}

/// <summary>
/// Self-checks run on the finished reading, without Revit: the model is checked against itself and
/// against the drawing the way a reviewer would, and every doubtful place is listed with its position
/// so it can be looked at in seconds. Nothing is changed here.
/// </summary>
public static class ModelChecks
{
    /// <summary>Development aid: each floor's open-area grid (level code, width, height, area number per cell, -1 closed).</summary>
    public static Action<string, int, int, int[]>? GridDebug { get; set; }

    public static List<CheckItem> Run(IReadOnlyList<SurveyLevel> levels, SurveyFrame frame, LayerRules rules, IReadOnlyDictionary<WallCandidate, double> stacked)
    {
        var items = new List<CheckItem>();
        foreach (var l in levels)
        {
            items.AddRange(UnusedDoorSymbols(l, frame, rules));
            items.AddRange(UnreachableRooms(l));
            items.AddRange(WallEnds(l));
        }
        items.AddRange(WindowColumns(levels));
        items.AddRange(FacadeOnOneFloor(levels, stacked));
        return items;
    }

    // ---------- door symbols with no door ----------

    /// <summary>Clusters of door-layer linework (a leaf, a swing) with no door or window made within reach of them.</summary>
    public static IEnumerable<CheckItem> UnusedDoorSymbols(SurveyLevel l, SurveyFrame frame, LayerRules rules)
    {
        var lines = l.Plan.OpeningLines.Where(o => rules.Classify(o.Layer) == LayerRole.Door).Select(frame.ToModel)
            .Concat(l.Plan.DoorArcs.Select(frame.ToModel).SelectMany(a => a.Tessellate(150))).Where(s => s.Length >= 20).ToList();
        if (lines.Count == 0) yield break;
        var near = Near(l.Walls.Walls, 3000);
        lines = lines.Where(s => near(s.A) && near(s.B)).ToList();
        foreach (var cluster in Clusters(lines, 40))
        {
            var pts = cluster.SelectMany(s => new[] { s.A, s.B }).ToList();
            double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
            double size = Math.Max(x1 - x0, y1 - y0);
            // under 600 mm: a cupboard door
            if (size < 600 || size > 4000 || cluster.Sum(s => s.Length) < 700) continue;
            var c = new Vec2((x0 + x1) / 2, (y0 + y1) / 2);
            double reach = Math.Max(800, size);
            if (l.Doors.Any(d => d.Center.DistanceTo(c) <= reach) || l.Windows.Any(w => w.Center.DistanceTo(c) <= reach / 2)) continue;
            // the survey says it is blocked up
            if (l.Plan.Texts.Any(t => DoorFinder.IsBlockedNote(t.Value) && frame.ToModel(t.Position).DistanceTo(c) <= 1500)) continue;
            yield return new CheckItem(l.Code, CheckKind.UnusedDoorSymbol, c, $"{l.Name}: door linework about {size / 1000:0.0} m across with no door made from it.");
        }
    }

    // ---------- rooms with no way in ----------

    /// <summary>
    /// The floor as a 100 mm grid: open floor inside the outline, walls (as built, low walls too) closed,
    /// doors open. Open areas not joined to the floor's main area (where the stairs arrive) are rooms with
    /// no way in: a missing door, or a wall where there is an opening.
    /// </summary>
    public static IEnumerable<CheckItem> UnreachableRooms(SurveyLevel l, double cell = 100, double minAreaM2 = 4)
    {
        if (l.Outline is not Polygon2 outline || outline.Area < 20e6) yield break;
        var v = outline.Vertices;
        double x0 = v.Min(p => p.X), y0 = v.Min(p => p.Y), x1 = v.Max(p => p.X), y1 = v.Max(p => p.Y);
        int nx = (int)Math.Ceiling((x1 - x0) / cell) + 1, ny = (int)Math.Ceiling((y1 - y0) / cell) + 1;
        if ((long)nx * ny > 4_000_000) yield break;
        var open = new bool[nx * ny];
        Vec2 At(int i, int j) => new(x0 + (i + 0.5) * cell, y0 + (j + 0.5) * cell);
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                open[j * nx + i] = FacadeWalls.Contains(outline, At(i, j));
        var inside = (bool[])open.Clone();
        void Paint(Vec2 a, Vec2 b, double half, bool value)
        {
            var d = b - a; double len = d.Length; if (len < 1) return;
            var u = d * (1 / len); var n = u.Perp();
            double ext = half + cell;
            var corners = new[] { a - n * ext - u * cell, a + n * ext - u * cell, b - n * ext + u * cell, b + n * ext + u * cell };
            int i0 = Math.Max(0, (int)((corners.Min(p => p.X) - x0) / cell)), i1 = Math.Min(nx - 1, (int)((corners.Max(p => p.X) - x0) / cell));
            int j0 = Math.Max(0, (int)((corners.Min(p => p.Y) - y0) / cell)), j1 = Math.Min(ny - 1, (int)((corners.Max(p => p.Y) - y0) / cell));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var p = At(i, j);
                    double t = (p - a).Dot(u), o = Math.Abs((p - a).Dot(n));
                    if (t >= -cell / 2 && t <= len + cell / 2 && o <= Math.Max(half, cell / 2)) open[j * nx + i] = value;
                }
        }
        foreach (var w in l.Walls.Walls)
        {
            if (w.Arc is Arc2 arc) foreach (var s in arc.Tessellate(200)) Paint(s.A, s.B, w.TypeThickness / 2, false);
            else Paint(w.Centerline.A, w.Centerline.B, w.TypeThickness / 2, false);
        }
        foreach (var d in l.Doors)
        {
            var hw = l.Walls.Walls[d.HostIndex];
            double along = WallGeometry.Along(hw, d.Center);
            Paint(WallGeometry.PointAt(hw, along - d.WidthMm / 2 + 50), WallGeometry.PointAt(hw, along + d.WidthMm / 2 - 50), hw.TypeThickness / 2 + cell, true);
        }
        // detail blocks (columns, piers) close the floor too
        foreach (var dt in l.Details)
            for (int k = 0; k < dt.Outline.Vertices.Count; k++)
                Paint(dt.Outline.Vertices[k], dt.Outline.Vertices[(k + 1) % dt.Outline.Vertices.Count], cell / 2, false);

        var comp = new int[nx * ny]; Array.Fill(comp, -1);
        var sizes = new List<int>();
        var touchesOut = new List<bool>();
        var stack = new Stack<int>();
        for (int s = 0; s < open.Length; s++)
        {
            if (!open[s] || comp[s] >= 0) continue;
            int id = sizes.Count, n = 0; bool edge = false;
            comp[s] = id; stack.Push(s);
            while (stack.Count > 0)
            {
                int c = stack.Pop(); n++;
                int ci = c % nx, cj = c / nx;
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int ni = ci + di, nj = cj + dj;
                    if (ni < 0 || nj < 0 || ni >= nx || nj >= ny) { edge = true; continue; }
                    int q = nj * nx + ni;
                    if (!inside[q]) edge = true;
                    if (open[q] && comp[q] < 0) { comp[q] = id; stack.Push(q); }
                }
            }
            sizes.Add(n); touchesOut.Add(edge);
        }
        GridDebug?.Invoke(l.Code, nx, ny, comp);
        if (sizes.Count < 2) yield break;
        int Comp(Vec2 p)
        {
            int i = (int)((p.X - x0) / cell), j = (int)((p.Y - y0) / cell);
            return i < 0 || j < 0 || i >= nx || j >= ny ? -1 : comp[j * nx + i];
        }
        // the floor's main area: where its stairs are, else the largest open area
        var reached = new HashSet<int>();
        foreach (var st in l.Stairs)
            foreach (var p in st.Flights.SelectMany(f => f.Risers).Select(r => r.Mid))
                if (Comp(p) is int c && c >= 0) reached.Add(c);
        reached.Add(Enumerable.Range(0, sizes.Count).OrderByDescending(k => sizes[k]).First());
        double cellM2 = cell * cell / 1e6;
        for (int id = 0; id < sizes.Count; id++)
        {
            if (reached.Contains(id) || sizes[id] * cellM2 < minAreaM2) continue;
            // the room's middle: the open cell nearest the mean of its cells
            double sx = 0, sy = 0; var cells = new List<int>();
            for (int s = 0; s < comp.Length; s++) if (comp[s] == id) { cells.Add(s); sx += s % nx; sy += s / nx; }
            var mean = At((int)(sx / cells.Count), (int)(sy / cells.Count));
            var mid = cells.Select(s => At(s % nx, s / nx)).OrderBy(p => p.DistanceTo(mean)).First();
            var name = l.RoomNames.Where(r => Comp(r.Position) == id).Select(r => r.Name).FirstOrDefault();
            // a shaft or riser with no way in is normal: only rooms the survey names, or of a room's size
            if (name == null && sizes[id] * cellM2 < 8) continue;
            // open to the outside edge with no name: a terrace, balcony or lightwell, not a room
            if (name == null && touchesOut[id]) continue;
            if (name != null && System.Text.RegularExpressions.Regex.IsMatch(name, @"\b(lift|shaft|riser|duct|void|plant|tank)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            yield return new CheckItem(l.Code, CheckKind.UnreachableRoom, mid,
                $"{l.Name}: {(name != null ? $"\"{name}\"" : "a room")} of about {sizes[id] * cellM2:0} m² has no door or opening into it.");
        }
    }

    // ---------- walls stopping short, walls crossing ----------

    public static IEnumerable<CheckItem> WallEnds(SurveyLevel l)
    {
        var walls = l.Walls.Walls.Where(w => w.Arc == null && !w.Curtain && w.HeightMm == null).ToList();
        foreach (var w in walls)
        {
            if (w.Length < 1000) continue;
            var c = w.Centerline;
            foreach (var (end, dir) in new[] { (c.A, c.Direction * -1), (c.B, c.Direction) })
            {
                // touching another wall: done
                if (walls.Any(o => o != w && Inside(o, end, 60))) continue;
                // the nearest wall straight on, within 600 mm: a join the reading missed
                double? gap = null;
                for (double d = 60; d <= 600 && gap == null; d += 30)
                {
                    var q = end + dir * d;
                    if (walls.Any(o => o != w && Inside(o, q, 0))) gap = d;
                }
                if (gap is double g && !l.Doors.Any(dr => dr.Center.DistanceTo(end + dir * (g / 2)) < dr.WidthMm / 2 + 200)
                                    && !l.Windows.Any(wi => wi.Center.DistanceTo(end + dir * (g / 2)) < wi.WidthMm / 2 + 200))
                    yield return new CheckItem(l.Code, CheckKind.LooseWallEnd, end, $"{l.Name}: a {w.TypeThickness:0} mm wall stops {g:0} mm short of the next wall.");
            }
        }
        for (int a = 0; a < walls.Count; a++)
            for (int b = a + 1; b < walls.Count; b++)
            {
                Segment2 s = walls[a].Centerline, t = walls[b].Centerline;
                if (SegmentCleaner.AngleDiffDeg(s.AngleDeg, t.AngleDeg) < 20) continue;
                if (Intersect(s, t) is not Vec2 x) continue;
                // through the middle of both (not a junction at an end)
                double sa = SegmentCleaner.Along(s, x), ta = SegmentCleaner.Along(t, x);
                double ms = walls[b].TypeThickness / 2 + 150, mt = walls[a].TypeThickness / 2 + 150;
                if (sa > ms && sa < s.Length - ms && ta > mt && ta < t.Length - mt)
                    yield return new CheckItem(l.Code, CheckKind.CrossingWalls, x, $"{l.Name}: a {walls[a].TypeThickness:0} mm and a {walls[b].TypeThickness:0} mm wall run through each other.");
            }
    }

    static bool Inside(WallCandidate w, Vec2 p, double slack)
    {
        double along = WallGeometry.Along(w, p);
        return along >= -slack && along <= WallGeometry.Length(w) + slack && Math.Abs(WallGeometry.Offset(w, p)) <= w.TypeThickness / 2 + slack;
    }

    static Vec2? Intersect(Segment2 s, Segment2 t)
    {
        var r = s.B - s.A; var q = t.B - t.A;
        double den = r.Cross(q);
        if (Math.Abs(den) < 1e-9) return null;
        double u = (t.A - s.A).Cross(q) / den, v = (t.A - s.A).Cross(r) / den;
        return u is >= 0 and <= 1 && v is >= 0 and <= 1 ? s.A + r * u : null;
    }

    // ---------- windows in columns up the façade ----------

    /// <summary>
    /// Façades are regular: windows stand in columns. A column seen on three or more floors with a floor
    /// in between that has a wall there but no window is flagged; so is a window whose wall face is out of
    /// line with the windows directly above and below it.
    /// </summary>
    public static IEnumerable<CheckItem> WindowColumns(IReadOnlyList<SurveyLevel> levels, double slack = 300)
    {
        var floors = levels.Where(l => l.Include && !l.Partial && l.Windows.Count > 0).ToList();
        if (floors.Count < 3) yield break;
        // each window as (floor index, outward, centre)
        var wins = new List<(int F, Vec2 Out, Vec2 C, double W)>();
        for (int f = 0; f < floors.Count; f++)
            foreach (var w in floors[f].Windows)
            {
                // the window's facing: outward where known, else square to its wall (either way round)
                var hw = floors[f].Walls.Walls[w.HostIndex];
                double a = WallGeometry.AngleDegAt(hw, Math.Clamp(WallGeometry.Along(hw, w.Center), 0, WallGeometry.Length(hw))) * Math.PI / 180;
                wins.Add((f, w.Outward ?? new Vec2(-Math.Sin(a), Math.Cos(a)), w.Center, w.WidthMm));
            }
        bool Same((int F, Vec2 Out, Vec2 C, double W) a, (int F, Vec2 Out, Vec2 C, double W) b) =>
            Math.Abs(a.Out.Dot(b.Out)) > 0.98 && Math.Abs((b.C - a.C).Dot(a.Out.Perp())) <= slack && Math.Abs((b.C - a.C).Dot(a.Out)) <= 1500;
        var gaps = new List<(int F, Vec2 C)>();
        foreach (var w in wins)
        {
            // the column through this window: the floors with a window in line with it
            var floorsWith = wins.Where(o => Same(w, o)).Select(o => o.F).Distinct().OrderBy(f => f).ToList();
            if (floorsWith.Count < 3) continue;
            for (int f = floorsWith.First() + 1; f < floorsWith.Last(); f++)
            {
                if (floorsWith.Contains(f)) continue;
                if (gaps.Any(g => g.F == f && g.C.DistanceTo(w.C) < 800)) continue;
                gaps.Add((f, w.C));
                // a wall facing the same way runs past the column on that floor (else the façade steps there)
                var l = floors[f];
                var wall = l.Walls.Walls.FirstOrDefault(x => x.Arc == null && !x.Curtain && SegmentCleaner.AngleDiffDeg(x.Centerline.AngleDeg, new Segment2(w.C, w.C + w.Out.Perp()).AngleDeg) < 5
                    && Math.Abs(SegmentCleaner.Offset(x.Centerline, w.C)) <= x.TypeThickness / 2 + 500
                    && SegmentCleaner.Along(x.Centerline, w.C) > w.W / 2 && SegmentCleaner.Along(x.Centerline, w.C) < x.Centerline.Length - w.W / 2);
                if (wall == null) continue;
                var at = wall.Centerline.A + wall.Centerline.Direction * SegmentCleaner.Along(wall.Centerline, w.C);
                yield return new CheckItem(l.Code, CheckKind.WindowRhythmGap, at,
                    $"{l.Name}: the windows above and below line up here ({floorsWith.Count} floors), but this floor has none.");
            }
        }
        foreach (var w in wins)
        {
            if (w.F == 0 || w.F == floors.Count - 1) continue;
            var below = wins.Where(o => o.F == w.F - 1 && Math.Abs(o.Out.Dot(w.Out)) > 0.98 && Math.Abs((o.C - w.C).Dot(w.Out.Perp())) <= slack).OrderBy(o => Math.Abs((o.C - w.C).Dot(w.Out))).FirstOrDefault();
            var above = wins.Where(o => o.F == w.F + 1 && Math.Abs(o.Out.Dot(w.Out)) > 0.98 && Math.Abs((o.C - w.C).Dot(w.Out.Perp())) <= slack).OrderBy(o => Math.Abs((o.C - w.C).Dot(w.Out))).FirstOrDefault();
            if (below.W == 0 || above.W == 0) continue;
            double db = (w.C - below.C).Dot(w.Out), da = (w.C - above.C).Dot(w.Out), ab = (above.C - below.C).Dot(w.Out);
            // above and below agree, this one sits more than 300 mm in or out of their plane
            if (Math.Abs(ab) <= 150 && Math.Abs(db) > 300 && Math.Abs(da) > 300 && Math.Abs(db) < 1500)
                yield return new CheckItem(floors[w.F].Code, CheckKind.WindowOffPlane, w.C,
                    $"{floors[w.F].Name}: this window sits {Math.Abs(db):0} mm {(db < 0 ? "inside" : "outside")} the windows above and below it.");
        }
    }

    // ---------- façade on one floor only ----------

    static IEnumerable<CheckItem> FacadeOnOneFloor(IReadOnlyList<SurveyLevel> levels, IReadOnlyDictionary<WallCandidate, double> stacked)
    {
        // middle floors only: the top and bottom floors and part floors have nothing to stand on or under
        for (int i = 1; i + 1 < levels.Count; i++)
        {
            var l = levels[i];
            if (l.Partial || l.Outline is not Polygon2 outline) continue;
            var hosts = l.Windows.Select(w => w.HostIndex).Concat(l.Doors.Select(d => d.HostIndex)).ToHashSet();
            for (int k = 0; k < l.Walls.Walls.Count; k++)
            {
                var w = l.Walls.Walls[k];
                if (w.Arc != null || w.Curtain || w.HeightMm != null || w.Length < 2000 || hosts.Contains(k)) continue;
                if (!stacked.TryGetValue(w, out var share) || share > 0.1) continue;
                if (WindowFinder.Outward(w, w.Length / 2, outline) is null) continue;
                yield return new CheckItem(l.Code, CheckKind.FacadeNotStacked, w.Centerline.Mid,
                    $"{l.Name}: a {w.Length / 1000:0.0} m outside wall with no wall above or below it and no opening: a balcony edge or terrace upstand built full height?");
            }
        }
    }

    // ---------- helpers ----------

    static List<List<Segment2>> Clusters(List<Segment2> lines, double join)
    {
        var parent = Enumerable.Range(0, lines.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
                if (Dist(lines[i].A, lines[j]) <= join || Dist(lines[i].B, lines[j]) <= join || Dist(lines[j].A, lines[i]) <= join || Dist(lines[j].B, lines[i]) <= join)
                    parent[Root(i)] = Root(j);
        return Enumerable.Range(0, lines.Count).GroupBy(Root).Select(g => g.Select(i => lines[i]).ToList()).ToList();
    }

    static double Dist(Vec2 p, Segment2 s)
    {
        var d = s.B - s.A; double l2 = d.Dot(d);
        double t = l2 < 1e-9 ? 0 : Math.Clamp((p - s.A).Dot(d) / l2, 0, 1);
        return p.DistanceTo(s.A + d * t);
    }

    static Func<Vec2, bool> Near(IReadOnlyList<WallCandidate> walls, double margin)
    {
        var pts = walls.SelectMany(w => new[] { w.Centerline.A, w.Centerline.B }).ToList();
        if (pts.Count < 10) return _ => true;
        var xs = pts.Select(p => p.X).OrderBy(x => x).ToList(); var ys = pts.Select(p => p.Y).OrderBy(y => y).ToList();
        int lo = (int)(0.01 * (pts.Count - 1)), hi = (int)(0.99 * (pts.Count - 1));
        double x0 = xs[lo] - margin, x1 = xs[hi] + margin, y0 = ys[lo] - margin, y1 = ys[hi] + margin;
        return p => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
    }
}
