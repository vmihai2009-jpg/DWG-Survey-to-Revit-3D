namespace SurveyToRevit.Core;

/// <summary>
/// A plain grid over part of the plan for shape work that drawn linework makes awkward to do
/// exactly: closed shapes from loose lines (a column drawn as four separate lines), and the part
/// of one outline not covered by another. Cells are square, <see cref="Cell"/> mm across.
/// </summary>
public sealed class ShapeGrid
{
    public double Cell { get; }
    readonly double _x0, _y0;
    public int W { get; }
    public int H { get; }

    public ShapeGrid(double minX, double minY, double maxX, double maxY, double cell)
    {
        Cell = cell; _x0 = minX - 2 * cell; _y0 = minY - 2 * cell;
        W = (int)Math.Ceiling((maxX - _x0) / cell) + 2;
        H = (int)Math.Ceiling((maxY - _y0) / cell) + 2;
    }

    public static ShapeGrid Around(IEnumerable<Vec2> points, double cell, double margin = 0, double maxSpan = 300_000)
    {
        // stray far-off entities (a title block, a north point miles away) are left outside the grid
        var all = points.ToList();
        var mx = all.Select(v => v.X).OrderBy(x => x).ElementAt(all.Count / 2);
        var my = all.Select(v => v.Y).OrderBy(y => y).ElementAt(all.Count / 2);
        var p = all.Where(v => Math.Abs(v.X - mx) <= maxSpan / 2 && Math.Abs(v.Y - my) <= maxSpan / 2).ToList();
        if (p.Count == 0) p = new List<Vec2> { new(mx, my) };
        return new ShapeGrid(p.Min(v => v.X) - margin, p.Min(v => v.Y) - margin, p.Max(v => v.X) + margin, p.Max(v => v.Y) + margin, cell);
    }

    public Vec2 Centre(int i) => new(_x0 + (i % W + 0.5) * Cell, _y0 + (i / W + 0.5) * Cell);

