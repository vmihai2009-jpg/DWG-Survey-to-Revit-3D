namespace SurveyToRevit.Core.Survey;

/// <summary>Something the engine could not classify confidently: becomes a generic model for review.</summary>
public sealed record UnclearRegion(Polygon2 Outline, string Reason);

public sealed record PlanResult(
    WallDetectionResult Walls,
    Polygon2? Outline,
    IReadOnlyList<UnclearRegion> Unclear,
    int Rooms)
{
    /// <summary>Is this point (model mm) wall material?</summary>
    public Func<Vec2, bool>? IsMaterial { get; init; }
}

/// <summary>
/// Room-based plan reading, for surveys where rooms are drawn as outlines.
/// The plan is drawn onto a fine grid (25 mm). Space reachable from outside is
/// outside; space reachable from a room label or spot height is a room; small
/// enclosed pockets narrower than a wall are wall material; wider unlabelled
/// pockets are rooms; those in between are unclear. The result is a map of
/// wall material (<see cref="PlanResult.IsMaterial"/>), the building outline and
/// the unclear areas; walls themselves are measured from the drawn lines by
/// <see cref="WallDetector"/>, which uses the map to reject line pairs with room between them.
/// All input and output is in model coordinates (mm, building squared to the axes).
/// </summary>
public sealed class PlanRaster
{
    public double Px { get; init; } = 25;
    /// <summary>Gaps in linework up to about twice this are closed when telling inside from outside.</summary>
    public int GapClosePx { get; init; } = 2;
    /// <summary>Openings in the outside wall up to about twice this (entrances, missing glazing) are bridged when finding the building edge.</summary>
    public double FacadeGapClose { get; init; } = 1000;
    /// <summary>Spaces narrower than this are never rooms: thin strips between parallel lines are wall material even where they open into a room.</summary>
    public double NarrowestRoom { get; init; } = 600;
    /// <summary>Enclosed unlabelled pockets wider than this are rooms (cupboards, shafts, voids).</summary>
    public double RoomMinWidth { get; init; } = 1500;

    int _w, _h;
    double _x0, _y0;

    /// <summary>Development aid: receives the classified grid (width, height, cell classes).</summary>
    public Action<int, int, byte[]>? Debug { get; init; }

