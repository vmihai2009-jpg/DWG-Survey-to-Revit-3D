using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Interior;

/// <summary>
/// Leaves the exterior out of a survey reading, for a project that already holds the surveyed exterior. Floor by
/// floor, the existing model is cut at a few heights (low, at window height, under the ceiling) and drawn on a 50 mm
/// grid. Then:
/// <list type="bullet">
/// <item>A survey wall mostly inside what already exists (a façade wall, a shopfront, a mansard roof), or outside the
/// existing envelope (a terrace parapet, a balcony edge), is left out, with its windows and doors.</item>
/// <item>A kept wall that runs into the existing façade is cut back to its inner face; one stopping just short of it
/// (up to 250 mm) is carried on to meet it.</item>
/// <item>Floor slabs and ceilings stop at the façade's inner face.</item>
/// <item>Piers, columns and fills inside the existing model or outside it are left out; roofs are not built.</item>
/// </list>
/// Works in the survey's model coordinates, which <see cref="InteriorFit"/> has already made the project's own.
/// </summary>
public static class InteriorSplit
{
    public const double CellMm = 50;
    /// <summary>A wall this much (by its footprint) in the existing model or outside it is left out.</summary>
    public const double FacadeShare = 0.5;
    /// <summary>Gap between a kept wall's end and the façade closed by carrying the wall on (mm).</summary>
    public const double ReachMm = 250;
    /// <summary>Furthest a wall end inside the façade is cut back (mm): beyond that it is a wall the façade really crosses.</summary>
    public const double MaxCutBackMm = 800;

    public sealed record Result(int WallsLeftOut, int WindowsLeftOut, int DoorsLeftOut, int WallsMet, int DetailsLeftOut, bool EnvelopeClosed, string Note);

    /// <summary>Applies the split to every floor; returns a line per floor for the log.</summary>
    public static List<string> Apply(SurveyModel model, ExistingBuilding existing, double datumMm)
    {
        var log = new List<string>();
        var floors = model.Levels.Where(l => l.Include && !double.IsNaN(l.ElevationMm)).OrderBy(l => l.ElevationMm).ToList();
        for (int i = 0; i < floors.Count; i++)
        {
            var l = floors[i];
            double storey = i + 1 < floors.Count ? floors[i + 1].ElevationMm - l.ElevationMm : Math.Max(l.WallHeightMm, 2400);
            var heights = InteriorFit.CutHeightsMm.Append(Math.Max(1500, Math.Min(l.CeilingHeightMm > 0 ? l.CeilingHeightMm - 100 : 2400, storey - 300)))
                .Select(h => l.ElevationMm - datumMm + h).ToList();
            var r = Level(l, existing, heights);
            log.Add($"{l.Name}: {r.WallsLeftOut} walls, {r.WindowsLeftOut} windows and {r.DoorsLeftOut} doors left to the existing exterior; " +
                    $"{r.WallsMet} walls cut back or carried on to meet it; {r.DetailsLeftOut} piers/fills left out. {r.Note}");
        }
        return log;
    }

