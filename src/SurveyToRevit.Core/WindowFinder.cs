namespace SurveyToRevit.Core;

/// <summary>A window to place: on wall <see cref="HostIndex"/>, centred at <see cref="Center"/>; sizes in mm, heights above the floor.</summary>
public sealed record WindowCandidate(int HostIndex, Vec2 Center, double WidthMm, double SillMm, double HeadMm, bool HeightsFromSurvey)
{
    public double HeightMm => HeadMm - SillMm;
}

/// <summary>
/// Windows from the window layer: touching window lines are one window; its width is
/// their extent along the wall it sits in. Survey sill and head texts nearby
/// ("S 0.94", "H 2.58", "fs 0.92" + "sh 1.88"; see <see cref="SurveyHeights"/>) give its heights; otherwise defaults.
/// </summary>
public static class WindowFinder
{
    public const double DefaultSillMm = 900, DefaultHeightMm = 1500;

    public static List<WindowCandidate> Find(IReadOnlyList<Segment2> windowLines, IReadOnlyList<WallCandidate> walls,
        IReadOnlyList<(Vec2 Position, string Value)> texts, double storeyMm,
        double joinMm = 150, double textReach = 2000, double minWidth = 300, double maxWidth = 4000)
    {
        var lines = windowLines.Where(l => l.Length >= 20).ToList();
        // touching lines → one window (union-find)
        var parent = Enumerable.Range(0, lines.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
                if (Near(lines[i], lines[j], joinMm)) parent[Root(i)] = Root(j);

        var notes = SurveyHeights.Read(texts);

        var result = new List<WindowCandidate>();
        foreach (var cluster in Enumerable.Range(0, lines.Count).GroupBy(Root).Select(g => g.Select(i => lines[i]).ToList()))
        {
            var main = cluster.OrderByDescending(l => l.Length).First();
            var pts = cluster.SelectMany(l => new[] { l.A, l.B }).ToList();
            var mid = new Vec2(pts.Average(p => p.X), pts.Average(p => p.Y));

            // host: a parallel wall the window sits in; the thickest (the façade, not a lining in front of it)
            int host = -1; double best = double.MaxValue;
            for (int k = 0; k < walls.Count; k++)
            {
                var w = walls[k];
                double off = Math.Abs(WallGeometry.Offset(w, mid));
                double along = WallGeometry.Along(w, mid);
                if (off > w.TypeThickness / 2 + 400 || along < 0 || along > WallGeometry.Length(w)) continue;
                if (SegmentCleaner.AngleDiffDeg(WallGeometry.AngleDegAt(w, along), main.AngleDeg) > (w.Arc != null ? 15 : 5)) continue;
                double score = -walls[k].TypeThickness * 10 + off;
                if (score < best) { best = score; host = k; }
            }
            if (host < 0) continue;
            var hw = walls[host];
            var proj = pts.Select(p => WallGeometry.Along(hw, p)).ToList();
            double lo = Math.Max(proj.Min(), 0), hi = Math.Min(proj.Max(), WallGeometry.Length(hw));
            double width = Math.Round((hi - lo) / 10) * 10;
            if (width < minWidth || width > maxWidth) continue;
            var center = WallGeometry.PointAt(hw, (lo + hi) / 2);

            var s = SurveyHeights.Nearest(notes, HeightKind.Sill, center, textReach);
            var h = SurveyHeights.Nearest(notes, HeightKind.Head, center, textReach);
            // a window height (sill to head) instead of a head
            if (h == null && s != null && SurveyHeights.Nearest(notes, HeightKind.OpeningHeight, center, textReach) is double oh) h = s + oh;
            double sill = s ?? DefaultSillMm;
            double head = h is double hv && hv > sill + 300 ? hv : sill + DefaultHeightMm;
            head = Math.Min(head, storeyMm - 150);
            if (head - sill < 300) continue;
            result.Add(new WindowCandidate(host, center, width, Math.Round(sill / 10) * 10, Math.Round(head / 10) * 10, s != null && h != null));
        }
        return RemoveOverlaps(result);
    }

    // Windows drawn with several clusters in one opening (frame, glass) would overlap: keep the widest.
    static List<WindowCandidate> RemoveOverlaps(List<WindowCandidate> windows)
    {
        var kept = new List<WindowCandidate>();
        foreach (var w in windows.OrderByDescending(w => w.WidthMm))
            if (!kept.Any(k => k.HostIndex == w.HostIndex && k.Center.DistanceTo(w.Center) < (k.WidthMm + w.WidthMm) / 2 + 100))
                kept.Add(w);
        return kept;
    }

    static bool Near(Segment2 a, Segment2 b, double d) =>
        Dist(a.A, b) <= d || Dist(a.B, b) <= d || Dist(b.A, a) <= d || Dist(b.B, a) <= d;

    static double Dist(Vec2 p, Segment2 s)
    {
        var d = s.B - s.A; double len2 = d.Dot(d);
        double t = len2 == 0 ? 0 : Math.Clamp((p - s.A).Dot(d) / len2, 0, 1);
        return p.DistanceTo(s.A + d * t);
    }
}
