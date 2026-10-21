using System.Text.RegularExpressions;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace SurveyToRevit.Core.Survey;

/// <summary>
/// A survey section: its name, where it cuts the building (model mm, from the key plan on the
/// sheet; null when the cut could not be placed) and the heights written on it (above datum, mm).
/// </summary>
public sealed record SurveySection(string Name, Segment2? Cut, IReadOnlyList<double> HeightsMm, string File, string Note);

/// <summary>
/// Reads survey section sheets, the same way for any survey. A section sheet carries one or
/// more sections titled "Section A" / "Section-B" / "SECTION 1"; spot heights near a title
/// belong to that section. Where the sheet has a key plan (a small copy of the building with
/// the cut lines, each end marked with the section's letter), the key plan is fitted onto the
/// floor plans (scale and position, searched) and the cut line is taken into the model, so a
/// section view can be made along it.
/// </summary>
public static class SectionReader
{
    static readonly Regex Title = new(@"^\s*section[\s\-_]*([A-Z0-9]{1,3})\s*$", RegexOptions.IgnoreCase);

    // "SECTIONS A-A & B-B", "Section 1-1"
    static readonly Regex Pairs = new(@"\b([A-Z0-9])\s*-\s*\1\b", RegexOptions.IgnoreCase);

    static IEnumerable<(string Name, Vec2 P)> Titles(SurveyDrawing d)
    {
        foreach (var t in d.Texts)
        {
            var v = t.Value ?? "";
            if (Title.Match(v) is { Success: true } m) yield return (m.Groups[1].Value.ToUpperInvariant(), t.Position);
            else if (Regex.IsMatch(v, @"section", RegexOptions.IgnoreCase))
                foreach (Match p in Pairs.Matches(v)) yield return (p.Groups[1].Value.ToUpperInvariant(), t.Position);
        }
    }

    public static bool IsSectionSheet(SurveyDrawing d) => Titles(d).Any();

    /// <param name="planWalls">wall lines of all floor plans, survey coordinates (mm)</param>
    public static List<SurveySection> Read(SurveyDrawing d, LayerRules rules, SurveyFrame frame, IReadOnlyList<Segment2> planWalls)
    {
        var titles = Titles(d).GroupBy(x => x.Name).Select(g => g.First()).ToList();
        var result = new List<SurveySection>();
        if (titles.Count == 0) return result;
        // spot heights to the nearest title, left to right (sections sit side by side on a sheet)
        var spots = d.Texts.Where(t => rules.Classify(t.Layer) == LayerRole.LevelText)
            .Select(t => (t.Position, Mm: LevelEstimator.ParseMm(t.Value))).Where(x => x.Mm != null).ToList();
        var cuts = KeyPlanCuts(d, frame, planWalls, titles.Select(t => t.Name).ToHashSet(), out string keyNote);
        foreach (var (name, p) in titles)
        {
            var heights = spots.Where(s => titles.OrderBy(o => Math.Abs(o.P.X - s.Position.X)).First().Name == name)
                .Select(s => s.Mm!.Value).OrderBy(x => x).ToList();
            cuts.TryGetValue(name, out var cut);
            result.Add(new SurveySection(name, cut, heights, d.Name.FileName,
                $"Section {name} ({d.Name.FileName}): {heights.Count} heights" + (cut != null ? ", cut line placed from the key plan" : ", cut line not placed" + (keyNote.Length > 0 ? $" ({keyNote})" : ""))));
        }
        return result;
    }

