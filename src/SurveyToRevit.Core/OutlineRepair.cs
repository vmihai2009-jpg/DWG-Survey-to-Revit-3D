using SurveyToRevit.Core.Site;

namespace SurveyToRevit.Core;

/// <summary>
/// An outline Revit refuses (it crosses or touches itself) redrawn on a fine grid: the area it encloses,
/// traced back into clean outlines, one per separate part. Edges come out in 100 mm steps square to the
/// model, which suits the survey's own walls.
/// </summary>
public static class OutlineRepair
{
    public static List<Polygon2> Clean(Polygon2 poly, double cellMm = 100)
    {
        double x0 = poly.Vertices.Min(v => v.X) - cellMm, y1 = poly.Vertices.Max(v => v.Y) + cellMm;
        int w = (int)Math.Ceiling((poly.Vertices.Max(v => v.X) + cellMm - x0) / cellMm);
        int h = (int)Math.Ceiling((y1 - poly.Vertices.Min(v => v.Y) + cellMm) / cellMm);
        int n = Math.Max(w, h);
        var inside = new bool[n * n];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                inside[r * n + c] = FacadeWalls.Contains(poly, new Vec2(x0 + (c + 0.5) * cellMm, y1 - (r + 0.5) * cellMm));
        var region = new int[n * n];
        Array.Fill(region, -1);
        var parts = new List<Polygon2>();
        var queue = new Queue<int>();
        int next = 0;
        for (int s = 0; s < inside.Length; s++)
        {
            if (!inside[s] || region[s] >= 0) continue;
            var cells = new List<int>();
            region[s] = next; queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(); cells.Add(i);
                int r = i / n, c = i % n;
                foreach (var j in new[] { c > 0 ? i - 1 : -1, c < n - 1 ? i + 1 : -1, r > 0 ? i - n : -1, r < n - 1 ? i + n : -1 })
                    if (j >= 0 && inside[j] && region[j] < 0) { region[j] = next; queue.Enqueue(j); }
            }
            int id = next++;
            if (cells.Count < 4) continue;
            var outer = SiteContextBuilder.Boundary(cells, region, id, n).OrderByDescending(SiteContextBuilder.SignedArea).First();
            parts.Add(SiteContextBuilder.Corners(outer, 0, 0, (gx, gy) => new Vec2(x0 + gx * cellMm, y1 + gy * cellMm)));
        }
        return parts.OrderByDescending(p => p.Area).ToList();
    }
}