    public PlanResult Analyze(IReadOnlyList<Segment2> wallLines, IReadOnlyList<Segment2> openingLines, IReadOnlyList<Vec2> roomMarkers, Tolerances tol)
    {
        if (wallLines.Count == 0) return new PlanResult(Empty(), null, Array.Empty<UnclearRegion>(), 0);
        var pts = wallLines.SelectMany(s => new[] { s.A, s.B }).ToList();
        double margin = FacadeGapClose + 1000;
        _x0 = pts.Min(p => p.X) - margin; _y0 = pts.Min(p => p.Y) - margin;
        _w = (int)Math.Ceiling((pts.Max(p => p.X) + margin - _x0) / Px);
        _h = (int)Math.Ceiling((pts.Max(p => p.Y) + margin - _y0) / Px);
        int n = _w * _h;

        // 1. Lines. Wall and window lines are wall material; door lines only separate spaces.
        var line = new bool[n];
        var solidLine = new bool[n];
        foreach (var s in wallLines) Draw(s, line, solidLine);
        foreach (var s in openingLines) Draw(s, line, IsWindow(s.Layer) ? solidLine : null);

        // 2. Outside and rooms, flooding with gaps closed, then growing back up to the lines.
        var label = new int[n]; // 0 unknown, -1 outside, >0 room
        var queue = new int[n];
        int facadeR = (int)Math.Round(FacadeGapClose / Px);
        var shut = Dilate(line, facadeR);
        for (int x = 0; x < _w; x++) { Flood(x, 0, -1, shut, label, queue); Flood(x, _h - 1, -1, shut, label, queue); }
        for (int y = 0; y < _h; y++) { Flood(0, y, -1, shut, label, queue); Flood(_w - 1, y, -1, shut, label, queue); }
        GrowLabels(label, line, facadeR + 1);
        var closed = Dilate(line, GapClosePx);
        int rooms = 0;
        foreach (var m in roomMarkers)
        {
            int c = Cell(m);
            if (c >= 0 && !closed[c] && label[c] == 0) Flood(c % _w, c / _w, ++rooms, closed, label, queue);
        }
        GrowLabels(label, line, GapClosePx + 1);

        // 3. Enclosed pockets: wall material, unclear, or unlabelled rooms, by width.
        var pocket = new bool[n];
        for (int i = 0; i < n; i++) pocket[i] = label[i] == 0 && !line[i];
        var pocketDist = Edt(pocket);
        var comp = new int[n];
        var solid = (bool[])solidLine.Clone();
        var unclear = new List<UnclearRegion>();
        int compId = 0;
        for (int i = 0; i < n; i++)
        {
            if (!pocket[i] || comp[i] != 0) continue;
            var cells = Component(i, pocket, comp, ++compId, queue);
            double width = (2 * cells.Max(c => pocketDist[c]) - 1) * Px;
            if (width <= tol.MaxWallThickness)
                foreach (var c in cells) solid[c] = true;
            else if (width > RoomMinWidth)
            {
                int room = ++rooms;
                foreach (var c in cells) label[c] = room;
            }
            else if (cells.Count * Px * Px > 0.25e6 && Outline(cells) is Polygon2 poly)
                unclear.Add(new UnclearRegion(poly, $"enclosed space {width:0} mm wide, no label: shaft, duct or very thick wall?"));
        }
        // Thin strips that open into a room at their ends (partitions with door gaps) are wall too:
        // keep as room only what a square of the narrowest room width can reach (morphological opening).
        // A square, not a circle, so the square corners of rooms stay rooms.
        int r = (int)Math.Round(NarrowestRoom / 2 / Px);
        var core = Dilate(line, r);
        for (int i = 0; i < n; i++) core[i] = !core[i];
        var reach = Dilate(core, r);
        for (int i = 0; i < n; i++)
            if (label[i] > 0 && !line[i] && !reach[i]) solid[i] = true;
        // Lines inside rooms that border nothing stay 1-2 cells thick; the skeleton filter drops them by thickness.

        if (Debug != null)
        {
            var cls = new byte[n];
            for (int i = 0; i < n; i++)
                cls[i] = (byte)(solid[i] ? 4 : line[i] ? 3 : label[i] == -1 ? 1 : label[i] > 0 ? 2 : 0);
            Debug(_w, _h, cls);
        }

        // 5. Floor outline: everything not outside.
        var inside = new bool[n];
        for (int i = 0; i < n; i++) inside[i] = label[i] != -1;
        var compIn = new int[n];
        List<int>? biggest = null;
        int id = 0;
        for (int i = 0; i < n; i++)
            if (inside[i] && compIn[i] == 0)
            {
                var cells = Component(i, inside, compIn, ++id, queue);
                if (biggest == null || cells.Count > biggest.Count) biggest = cells;
            }
        var outline = biggest != null ? BuildingOutline(biggest, queue) : null;

        var result = Empty();
        var material = solid;
        bool IsMaterial(Vec2 p)
        {
            int c = Cell(p);
            if (c < 0) return false;
            int x = c % _w, y = c / _w;
            // the strip centre, or right beside it for walls only a cell or two wide
            foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (In(x + dx, y + dy) && material[(y + dy) * _w + x + dx]) return true;
            return false;
        }
        return new PlanResult(result, outline, unclear, rooms) { IsMaterial = IsMaterial };
    }

    // Thin strands (a stray line leaking into the outside) and parts touching only at a corner
    // make the traced boundary cross itself. Opening then closing the shape by a few cells
    // (50-200 mm) removes strands and fuses pinch points; the smallest change that works is kept.
    Polygon2? BuildingOutline(List<int> cells, int[] queue)
    {
        Polygon2? last = null;
        foreach (int r in new[] { 0, 2, 4, 8 })
        {
            var mask = new bool[_w * _h];
            foreach (var c in cells) mask[c] = true;
            if (r > 0)
            {
                mask = Dilate(Erode(mask, r), r); // opening: strands go
                mask = Erode(Dilate(mask, r), r); // closing: pinch points fuse
            }
            var comp = new int[mask.Length];
            List<int>? best = null;
            int id = 0;
            foreach (var c in cells)
                if (mask[c] && comp[c] == 0)
                {
                    var part = Component(c, mask, comp, ++id, queue);
                    if (best == null || part.Count > best.Count) best = part;
                }
            if (best == null) continue;
            last = Outline(best, simplifyPx: 2);
            if (last == null || last.IsSimple()) return last;
        }
        return last;
    }