    /// <summary>One floor: the existing model cut at the given heights (internal mm), then the survey floor split.</summary>
    public static Result Level(SurveyLevel l, ExistingBuilding existing, IReadOnlyList<double> cutHeights)
    {
        var walls = l.Walls.Walls;
        var pts = walls.SelectMany(w => new[] { w.Centerline.A, w.Centerline.B }).ToList();
        if (l.Outline != null) pts.AddRange(l.Outline.Vertices);
        if (pts.Count == 0) return new Result(0, 0, 0, 0, 0, false, "nothing on this floor.");
        var grid = new Grid(pts.Min(p => p.X) - 3000, pts.Max(p => p.X) + 3000, pts.Min(p => p.Y) - 3000, pts.Max(p => p.Y) + 3000);

        // every existing element reaching one of the cut heights, drawn on the grid
        var cells = new List<(ExistingElement Element, List<int> Cells)>();
        foreach (var z in cutHeights)
            foreach (var (element, outline) in existing.Cut(z))
            {
                var mine = grid.Fill(outline);
                // a solid block (a roof or floor plate cut through flat, a mass): not a wall, however long. Width = 2 × area / perimeter
                double area = mine.Count * CellMm * CellMm, width = 2 * area / Math.Max(1, outline.Sum(s => s.Length));
                if (mine.Count > 0 && (area <= 4e6 || width <= 1500)) cells.Add((element, mine));
            }
        var faces = new List<Segment2>();
        foreach (var z in cutHeights.Skip(1).Take(1)) foreach (var (_, outline) in existing.Cut(z)) faces.AddRange(outline);
        var material = new bool[grid.Count];
        var glazing = new bool[grid.Count];
        foreach (var (e, c) in cells)
            foreach (var i in c) { material[i] = true; if (Glazing(e.Category)) glazing[i] = true; }
        if (cells.Count == 0)
            return new Result(0, 0, 0, 0, 0, false, "The existing model has nothing at this floor's heights: built in full.");

        // outside: a point from which the open air is in sight past the existing model (rays fanned out), so a
        // gap in the existing model (a door, a side not modelled) does not make the floor 'outside'
        var rays = new Rays(grid, grid.Grow(material, 1));
        var inOutline = l.Outline != null ? grid.Fill(Edges(l.Outline)).ToHashSet() : null;
        // an existing model that does not enclose this floor (a basement it leaves out, a side not modelled) does
        // not tell outside from inside: there only what it overlaps is left out
        var probe = inOutline?.Where(i => !material[i]).Where((_, k) => k % 97 == 0).Take(400).ToList() ?? new List<int>();
        bool enclosed = probe.Count > 0 && probe.Count(i => rays.Outside(i)) < 0.3 * probe.Count;
        bool Outside(int i) => enclosed && !material[i] && rays.Outside(i);

        // walls: left out when mostly in the existing model or outside it, or when their windows are the existing model's
        var nearGlazing = grid.Grow(glazing, 20);
        var keep = new List<int>();
        for (int k = 0; k < walls.Count; k++)
        {
            int all = 0, gone = 0;
            foreach (var p in Footprint(walls[k]))
            {
                int i = grid.Index(p);
                if (i < 0) continue;
                all++;
                if (material[i] || Outside(i)) gone++;
            }
            var windows = l.Windows.Where(o => o.HostIndex == k).ToList();
            bool glazed = windows.Count > 0 && windows.Count(o => grid.Index(o.Center) is var i && i >= 0 && nearGlazing[i]) * 2 >= windows.Count;
            if (!(all > 0 && gone >= FacadeShare * all) && !glazed) keep.Add(k);
        }
        var map = Enumerable.Repeat(-1, walls.Count).ToArray();
        for (int k = 0; k < keep.Count; k++) map[keep[k]] = k;

        // kept walls meet the existing model: cut back out of it, or carried on to it
        int met = 0;
        var kept = keep.Select(k => walls[k]).ToList();
        for (int k = 0; k < kept.Count; k++)
        {
            var w = kept[k];
            if (w.Arc != null || w.Centerline.Length < 1) continue;
            var a = MeetEnd(grid, material, faces, w.Centerline.B, w.Centerline.A, w.Centerline.Length);
            var b = MeetEnd(grid, material, faces, w.Centerline.A, w.Centerline.B, w.Centerline.Length);
            if (a.DistanceTo(w.Centerline.A) > 1 || b.DistanceTo(w.Centerline.B) > 1)
            {
                kept[k] = w with { Centerline = w.Centerline with { A = a, B = b } };
                met++;
            }
        }
        int windowsBefore = l.Windows.Count, doorsBefore = l.Doors.Count;
        l.Walls = l.Walls with { Walls = kept };
        l.Windows = l.Windows.Where(o => o.HostIndex >= 0 && o.HostIndex < map.Length && map[o.HostIndex] >= 0)
            .Select(o => o with { HostIndex = map[o.HostIndex], AlsoCut = o.AlsoCut.Where(x => x.Wall >= 0 && x.Wall < map.Length && map[x.Wall] >= 0).Select(x => (map[x.Wall], x.At)).ToList() }).ToList();
        l.Doors = l.Doors.Where(o => o.HostIndex >= 0 && o.HostIndex < map.Length && map[o.HostIndex] >= 0).Select(o => o with { HostIndex = map[o.HostIndex] }).ToList();

        // piers, columns and fills: left out where the existing model (or the outside) has them
        bool Gone(IEnumerable<Vec2> points)
        {
            int all = 0, gone = 0;
            foreach (var p in points) { int i = grid.Index(p); if (i < 0) continue; all++; if (material[i] || Outside(i)) gone++; }
            return all > 0 && gone >= FacadeShare * all;
        }
        int details = l.Details.Count + l.Leftover.Count;
        l.Details = l.Details.Where(d => !Gone(Inside(d.Outline))).ToList();
        l.Leftover = l.Leftover.Where(c => !Gone(c)).ToList();
        details -= l.Details.Count + l.Leftover.Count;

        // floor and ceiling to the existing model's inner face
        string floorNote = "";
        if (l.Outline != null)
        {
            var inner = Inner(grid, l.Outline, material, Outside);
            if (inner.Count > 0)
            {
                double before = l.Outline.Area;
                l.Outline = inner[0];
                // a floor the existing model splits (two buildings, a party wall) keeps its other parts
                l.MoreOutlines = inner.Skip(1).Where(p => p.Area >= 2e6).ToList();
                floorNote = $"Floor {before / 1e6:0} → {inner.Sum(p => p.Area) / 1e6:0} m² inside the existing walls" + (inner.Count > 1 ? $" ({inner.Count} parts)" : "") + ".";
            }
            if (l.CeilingOutline != null && Inner(grid, l.CeilingOutline, material, Outside) is { Count: > 0 } ceiling) l.CeilingOutline = ceiling[0];
        }
        string note = (enclosed ? "" : "The existing model does not enclose this floor: only what it overlaps is left out. ") + floorNote;
        return new Result(walls.Count - keep.Count, windowsBefore - l.Windows.Count, doorsBefore - l.Doors.Count, met, details, enclosed, note.Trim());
    }