    /// <summary>Cells whose centre is inside the polygon (scanline).</summary>
    public bool[] Fill(Polygon2 poly)
    {
        var m = new bool[W * H];
        var v = poly.Vertices;
        var xs = new List<double>();
        for (int y = 0; y < H; y++)
        {
            double py = _y0 + (y + 0.5) * Cell;
            xs.Clear();
            for (int i = 0, j = v.Count - 1; i < v.Count; j = i++)
                if ((v[i].Y > py) != (v[j].Y > py))
                    xs.Add(v[i].X + (py - v[i].Y) * (v[j].X - v[i].X) / (v[j].Y - v[i].Y));
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int a = (int)Math.Ceiling((xs[k] - _x0) / Cell - 0.5), b = (int)Math.Floor((xs[k + 1] - _x0) / Cell - 0.5);
                for (int x = Math.Max(0, a); x <= Math.Min(W - 1, b); x++) m[y * W + x] = true;
            }
        }
        return m;
    }

    /// <summary>Cells holding these points.</summary>
    public bool[] Mark(IEnumerable<Vec2> points)
    {
        var m = new bool[W * H];
        foreach (var p in points)
        {
            double fx = (p.X - _x0) / Cell, fy = (p.Y - _y0) / Cell;
            if (fx >= 0 && fy >= 0 && fx < W && fy < H) m[(int)fy * W + (int)fx] = true;
        }
        return m;
    }

    /// <summary>Cells the lines pass through.</summary>
    public bool[] Draw(IEnumerable<Segment2> lines)
    {
        var m = new bool[W * H];
        double diag = Math.Sqrt((double)W * W + (double)H * H) * Cell;
        foreach (var s in lines)
        {
            if (!(s.Length <= diag * 2)) continue; // runs far outside the grid (or is not a number)
            int n = Math.Max(1, (int)Math.Ceiling(s.Length / (Cell / 2)));
            for (int k = 0; k <= n; k++)
            {
                var p = s.A + (s.B - s.A) * ((double)k / n);
                double fx = (p.X - _x0) / Cell, fy = (p.Y - _y0) / Cell;
                if (fx >= 0 && fy >= 0 && fx < W && fy < H) m[(int)fy * W + (int)fx] = true;
            }
        }
        return m;
    }

    /// <summary>Closed shapes drawn by the lines: everything the outside cannot reach (lines included).</summary>
    public bool[] Enclosed(bool[] lines)
    {
        var outside = new bool[W * H];
        var q = new Queue<int>();
        void Seed(int i) { if (!lines[i] && !outside[i]) { outside[i] = true; q.Enqueue(i); } }
        for (int x = 0; x < W; x++) { Seed(x); Seed((H - 1) * W + x); }
        for (int y = 0; y < H; y++) { Seed(y * W); Seed(y * W + W - 1); }
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % W, y = i / W;
            if (x > 0) Seed(i - 1);
            if (x < W - 1) Seed(i + 1);
            if (y > 0) Seed(i - W);
            if (y < H - 1) Seed(i + W);
        }
        var m = new bool[W * H];
        for (int i = 0; i < m.Length; i++) m[i] = !outside[i];
        return m;
    }

    public static bool[] Minus(bool[] a, bool[] b) { var m = new bool[a.Length]; for (int i = 0; i < a.Length; i++) m[i] = a[i] && !b[i]; return m; }

    /// <summary>Grows (r &gt; 0) or shrinks (r &lt; 0) by |r| cells (square neighbourhood).</summary>
    public bool[] Grow(bool[] m, int r)
    {
        if (r == 0) return (bool[])m.Clone();
        bool grow = r > 0; r = Math.Abs(r);
        var tmp = new bool[m.Length]; var res = new bool[m.Length];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool any = !grow;
                for (int d = -r; d <= r; d++)
                {
                    int xx = x + d;
                    bool v = xx >= 0 && xx < W && m[y * W + xx];
                    if (grow ? v : !v) { any = grow; break; }
                }
                tmp[y * W + x] = any;
            }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool any = !grow;
                for (int d = -r; d <= r; d++)
                {
                    int yy = y + d;
                    bool v = yy >= 0 && yy < H && tmp[yy * W + x];
                    if (grow ? v : !v) { any = grow; break; }
                }
                res[y * W + x] = any;
            }
        return res;
    }

    /// <summary>Connected pieces (4-neighbour) as outlines, each at least minArea mm².</summary>
    public List<Polygon2> Outlines(bool[] m, double minArea)
    {
        var seen = new bool[m.Length];
        var result = new List<Polygon2>();
        var q = new Queue<int>();
        for (int s = 0; s < m.Length; s++)
        {
            if (!m[s] || seen[s]) continue;
            var cells = new List<int>();
            seen[s] = true; q.Enqueue(s);
            while (q.Count > 0)
            {
                int i = q.Dequeue(); cells.Add(i);
                int x = i % W, y = i / W;
                foreach (var j in new[] { x > 0 ? i - 1 : -1, x < W - 1 ? i + 1 : -1, y > 0 ? i - W : -1, y < H - 1 ? i + W : -1 })
                    if (j >= 0 && m[j] && !seen[j]) { seen[j] = true; q.Enqueue(j); }
            }
            if (cells.Count * Cell * Cell < minArea) continue;
            if (Trace(cells) is Polygon2 p && p.Area >= minArea) result.Add(p);
        }
        return result;
    }

    // outer boundary of a piece along cell edges, then straightened (Douglas–Peucker, one cell)
    Polygon2? Trace(List<int> cells)
    {
        var set = new HashSet<int>(cells);
        bool Has(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && set.Contains(y * W + x);
        // walk the boundary edges with the piece on the left, starting at the lowest-leftmost cell's bottom edge
        int start = cells.Min();
        int sx = start % W, sy = start / W;
        var pts = new List<(int x, int y)>();
        int cx = sx, cy = sy, dir = 0; // corner (cx, cy); dir 0 east, 1 north, 2 west, 3 south
        var dx = new[] { 1, 0, -1, 0 }; var dy = new[] { 0, 1, 0, -1 };
        for (int guard = 0; guard < cells.Count * 4 + 8; guard++)
        {
            pts.Add((cx, cy));
            cx += dx[dir]; cy += dy[dir];
            if (cx == sx && cy == sy) break;
            // cells ahead-left and ahead-right of the corner, relative to dir
            (int x, int y) Left(int d) => d switch { 0 => (cx, cy), 1 => (cx - 1, cy), 2 => (cx - 1, cy - 1), _ => (cx, cy - 1) };
            (int x, int y) Right(int d) => d switch { 0 => (cx, cy - 1), 1 => (cx, cy), 2 => (cx - 1, cy), _ => (cx - 1, cy - 1) };
            var l = Left(dir); var r = Right(dir);
            bool hl = Has(l.x, l.y), hr = Has(r.x, r.y);
            if (hr) dir = (dir + 3) % 4;        // turn right
            else if (!hl) dir = (dir + 1) % 4;  // turn left
        }
        if (pts.Count < 4) return null;
        var mm = pts.Select(p => new Vec2(_x0 + p.x * Cell, _y0 + p.y * Cell)).ToList();
        var simple = Simplify(mm, Cell * 1.01);
        return simple.Count >= 3 ? new Polygon2(simple) : null;
    }

    static List<Vec2> Simplify(List<Vec2> ring, double tol)
    {
        // split the closed ring at its two farthest-apart points and simplify each half
        int far = 0; double best = -1;
        for (int i = 0; i < ring.Count; i++) { double d = ring[i].DistanceTo(ring[0]); if (d > best) { best = d; far = i; } }
        var a = DP(ring.Take(far + 1).ToList(), tol);
        var b = DP(ring.Skip(far).Append(ring[0]).ToList(), tol);
        return a.Take(a.Count - 1).Concat(b.Take(b.Count - 1)).ToList();
    }

    static List<Vec2> DP(List<Vec2> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var s = new Segment2(pts[0], pts[^1]);
        int idx = -1; double max = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double d = s.Length < 1e-9 ? pts[i].DistanceTo(pts[0]) : Math.Abs((pts[i] - s.A).Cross(s.Direction));
            if (d > max) { max = d; idx = i; }
        }
        if (max <= tol) return new List<Vec2> { pts[0], pts[^1] };
        var left = DP(pts.Take(idx + 1).ToList(), tol);
        var right = DP(pts.Skip(idx).ToList(), tol);
        return left.Take(left.Count - 1).Concat(right).ToList();
    }
}