    bool[] Erode(bool[] src, int r)
    {
        var inv = new bool[src.Length];
        for (int i = 0; i < src.Length; i++) inv[i] = !src[i];
        var grown = Dilate(inv, r);
        for (int i = 0; i < grown.Length; i++) grown[i] = !grown[i];
        return grown;
    }

    static WallDetectionResult Empty() => new(Array.Empty<WallCandidate>(), Array.Empty<Segment2>(), Array.Empty<double>(), 0, 0, Array.Empty<double>());

    static bool IsWindow(string layer) => LayerRules.Default.Classify(layer) == LayerRole.Window;

    int Cell(Vec2 p)
    {
        int x = (int)((p.X - _x0) / Px), y = (int)((p.Y - _y0) / Px);
        return x < 0 || y < 0 || x >= _w || y >= _h ? -1 : y * _w + x;
    }

    Vec2 ToMm((int x, int y) c) => new(_x0 + (c.x + 0.5) * Px, _y0 + (c.y + 0.5) * Px);

    void Draw(Segment2 s, bool[] a, bool[]? b)
    {
        int x0 = (int)((s.A.X - _x0) / Px), y0 = (int)((s.A.Y - _y0) / Px);
        int x1 = (int)((s.B.X - _x0) / Px), y1 = (int)((s.B.Y - _y0) / Px);
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            if (x0 >= 0 && y0 >= 0 && x0 < _w && y0 < _h) { a[y0 * _w + x0] = true; if (b != null) b[y0 * _w + x0] = true; }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            // step one axis at a time so the line is 4-connected and blocks 4-connected floods
            if (e2 >= dy) { err += dy; x0 += sx; if (x0 >= 0 && y0 >= 0 && x0 < _w && y0 < _h) { a[y0 * _w + x0] = true; if (b != null) b[y0 * _w + x0] = true; } }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    bool[] Dilate(bool[] src, int r)
    {
        var tmp = new bool[src.Length];
        var dst = new bool[src.Length];
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
                if (src[y * _w + x])
                    for (int k = Math.Max(0, x - r); k <= Math.Min(_w - 1, x + r); k++) tmp[y * _w + k] = true;
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
                if (tmp[y * _w + x])
                    for (int k = Math.Max(0, y - r); k <= Math.Min(_h - 1, y + r); k++) dst[k * _w + x] = true;
        return dst;
    }

    void Flood(int sx, int sy, int value, bool[] wall, int[] label, int[] queue)
    {
        int start = sy * _w + sx;
        if (wall[start] || label[start] != 0) return;
        int head = 0, tail = 0;
        queue[tail++] = start; label[start] = value;
        while (head < tail)
        {
            int c = queue[head++], x = c % _w, y = c / _w;
            void Try(int nc) { if (!wall[nc] && label[nc] == 0) { label[nc] = value; queue[tail++] = nc; } }
            if (x > 0) Try(c - 1);
            if (x < _w - 1) Try(c + 1);
            if (y > 0) Try(c - _w);
            if (y < _h - 1) Try(c + _w);
        }
    }

    // Labels were flooded with gaps closed (lines thickened); extend them back up to the real lines.
    void GrowLabels(int[] label, bool[] line, int steps)
    {
        for (int s = 0; s < steps; s++)
        {
            var next = (int[])label.Clone();
            for (int y = 1; y < _h - 1; y++)
                for (int x = 1; x < _w - 1; x++)
                {
                    int c = y * _w + x;
                    if (label[c] != 0 || line[c]) continue;
                    int v = label[c - 1] != 0 ? label[c - 1] : label[c + 1] != 0 ? label[c + 1] : label[c - _w] != 0 ? label[c - _w] : label[c + _w];
                    if (v != 0) next[c] = v;
                }
            Array.Copy(next, label, label.Length);
        }
    }

    List<int> Component(int start, bool[] mask, int[] comp, int id, int[] queue)
    {
        var cells = new List<int>();
        int head = 0, tail = 0;
        queue[tail++] = start; comp[start] = id;
        while (head < tail)
        {
            int c = queue[head++], x = c % _w, y = c / _w;
            cells.Add(c);
            void Try(int nc) { if (mask[nc] && comp[nc] == 0) { comp[nc] = id; queue[tail++] = nc; } }
            if (x > 0) Try(c - 1);
            if (x < _w - 1) Try(c + 1);
            if (y > 0) Try(c - _w);
            if (y < _h - 1) Try(c + _w);
        }
        return cells;
    }

    /// <summary>Euclidean distance (in cells) from each true cell to the nearest false cell (Felzenszwalb–Huttenlocher).</summary>
    double[] Edt(bool[] mask)
    {
        const double Inf = 1e12;
        var f = new double[_w * _h];
        for (int i = 0; i < f.Length; i++) f[i] = mask[i] ? Inf : 0;
        var col = new double[Math.Max(_w, _h)];
        var res = new double[Math.Max(_w, _h)];
        for (int x = 0; x < _w; x++)
        {
            for (int y = 0; y < _h; y++) col[y] = f[y * _w + x];
            Edt1(col, _h, res);
            for (int y = 0; y < _h; y++) f[y * _w + x] = res[y];
        }
        for (int y = 0; y < _h; y++)
        {
            for (int x = 0; x < _w; x++) col[x] = f[y * _w + x];
            Edt1(col, _w, res);
            for (int x = 0; x < _w; x++) f[y * _w + x] = Math.Sqrt(res[x]);
        }
        return f;
    }

    static void Edt1(double[] f, int n, double[] d)
    {
        var v = new int[n];
        var z = new double[n + 1];
        int k = 0;
        v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s;
            while (true)
            {
                s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                if (s <= z[k] && k > 0) k--; else break;
            }
            if (s <= z[k]) { v[0] = q; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity; k = 0; continue; }
            k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            d[q] = (double)(q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }

    bool In(int x, int y) => x >= 0 && y >= 0 && x < _w && y < _h;

    /// <summary>Douglas–Peucker: chain of cells → straight pieces (start, end).</summary>
    static List<((int x, int y) a, (int x, int y) b)> Simplify(List<(int x, int y)> chain, double tolPx)
    {
        var keep = new bool[chain.Count];
        keep[0] = keep[^1] = true;
        void Rec(int i, int j)
        {
            if (j <= i + 1) return;
            var (ax, ay) = chain[i]; var (bx, by) = chain[j];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int best = -1; double bestD = 0;
            for (int k = i + 1; k < j; k++)
            {
                var (px, py) = chain[k];
                double d = len < 1e-9 ? Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay))
                                      : Math.Abs((bx - ax) * (ay - py) - (ax - px) * (by - ay)) / len;
                if (d > bestD) { bestD = d; best = k; }
            }
            if (bestD > tolPx) { keep[best] = true; Rec(i, best); Rec(best, j); }
        }
        Rec(0, chain.Count - 1);
        var idx = Enumerable.Range(0, chain.Count).Where(k => keep[k]).ToList();
        return idx.Zip(idx.Skip(1), (i, j) => (chain[i], chain[j])).ToList();
    }

