namespace SurveyToRevit.Core;

/// <summary>A window to place: on wall <see cref="HostIndex"/>, centred at <see cref="Center"/>; sizes in mm, heights above the floor.</summary>
public sealed record WindowCandidate(int HostIndex, Vec2 Center, double WidthMm, double SillMm, double HeadMm, bool HeightsFromSurvey)
{
    /// <summary>For a window in an outside wall: the direction out of the building (the window's outer face points this way).</summary>
    public Vec2? Outward { get; init; }
    public double HeightMm => HeadMm - SillMm;
}

/// <summary>
/// Windows from the window layer: touching window lines are one window; its width is
/// their extent along the wall it sits in. Survey sill and head texts nearby
/// ("S 0.94", "H 2.58", "fs 0.92" + "sh 1.88"; see <see cref="SurveyHeights"/>) give its heights; a window
/// without them takes the floor's usual surveyed sill and head, and only then the defaults.
/// </summary>
public static class WindowFinder
{
    public const double DefaultSillMm = 900, DefaultHeightMm = 1500;

    public static List<WindowCandidate> Find(IReadOnlyList<Segment2> windowLines, IReadOnlyList<WallCandidate> walls,
        IReadOnlyList<(Vec2 Position, string Value)> texts, double storeyMm,
        double joinMm = 150, double textReach = 2000, double minWidth = 300, double maxWidth = 4000, Polygon2? outline = null)
    {
        var lines = windowLines.Where(l => l.Length >= 20).ToList();
        // touching lines → one window (union-find)
        var parent = Enumerable.Range(0, lines.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
                if (Near(lines[i], lines[j], joinMm)) parent[Root(i)] = Root(j);

        var notes = SurveyHeights.Read(texts);

        var found = new List<(int Host, Vec2 Center, double Width, double? Sill, double? Head, Vec2? Out)>();
        foreach (var cluster in Enumerable.Range(0, lines.Count).GroupBy(Root).Select(g => g.Select(i => lines[i]).ToList()))
        {
            var main = cluster.OrderByDescending(l => l.Length).First();
            var pts = cluster.SelectMany(l => new[] { l.A, l.B }).ToList();
            var mid = new Vec2(pts.Average(p => p.X), pts.Average(p => p.Y));

            // host: a parallel wall the window sits in, that holds the whole window if one does;
            // then the thickest (the façade, not a lining in front of it)
            int host = -1; double best = double.MaxValue;
            for (int k = 0; k < walls.Count; k++)
            {
                var w = walls[k];
                if (w.Curtain) continue;
                double off = Math.Abs(WallGeometry.Offset(w, mid));
                double along = WallGeometry.Along(w, mid), len = WallGeometry.Length(w);
                if (off > w.TypeThickness / 2 + 400 || along < 0 || along > len) continue;
                if (SegmentCleaner.AngleDiffDeg(WallGeometry.AngleDegAt(w, along), main.AngleDeg) > (w.Arc != null ? 15 : 5)) continue;
                var along2 = pts.Select(p => WallGeometry.Along(w, p)).ToList();
                double drawn = Math.Max(1, along2.Max() - along2.Min());
                double held = (Math.Min(along2.Max(), len) - Math.Max(along2.Min(), 0)) / drawn;
                double score = (held >= 0.9 ? 0 : 1e7) - w.TypeThickness * 10 + off;
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
            found.Add((host, center, width, s, h, Outward(hw, (lo + hi) / 2, outline)));
        }

        // where the survey gives no height for a window, the floor's usual surveyed sill and head
        // are used (so a row of windows lines up), and only failing those the defaults
        double? usualSill = Median(found.Where(f => f.Sill != null).Select(f => f.Sill!.Value));
        double? usualHead = Median(found.Where(f => f.Head != null && f.Sill != null && f.Head > f.Sill + 300).Select(f => f.Head!.Value));
        var result = new List<WindowCandidate>();
        foreach (var f in found)
        {
            double sill = f.Sill ?? usualSill ?? DefaultSillMm;
            double head = f.Head is double hv && hv > sill + 300 ? hv
                        : usualHead is double uh && uh > sill + 300 ? uh : sill + DefaultHeightMm;
            head = Math.Min(head, storeyMm - 150);
            if (head - sill < 300) continue;
            result.Add(new WindowCandidate(f.Host, f.Center, f.Width, Math.Round(sill / 10) * 10, Math.Round(head / 10) * 10, f.Sill != null && f.Head != null)
            { Outward = f.Out });
        }
        return RemoveOverlaps(result);
    }

    /// <summary>The side of a wall that is outside the building, as a unit vector; null for a wall inside.</summary>
    public static Vec2? Outward(WallCandidate w, double along, Polygon2? outline)
    {
        if (outline == null) return null;
        var p = WallGeometry.PointAt(w, along);
        double a = WallGeometry.AngleDegAt(w, along) * Math.PI / 180;
        var n = new Vec2(-Math.Sin(a), Math.Cos(a));
        double reach = w.TypeThickness / 2 + 400;
        bool plus = FacadeWalls.Contains(outline, p + n * reach), minus = FacadeWalls.Contains(outline, p - n * reach);
        return plus == minus ? null : plus ? n * -1 : n;
    }

    static double? Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? null : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
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