    /// <summary>
    /// Cut lines from the key plan: a line with the same single letter at both ends. The key plan
    /// (lines around those letters) is fitted to the plans' walls by trying scales and positions and
    /// keeping the one that puts most key-plan points on a wall.
    /// </summary>
    static Dictionary<string, Segment2> KeyPlanCuts(SurveyDrawing d, SurveyFrame frame, IReadOnlyList<Segment2> planWalls, HashSet<string> names, out string note)
    {
        note = "";
        var cuts = new Dictionary<string, Segment2>();
        var doc = DwgReader.Read(d.Path, (_, _) => { });
        double u = d.MmPerUnit;
        var lines = new List<Segment2>();
        foreach (var e in doc.Entities)
        {
            if (e is Line l) lines.Add(new Segment2(new Vec2(l.StartPoint.X, l.StartPoint.Y) * u, new Vec2(l.EndPoint.X, l.EndPoint.Y) * u, e.Layer?.Name ?? ""));
            else if (e is LwPolyline pl)
                for (int i = 0; i + 1 < pl.Vertices.Count; i++)
                    lines.Add(new Segment2(new Vec2(pl.Vertices[i].Location.X, pl.Vertices[i].Location.Y) * u, new Vec2(pl.Vertices[i + 1].Location.X, pl.Vertices[i + 1].Location.Y) * u, e.Layer?.Name ?? ""));
        }
        // letters marking cut ends, and the line joining each pair
        var letters = d.Texts.Where(t => Regex.IsMatch(t.Value ?? "", @"^\s*[A-Z0-9]\s*$")).GroupBy(t => t.Value.Trim().ToUpperInvariant()).ToList();
        var sheetCuts = new List<(string Name, Segment2 Line)>();
        foreach (var g in letters)
        {
            var marks = g.ToList();
            for (int i = 0; i < marks.Count; i++)
                for (int j = i + 1; j < marks.Count; j++)
                {
                    var a = marks[i].Position; var b = marks[j].Position;
                    var line = lines.Where(s => s.Length > 0.5 * a.DistanceTo(b))
                        .Select(s => (s, e: Math.Min(s.A.DistanceTo(a) + s.B.DistanceTo(b), s.A.DistanceTo(b) + s.B.DistanceTo(a))))
                        .Where(x => x.e <= 0.25 * a.DistanceTo(b)).OrderBy(x => x.e).Select(x => (Segment2?)x.s).FirstOrDefault();
                    if (line != null) sheetCuts.Add((g.Key, line));
                }
        }
        // a cut drawn in pieces (dashed): the two letters of a section named on the sheet are its ends
        foreach (var g in letters.Where(g => g.Count() == 2 && names.Contains(g.Key) && !sheetCuts.Any(c => c.Name == g.Key)))
            sheetCuts.Add((g.Key, new Segment2(g.First().Position, g.Last().Position, "", g.Key)));
        if (sheetCuts.Count == 0) { note = "no key plan with lettered cut lines"; return cuts; }
        // one key plan (a sheet can repeat it): the cuts close to the first
        var c0 = sheetCuts[0].Line;
        sheetCuts = sheetCuts.Where(c => c.Line.Mid.DistanceTo(c0.Mid) <= Math.Max(c0.Length, c.Line.Length) && c.Line.Length <= 4 * c0.Length && c0.Length <= 4 * c.Line.Length).ToList();
        // the key plan: lines near the cuts (the first key plan on the sheet)
        // the key plan: lines inside the box the cut lines span (they cross the whole building)
        var ends = sheetCuts.SelectMany(c => new[] { c.Line.A, c.Line.B }).ToList();
        double bx0 = ends.Min(p => p.X), bx1 = ends.Max(p => p.X), by0 = ends.Min(p => p.Y), by1 = ends.Max(p => p.Y);
        double pad = 0.1 * Math.Max(bx1 - bx0, by1 - by0);
        bool InBox(Vec2 p) => p.X >= bx0 - pad && p.X <= bx1 + pad && p.Y >= by0 - pad && p.Y <= by1 + pad;
        double stepK = Math.Max(bx1 - bx0, by1 - by0) / 300;
        var key = new List<Vec2>();
        foreach (var s in lines.Where(s => InBox(s.A) && InBox(s.B) && !sheetCuts.Any(c => c.Line == s)))
        {
            int n = Math.Max(1, (int)(s.Length / stepK));
            for (int k = 0; k <= n; k++) key.Add(s.A + (s.B - s.A) * ((double)k / n));
        }
        if (key.Count < 50 || planWalls.Count < 50) { note = "key plan too small to fit"; return cuts; }

        // plan walls as a 100 mm occupancy set
        var occ = new HashSet<(int, int)>();
        foreach (var s in planWalls)
        {
            int n = Math.Max(1, (int)(s.Length / 50));
            for (int k = 0; k <= n; k++) { var p = s.A + (s.B - s.A) * ((double)k / n); occ.Add(((int)Math.Floor(p.X / 100), (int)Math.Floor(p.Y / 100))); }
        }
        bool Near(Vec2 p) { int x = (int)Math.Floor(p.X / 100), y = (int)Math.Floor(p.Y / 100); for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++) if (occ.Contains((x + dx, y + dy))) return true; return false; }
        var wallPts = planWalls.SelectMany(s => new[] { s.A, s.B }).ToList();
        var wc = new Vec2(Median(wallPts.Select(p => p.X)), Median(wallPts.Select(p => p.Y)));
        var kc = new Vec2(Median(key.Select(p => p.X)), Median(key.Select(p => p.Y)));
        double spanW = Spread(wallPts), spanK = Spread(key);
        if (spanK <= 0 || spanW <= 0) { note = "key plan could not be fitted"; return cuts; }
        var sample = key.Where((_, i) => i % Math.Max(1, key.Count / 400) == 0).ToList();
        double Score(double s, Vec2 t) => sample.Count(p => Near(t + (p - kc) * s)) / (double)sample.Count;
        // coarse: scale around the spread ratio, position around the medians; then finer steps
        double bestS = spanW / spanK; var bestT = wc; double best = Score(bestS, bestT);
        foreach (var (ds, dt, steps) in new[] { (0.04, 4000.0, 6), (0.01, 1000.0, 5), (0.0025, 250.0, 4), (0.0006, 60.0, 4) })
        {
            double s0 = bestS; var t0 = bestT;
            for (int i = -steps; i <= steps; i++)
                for (int x = -steps; x <= steps; x++)
                    for (int y = -steps; y <= steps; y++)
                    {
                        double s = s0 * (1 + ds * i); var t = t0 + new Vec2(dt * x, dt * y);
                        double sc = Score(s, t);
                        if (sc > best) { best = sc; bestS = s; bestT = t; }
                    }
        }
        if (best < 0.6) { note = $"key plan fits the plans only {best:P0}"; return cuts; }
        foreach (var (name, line) in sheetCuts)
        {
            var cut = frame.ToModel(new Segment2(bestT + (line.A - kc) * bestS, bestT + (line.B - kc) * bestS, "section", name));
            // sections are cut square to the building: a cut that is not is a poor fit, not a section to build
            double off = cut.AngleDeg % 90;
            if (Math.Min(off, 90 - off) <= 10) cuts[name] = cut;
            else note = "cut line did not come out square to the building; key plan fit not trusted";
        }
        return cuts;
    }

    static double Median(IEnumerable<double> v) { var l = v.OrderBy(x => x).ToList(); return l[l.Count / 2]; }

    // the middle 90% extent along x plus along y
    static double Spread(List<Vec2> p)
    {
        var xs = p.Select(v => v.X).OrderBy(x => x).ToList(); var ys = p.Select(v => v.Y).OrderBy(y => y).ToList();
        int lo = (int)(0.05 * (p.Count - 1)), hi = (int)(0.95 * (p.Count - 1));
        return xs[hi] - xs[lo] + ys[hi] - ys[lo];
    }
}