    /// <summary>Points over a wall's footprint: along its centre line, and along both faces of a thick wall.</summary>
    static IEnumerable<Vec2> Footprint(WallCandidate w)
    {
        var lines = new List<Segment2>();
        if (w.Arc is Arc2 arc) lines.AddRange(arc.Tessellate(100));
        else lines.Add(w.Centerline);
        double half = Math.Max(0, w.TypeThickness / 2 - 25);
        foreach (var s in lines)
        {
            int n = Math.Max(1, (int)Math.Ceiling(s.Length / 50));
            var across = s.Direction.Perp();
            for (int i = 0; i <= n; i++)
            {
                var p = s.A + (s.B - s.A) * (i / (double)n);
                yield return p;
                if (half >= 25) { yield return p + across * half; yield return p - across * half; }
            }
        }
    }

    /// <summary>
    /// The end <paramref name="end"/> of a wall coming from <paramref name="from"/>: cut back to where it leaves the
    /// existing model, or carried on to it when it stops just short.
    /// </summary>
    static Vec2 MeetEnd(Grid grid, bool[] material, List<Segment2> faces, Vec2 from, Vec2 end, double length)
    {
        var d = (end - from).Normalized();
        bool In(Vec2 p) { int i = grid.Index(p); return i >= 0 && material[i]; }
        // the existing faces the wall's line crosses near its end (exact, from the cut at window height), as distances from the end
        var hits = new List<double>();
        double reach = Math.Max(MaxCutBackMm, ReachMm) + 50;
        foreach (var f in faces)
        {
            var e = f.B - f.A;
            double den = d.Cross(e);
            if (Math.Abs(den) < 1e-9) continue;
            var w = f.A - end;
            double s = w.Cross(e) / den, u = w.Cross(d) / den;
            if (u >= 0 && u <= 1 && Math.Abs(s) <= reach) hits.Add(s);
        }
        if (In(end))
        {
            // back along the wall to where it leaves the existing model (not past the wall's middle)
            double limit = Math.Min(MaxCutBackMm, length / 2);
            var face = hits.Where(s => s < 0 && -s <= limit).OrderByDescending(s => s).Cast<double?>().FirstOrDefault(x => !In(end + d * (x!.Value - 2 * CellMm)));
            if (face is double f0) return end + d * f0;
            for (double s = 10; s <= limit; s += 10)
                if (!In(end - d * s)) return end - d * (s - 5);
            return end;
        }
        var ahead = hits.Where(s => s > 0 && s <= ReachMm).OrderBy(s => s).Cast<double?>().FirstOrDefault();
        if (ahead is double a0) return end + d * a0;
        for (double s = 10; s <= ReachMm; s += 10)
            if (In(end + d * s)) return end + d * s;
        return end;
    }

    static IEnumerable<Vec2> Inside(Polygon2 poly)
    {
        double x0 = poly.Vertices.Min(v => v.X), x1 = poly.Vertices.Max(v => v.X), y0 = poly.Vertices.Min(v => v.Y), y1 = poly.Vertices.Max(v => v.Y);
        double step = Math.Max(25, Math.Max(x1 - x0, y1 - y0) / 20);
        for (double x = x0 + step / 2; x < x1; x += step)
            for (double y = y0 + step / 2; y < y1; y += step)
            {
                var p = new Vec2(x, y);
                if (FacadeWalls.Contains(poly, p)) yield return p;
            }
    }