    /// <summary>Outer boundary of a set of cells as a simplified polygon (mm).</summary>
    Polygon2? Outline(List<int> cells, double simplifyPx = 1.5)
    {
        var set = new HashSet<int>(cells);
        // start at the lowest, leftmost cell; trace the boundary with Moore-neighbour tracing
        int start = cells.Min();
        int sx = start % _w, sy = start / _w;
        var dirs = new (int dx, int dy)[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) };
        bool Has(int x, int y) => In(x, y) && set.Contains(y * _w + x);
        var path = new List<(int x, int y)> { (sx, sy) };
        int cx = sx, cy = sy, back = 4; // came from the left
        for (int guard = 0; guard < cells.Count * 4 + 8; guard++)
        {
            int found = -1;
            for (int k = 1; k <= 8; k++)
            {
                int d = (back + k) % 8;
                if (Has(cx + dirs[d].dx, cy + dirs[d].dy)) { found = d; break; }
            }
            if (found < 0) break;
            cx += dirs[found].dx; cy += dirs[found].dy;
            if (cx == sx && cy == sy) break;
            path.Add((cx, cy));
            back = (found + 4) % 8;
        }
        if (path.Count < 4) return null;
        path.Add(path[0]);
        var pieces = Simplify(path, simplifyPx);
        var verts = pieces.Select(p => ToMm(p.a)).ToList();
        if (verts.Count < 3) return null;
        var poly = new Polygon2(verts);
        return poly.Area < 0.25e6 ? null : poly;
    }
}