    /// <summary>
    /// The part of an outline clear of the existing model, traced from the grid and simplified, largest first.
    /// Slivers under 400 mm wide (between a survey line and the existing face), parts mostly outside and parts
    /// under 1 m² are dropped.
    /// </summary>
    static List<Polygon2> Inner(Grid grid, Polygon2 outline, bool[] material, Func<int, bool> outside)
    {
        var mask = new bool[grid.Count];
        foreach (var i in grid.Fill(Edges(outline))) if (!material[i]) mask[i] = true;
        var open = grid.Grow(grid.Shrink(mask, 4), 4);
        for (int i = 0; i < mask.Length; i++) mask[i] &= open[i];
        return grid.Trace(mask, 1e6, cells => cells.Where((_, k) => k % 7 == 0).Count(outside) * 2 < Math.Max(1, (cells.Count + 6) / 7))
            .Select(p => Simplify(p, 30)).Where(p => p.Vertices.Count >= 3 && p.Area >= 1e6).OrderByDescending(p => p.Area).ToList();
    }

    static List<Segment2> Edges(Polygon2 p) => p.Vertices.Select((v, k) => new Segment2(v, p.Vertices[(k + 1) % p.Vertices.Count])).ToList();

    /// <summary>Windows, doors and curtain walls: the existing model's glazing, always part of the façade.</summary>
    static bool Glazing(string category) => category is "Windows" or "Doors" or "CurtainWallPanels" or "CurtainWallMullions" or "CurtainWalls";

    /// <summary>
    /// Is a cell outside the existing model? Rays are sent out in 16 directions; a cell from which at least 5 reach
    /// the open edge of the plan without meeting the existing model sees the outside (a cell in the building sees
    /// out through a door or a gap in one or two directions at most).
    /// </summary>
    sealed class Rays
    {
        readonly Grid _grid; readonly bool[] _block;
        readonly Dictionary<int, bool> _seen = new();
        public Rays(Grid grid, bool[] block) { _grid = grid; _block = block; }
        public bool Outside(int cell)
        {
            if (_seen.TryGetValue(cell, out var known)) return known;
            int r0 = cell / _grid.N, c0 = cell % _grid.N, free = 0;
            for (int k = 0; k < 16; k++)
            {
                double a = k * Math.PI / 8, dx = Math.Cos(a), dy = Math.Sin(a);
                bool escaped = true;
                for (double s = 1; ; s += 0.7)
                {
                    int c = (int)Math.Round(c0 + dx * s), r = (int)Math.Round(r0 - dy * s);
                    if (c < 0 || r < 0 || c >= _grid.W || r >= _grid.H) break;
                    bool b = _block[r * _grid.N + c];
                    if (b) { escaped = false; break; }
                }
                if (escaped && ++free >= 5) break;
            }
            return _seen[cell] = free >= 5;
        }
    }

    /// <summary>Douglas-Peucker on a closed outline: the grid's 50 mm steps along slanted and curved edges smoothed out.</summary>
    internal static Polygon2 Simplify(Polygon2 poly, double tol)
    {
        var v = poly.Vertices;
        if (v.Count < 5) return poly;
        // start from the two points furthest apart, so the closed loop splits into two open runs
        int a = 0, b = v.Count / 2; double far = 0;
        for (int i = 0; i < v.Count; i++) if (v[i].DistanceTo(v[0]) > far) { far = v[i].DistanceTo(v[0]); b = i; }
        var keep = new bool[v.Count];
        keep[a] = keep[b] = true;
        void Run(int i, int j)
        {
            // points strictly between i and j going forward round the loop
            int n = (j - i + v.Count) % v.Count;
            if (n < 2) return;
            var s = new Segment2(v[i], v[j]);
            double best = -1; int at = -1;
            for (int k = 1; k < n; k++)
            {
                int m = (i + k) % v.Count;
                double dist = InteriorFit.Distance(s, v[m]);
                if (dist > best) { best = dist; at = m; }
            }
            if (best > tol) { keep[at] = true; Run(i, at); Run(at, j); }
        }
        Run(a, b); Run(b, a);
        var kept = v.Where((p, i) => keep[i]).ToList();
        return kept.Count >= 3 ? new Polygon2(kept) : poly;
    }

    /// <summary>A plan grid of <see cref="CellMm"/> cells, row 0 at the top.</summary>
    sealed class Grid
    {
        public readonly double X0, Y1;
        public readonly int W, H, N;
        public int Count => N * N;
        public Grid(double x0, double x1, double y0, double y1)
        {
            X0 = x0; Y1 = y1;
            W = (int)Math.Ceiling((x1 - x0) / CellMm); H = (int)Math.Ceiling((y1 - y0) / CellMm);
            N = Math.Max(W, H);
        }
        public int Index(Vec2 p)
        {
            int c = (int)Math.Floor((p.X - X0) / CellMm), r = (int)Math.Floor((Y1 - p.Y) / CellMm);
            return c < 0 || r < 0 || c >= W || r >= H ? -1 : r * N + c;
        }
        Vec2 Centre(int c, int r) => new(X0 + (c + 0.5) * CellMm, Y1 - (r + 0.5) * CellMm);

        /// <summary>The cells inside a closed outline (even-odd, so an element's own cut loops fill), and the cells its lines pass through.</summary>
        public List<int> Fill(List<Segment2> outline)
        {
            var result = new HashSet<int>();
            if (outline.Count == 0) return new List<int>();
            double minY = outline.Min(s => Math.Min(s.A.Y, s.B.Y)), maxY = outline.Max(s => Math.Max(s.A.Y, s.B.Y));
            int r0 = Math.Max(0, (int)Math.Floor((Y1 - maxY) / CellMm)), r1 = Math.Min(H - 1, (int)Math.Floor((Y1 - minY) / CellMm));
            var xs = new List<double>();
            for (int r = r0; r <= r1; r++)
            {
                double y = Y1 - (r + 0.5) * CellMm;
                xs.Clear();
                foreach (var s in outline)
                    if ((s.A.Y > y) != (s.B.Y > y)) xs.Add(s.A.X + (y - s.A.Y) * (s.B.X - s.A.X) / (s.B.Y - s.A.Y));
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int c0 = Math.Max(0, (int)Math.Ceiling((xs[k] - X0) / CellMm - 0.5)), c1 = Math.Min(W - 1, (int)Math.Floor((xs[k + 1] - X0) / CellMm - 0.5));
                    for (int c = c0; c <= c1; c++) result.Add(r * N + c);
                }
            }
            // thin parts (glass, a mullion) narrower than a cell: the cells their lines cross
            foreach (var s in outline)
            {
                int n = Math.Max(1, (int)Math.Ceiling(s.Length / (CellMm / 2)));
                for (int k = 0; k <= n; k++) { int i = Index(s.A + (s.B - s.A) * (k / (double)n)); if (i >= 0) result.Add(i); }
            }
            return result.ToList();
        }

        public bool[] Grow(bool[] mask, int by)
        {
            var result = (bool[])mask.Clone();
            for (int step = 0; step < by; step++)
            {
                var src = (bool[])result.Clone();
                for (int r = 0; r < H; r++)
                    for (int c = 0; c < W; c++)
                        if (!src[r * N + c] && (c > 0 && src[r * N + c - 1] || c < W - 1 && src[r * N + c + 1] || r > 0 && src[(r - 1) * N + c] || r < H - 1 && src[(r + 1) * N + c]))
                            result[r * N + c] = true;
            }
            return result;
        }

        public bool[] Shrink(bool[] mask, int by)
        {
            var inv = mask.Select(m => !m).ToArray();
            return Grow(inv, by).Select(m => !m).ToArray();
        }

        /// <summary>The outlines of the masked cells, one per separate part of at least the given area (mm²).</summary>
        public List<Polygon2> Trace(bool[] mask, double minAreaMm2, Func<List<int>, bool>? keep = null)
        {
            var region = new int[Count];
            Array.Fill(region, -1);
            var parts = new List<Polygon2>();
            var queue = new Queue<int>();
            int next = 0;
            for (int s = 0; s < mask.Length; s++)
            {
                if (!mask[s] || region[s] >= 0) continue;
                var cells = new List<int>();
                region[s] = next; queue.Enqueue(s);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue(); cells.Add(i);
                    int r = i / N, c = i % N;
                    foreach (var j in new[] { c > 0 ? i - 1 : -1, c < N - 1 ? i + 1 : -1, r > 0 ? i - N : -1, r < N - 1 ? i + N : -1 })
                        if (j >= 0 && mask[j] && region[j] < 0) { region[j] = next; queue.Enqueue(j); }
                }
                int id = next++;
                if (cells.Count * CellMm * CellMm < minAreaMm2 || keep != null && !keep(cells)) continue;
                var outer = SiteContextBuilder.Boundary(cells, region, id, N).OrderByDescending(SiteContextBuilder.SignedArea).First();
                parts.Add(SiteContextBuilder.Corners(outer, 0, 0, (gx, gy) => new Vec2(X0 + gx * CellMm, Y1 + gy * CellMm)));
            }
            return parts;
        }
    }
}
