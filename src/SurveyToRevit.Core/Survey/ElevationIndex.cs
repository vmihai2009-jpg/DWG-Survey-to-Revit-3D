using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Survey;

/// <summary>One part of an elevation drawing with its own height fit (a block of another height drawn on the same sheet).</summary>
public sealed record ElevationPart(double X0, double X1, double A, double B, double ResidualMm, IReadOnlyList<double[]> Used, IReadOnlyList<double[]> Rejected);

/// <summary>One elevation drawing found on a sheet, the facade it shows and how its sheet maps onto the model.</summary>
public sealed class ElevationDrawing
{
    public string File { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Its box on the sheet (sheet mm): x0, y0, x1, y1.</summary>
    public double[] Box { get; set; } = Array.Empty<double>();
    /// <summary>The facade it shows (F1.. along the ground floor's outline), or null when no facade matched.</summary>
    public string? Facade { get; set; }
    /// <summary>That facade's line, model mm: x1, y1, x2, y2.</summary>
    public double[]? FacadeLine { get; set; }
    /// <summary>Distance along the facade line (mm) = ShiftMm + (Mirrored ? -1 : 1) * Scale * sheet x.</summary>
    public bool Mirrored { get; set; }
    public double Scale { get; set; } = 1;
    public double ShiftMm { get; set; }
    /// <summary>Share of window columns that line up (0..1); 0.7 or more to be taken.</summary>
    public double Score { get; set; }
    public int DrawnColumns { get; set; }
    public int ModelColumns { get; set; }
    /// <summary>Window column centres drawn (sheet x, mm): what the matching used, for anyone checking it.</summary>
    [JsonIgnore] public List<double> Columns { get; set; } = new();
    public List<ElevationPart> Parts { get; set; } = new();
    public string Note { get; set; } = "";
    /// <summary>Its lines (sheet mm), for the window reading (item F).</summary>
    [JsonIgnore] public List<Segment2> Lines { get; set; } = new();
}

/// <summary>
/// Item E of MCP-AUDIT-AND-TOOLS.md: elevation sheets split into drawings, each drawing matched to the facade it shows and
/// fitted for height, part by part. A drawing is an island of linework (gaps of 2 m or more) with a title
/// (ELEVATION / ELEV. / FACADE) near it. Its facade is found by its pattern of window columns: the drawn column centres
/// against the model's along each facade of the ground floor's outline, both ways round, at 98% to 102% scale; 70% of the
/// columns lining up within 150 mm or it is left unmatched. Heights: z = a·y + b from its level marks, and a second fit for
/// a part of the drawing whose marks disagree with the first (a neighbouring block drawn on the same sheet).
/// Deterministic, any survey; written to elevation-index.json.
/// </summary>
public static class ElevationIndex
{
    public const double MinScore = 0.7, ColumnTolMm = 150, ColumnMergeMm = 100, FacadeReachMm = 800;
    static readonly Regex TitleWords = new(@"\b(ELEVATIONS?|ELEV\.|FA[CÇ]ADES?)\b", RegexOptions.IgnoreCase);
    static readonly Regex Spaced = new(@"^\s*(\S\s+){3,}\S\s*$");

    /// <summary>A title as words: letter-spaced titles ("N O R T H  E L E V A T I O N") closed up, word gaps (2+ spaces) kept.</summary>
    public static string Clean(string s) => Spaced.IsMatch(s) ? Regex.Replace(Regex.Replace(s.Trim(), @"\s{2,}", "|"), @"\s", "").Replace("|", " ") : s;

    static bool IsTitle(string s) { var c = Clean(s); return c.Length <= 60 && TitleWords.IsMatch(c); }

    public static bool IsElevationSheet(SurveyDrawing d) => d.Texts.Any(t => IsTitle(t.Value));

    static readonly Regex Datum = new(@"^\s*\+?(-?\d{1,3}\.\d{2,3})\s*m?\s*(A\.?\s?O\.?\s?D\.?|O\.?D\.?)\s*$", RegexOptions.IgnoreCase);

    /// <summary>A datum mark's height in mm ("20.00m A.O.D."), on any layer; null when the text is not one.</summary>
    public static double? DatumMm(string text) { var m = Datum.Match(text ?? ""); return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 1000 : null; }

    /// <summary>The drawings on one sheet (sheet mm), each with its title, lines, window columns and level marks.</summary>
    public static List<(ElevationDrawing Drawing, List<(double X, double Y, double Mm)> Marks)> Split(SurveyDrawing d, LayerRules rules, IReadOnlyList<Segment2> lines)
    {
        var titles = d.Texts.Where(t => IsTitle(t.Value)).ToList();
        var res = new List<(ElevationDrawing, List<(double, double, double)>)>();
        if (titles.Count == 0) return res;
        var drawn = lines.Where(l => rules.Classify(l.Layer) is not (LayerRole.Annotation or LayerRole.LevelText or LayerRole.HeightText or LayerRole.RoomName)).ToList();
        var islands = SectionReader.Islands(drawn).Where(g => g.Count >= 20).ToList();
        // drawings stacked closer than the island gap read as one island: with two or more titles at different heights
        // under it, cut it into bands, each title's band from it up to the next title above (titles sit under their drawing)
        islands = islands.SelectMany(g =>
        {
            double gx0 = g.Min(s => Math.Min(s.A.X, s.B.X)), gx1 = g.Max(s => Math.Max(s.A.X, s.B.X));
            double gy0 = g.Min(s => Math.Min(s.A.Y, s.B.Y)), gy1 = g.Max(s => Math.Max(s.A.Y, s.B.Y));
            var rows = titles.Where(t => t.Position.X >= gx0 - 3000 && t.Position.X <= gx1 + 3000 && t.Position.Y >= gy0 - 6000 && t.Position.Y <= gy1)
                             .Select(t => t.Position.Y).OrderBy(y => y).ToList();
            var cuts = new List<double>();
            foreach (var y in rows) if (cuts.Count == 0 || y - cuts[^1] > 5000) cuts.Add(y);
            if (cuts.Count < 2) return new[] { g };
            var bands = new List<List<Segment2>>();
            for (int k = 0; k < cuts.Count; k++)
            {
                double lo = cuts[k], hi = k + 1 < cuts.Count ? cuts[k + 1] : double.MaxValue;
                var band = g.Where(s => { double my = (s.A.Y + s.B.Y) / 2; return my >= lo && my < hi; }).ToList();
                if (band.Count >= 20) bands.Add(band);
            }
            return bands.Count >= 2 ? bands.ToArray() : new[] { g };
        }).ToList();
        var marks = d.Texts.Where(t => rules.Classify(t.Layer) == LayerRole.LevelText).Select(t => (t.Position, Mm: LevelEstimator.ParseMm(t.Value))).Where(x => x.Mm != null).ToList();
        // a datum mark ("20.00m A.O.D.") on any layer: its height is the flat line drawn next to it (just above or below the text);
        // it belongs to the drawing whose title it sits by (it is drawn beside the title, outside the drawing)
        var datums = new List<(Vec2 Text, Vec2 At, double Mm)>();
        foreach (var t in d.Texts)
        {
            if (DatumMm(t.Value) is not double z) continue;
            var line = lines.Where(l => Math.Abs(l.A.Y - l.B.Y) < 1 && Math.Abs(l.A.X - l.B.X) > 1000
                                        && Math.Min(l.A.X, l.B.X) <= t.Position.X + 500 && Math.Max(l.A.X, l.B.X) >= t.Position.X - 3000
                                        && Math.Abs(l.A.Y - t.Position.Y) <= 800)
                            .OrderBy(l => Math.Abs(l.A.Y - t.Position.Y)).FirstOrDefault();
            datums.Add((t.Position, new Vec2(t.Position.X, line?.A.Y ?? t.Position.Y), z));
        }
        var taken = new HashSet<SurveyText>();
        foreach (var g in islands.OrderByDescending(g => g.Count))
        {
            double x0 = g.Min(s => Math.Min(s.A.X, s.B.X)), x1 = g.Max(s => Math.Max(s.A.X, s.B.X));
            double y0 = g.Min(s => Math.Min(s.A.Y, s.B.Y)), y1 = g.Max(s => Math.Max(s.A.Y, s.B.Y));
            double Gap(Vec2 p) { double dx = p.X < x0 ? x0 - p.X : p.X > x1 ? p.X - x1 : 0, dy = p.Y < y0 ? y0 - p.Y : p.Y > y1 ? p.Y - y1 : 0; return Math.Sqrt(dx * dx + dy * dy); }
            // its title: the nearest one within a fifth of its size (titles sit under or over the drawing), not used by another
            double reach = Math.Max(3000, 0.2 * Math.Max(x1 - x0, y1 - y0));
            var title = titles.Where(t => !taken.Contains(t) && Gap(t.Position) <= reach).OrderBy(t => Gap(t.Position)).FirstOrDefault();
            if (title == null) continue;
            taken.Add(title);
            var e = new ElevationDrawing { File = d.Name.FileName, Title = title.Value.Trim(), Box = new[] { Math.Round(x0), Math.Round(y0), Math.Round(x1), Math.Round(y1) }, Lines = g };
            e.Columns = Columns(g.Where(s => rules.Classify(s.Layer) == LayerRole.Window).Select(s => (Math.Min(s.A.X, s.B.X), Math.Max(s.A.X, s.B.X))));
            var mine = marks.Where(m => m.Position.X >= x0 - 3000 && m.Position.X <= x1 + 3000 && m.Position.Y >= y0 - 3000 && m.Position.Y <= y1 + 3000)
                .Select(m => (m.Position.X, m.Position.Y, m.Mm!.Value)).ToList();
            foreach (var dm in datums)
                if (titles.OrderBy(tt => tt.Position.DistanceTo(dm.Text)).First() == title && dm.Text.DistanceTo(title.Position) <= 12000)
                    mine.Add((dm.At.X, dm.At.Y, dm.Mm));
            res.Add((e, mine));
        }
        return res;
    }

    /// <summary>Window columns: x extents of window lines merged where they overlap or nearly touch; centres of the merged runs.</summary>
    public static List<double> Columns(IEnumerable<(double X0, double X1)> spans, double mergeMm = 50)
    {
        var res = new List<double>(); double a = double.NaN, b = double.NaN;
        foreach (var (s0, s1) in spans.OrderBy(s => s.X0))
        {
            if (double.IsNaN(a)) { a = s0; b = s1; continue; }
            if (s0 <= b + mergeMm) b = Math.Max(b, s1);
            else { if (b - a >= 300) res.Add((a + b) / 2); a = s0; b = s1; }
        }
        if (!double.IsNaN(a) && b - a >= 300) res.Add((a + b) / 2);
        return res;
    }

    /// <summary>Model window columns along a facade line (distance from its start), from every floor's windows within reach of the line.</summary>
    public static List<double> ModelColumns(Segment2 facade, IEnumerable<WindowCandidate> windows)
    {
        var d = facade.Direction;
        var ts = windows.Where(w => Math.Abs(SegmentCleaner.Offset(facade, w.Center)) <= FacadeReachMm)
            .Select(w => (w.Center - facade.A) is var v ? v.X * d.X + v.Y * d.Y : 0).Where(t => t >= -200 && t <= facade.Length + 200).OrderBy(t => t).ToList();
        var res = new List<double>();
        foreach (var t in ts) if (res.Count == 0 || t - res[^1] > ColumnMergeMm) res.Add(t); else res[^1] = (res[^1] + t) / 2;
        return res;
    }

    /// <summary>Best way the drawn columns land on the model's: share matched (of the larger count), mirrored, scale, shift.</summary>
    public static (double Score, bool Mirrored, double Scale, double Shift) Match(IReadOnlyList<double> drawn, IReadOnlyList<double> model)
    {
        (double, bool, double, double) best = (0, false, 1, 0);
        if (drawn.Count < 2 || model.Count < 2) return best;
        int denom = Math.Max(drawn.Count, model.Count);
        foreach (var mirrored in new[] { false, true })
            for (double scale = 0.98; scale <= 1.02001; scale += 0.005)
            {
                int sg = mirrored ? -1 : 1;
                foreach (var t in model)
                    foreach (var x in drawn)
                    {
                        double shift = t - sg * scale * x;
                        int n = drawn.Count(xx => model.Any(tt => Math.Abs(shift + sg * scale * xx - tt) <= ColumnTolMm));
                        double sc = (double)Math.Min(n, model.Count) / denom;
                        // a drawing wider than the facade (the street, the buildings next door drawn too): the share of the
                        // facade's own columns found, when it has enough of them to be sure
                        if (drawn.Count > model.Count && model.Count >= 4)
                        {
                            int hit = model.Count(tt => drawn.Any(xx => Math.Abs(shift + sg * scale * xx - tt) <= ColumnTolMm));
                            sc = Math.Max(sc, 0.95 * hit / model.Count);
                        }
                        if (sc > best.Item1 + 1e-9) best = (sc, mirrored, Math.Round(scale, 3), Math.Round(shift));
                    }
            }
        return best;
    }

    /// <summary>One height fit, or two when the marks of one part of the drawing disagree with the rest (split at the widest gap in x).</summary>
    public static List<ElevationPart> FitParts(IReadOnlyList<(double X, double Y, double Mm)> marks, double x0, double x1)
    {
        var one = SectionProfiles.Fit(marks.Select(m => (m.Y, m.Mm)).ToList());
        var parts = new List<ElevationPart>();
        // one mark only (a datum line): survey elevations are drawn 1:1, so the offset alone
        if (one == null && marks.Count >= 1 && marks.Select(m => Math.Round(m.Mm - m.Y)).Distinct().Count() == 1)
            one = new HeightFit(1, marks[0].Mm - marks[0].Y, 0, marks.Select(m => new[] { m.Y, m.Mm }).ToList(), Array.Empty<double[]>());
        if (one == null) return parts;
        parts.Add(new ElevationPart(Math.Round(x0), Math.Round(x1), one.A, one.B, one.ResidualMm, one.Used, one.Rejected));
        if (one.Rejected.Count < 2 || marks.Count < 4) return parts;
        var xs = marks.Select(m => m.X).OrderBy(x => x).ToList();
        int gi = Enumerable.Range(0, xs.Count - 1).OrderByDescending(i => xs[i + 1] - xs[i]).First();
        double cut = (xs[gi] + xs[gi + 1]) / 2;
        var left = marks.Where(m => m.X < cut).ToList(); var right = marks.Where(m => m.X >= cut).ToList();
        var fl = SectionProfiles.Fit(left.Select(m => (m.Y, m.Mm)).ToList()); var fr = SectionProfiles.Fit(right.Select(m => (m.Y, m.Mm)).ToList());
        if (fl == null || fr == null || fl.Rejected.Count + fr.Rejected.Count >= one.Rejected.Count) return parts;
        return new List<ElevationPart>
        {
            new(Math.Round(x0), Math.Round(cut), fl.A, fl.B, fl.ResidualMm, fl.Used, fl.Rejected),
            new(Math.Round(cut), Math.Round(x1), fr.A, fr.B, fr.ResidualMm, fr.Used, fr.Rejected),
        };
    }

    /// <summary>The facades: edges of the lowest floor outline 2 m or longer, F1.. in outline order.</summary>
    public static List<(string Id, Segment2 Line)> Facades(IReadOnlyList<SurveyLevel> levels)
    {
        var floors = levels.Where(l => l.Outline != null && !double.IsNaN(l.ElevationMm)).OrderBy(l => l.ElevationMm).ToList();
        if (floors.Count == 0) return new();
        // the ground floor's edges first (ids as before), then the upper floors' edges that no lower edge already gives
        // (a rear wing set back above a ground floor that runs further out has its own facade and its own elevation)
        var lines = FacadeTops.Stretches(floors[0].Outline!).ToList();
        foreach (var f in floors.Skip(1))
            foreach (var s in FacadeTops.Stretches(f.Outline!))
                if (!lines.Any(o => SegmentCleaner.AngleDiffDeg(o.AngleDeg, s.AngleDeg) <= 3 && Math.Abs(SegmentCleaner.Offset(o, s.Mid)) <= 400
                                    && Math.Abs((s.Mid - o.Mid).Dot(o.Direction)) <= (o.Length + s.Length) / 2 - 0.5 * Math.Min(o.Length, s.Length)))
                    lines.Add(s);
        return lines.Select((s, i) => ($"F{i + 1}", s)).ToList();
    }

    /// <summary>Everything: drawings from the sheets, each matched to at most one facade and each facade to at most one drawing, best first.</summary>
    public static List<ElevationDrawing> Build(IReadOnlyList<(ElevationDrawing Drawing, List<(double X, double Y, double Mm)> Marks)> found, IReadOnlyList<SurveyLevel> levels)
    {
        var facades = Facades(levels);
        var windows = levels.SelectMany(l => l.Windows).ToList();
        var cols = facades.Select(f => (f.Id, f.Line, Cols: ModelColumns(f.Line, windows))).ToList();
        var pairs = new List<(ElevationDrawing D, string Id, Segment2 Line, int ModelCols, (double Score, bool Mirrored, double Scale, double Shift) M)>();
        foreach (var (d, marks) in found)
        {
            d.Parts = FitParts(marks, d.Box[0], d.Box[2]);
            d.DrawnColumns = d.Columns.Count;
            foreach (var f in cols) pairs.Add((d, f.Id, f.Line, f.Cols.Count, Match(d.Columns, f.Cols)));
        }
        var usedD = new HashSet<ElevationDrawing>(); var usedF = new HashSet<string>();
        foreach (var p in pairs.OrderByDescending(p => p.M.Score))
        {
            if (p.M.Score < MinScore || usedD.Contains(p.D) || usedF.Contains(p.Id)) continue;
            usedD.Add(p.D); usedF.Add(p.Id);
            var l = p.Line;
            p.D.Facade = p.Id; p.D.FacadeLine = new[] { Math.Round(l.A.X), Math.Round(l.A.Y), Math.Round(l.B.X), Math.Round(l.B.Y) };
            p.D.Mirrored = p.M.Mirrored; p.D.Scale = p.M.Scale; p.D.ShiftMm = p.M.Shift; p.D.Score = Math.Round(p.M.Score, 2); p.D.ModelColumns = p.ModelCols;
        }
        foreach (var (d, _) in found)
        {
            var bestTry = pairs.Where(p => p.D == d).OrderByDescending(p => p.M.Score).FirstOrDefault();
            if (d.Facade == null) d.Score = Math.Round(bestTry.M.Score, 2);
            d.Note = d.Facade != null ? $"{d.Title}: facade {d.Facade}, {d.Score:P0} of window columns line up" + (d.Mirrored ? ", drawn the other way round" : "")
                : d.Columns.Count < 2 ? $"{d.Title}: fewer than 2 window columns drawn on a window layer: facade not found (a decision with the facade line settles it)"
                : $"{d.Title}: best facade {bestTry.Id} lines up only {bestTry.M.Score:P0} (under {MinScore:P0}): left unmatched";
            if (d.Parts.Count == 0) d.Note += "; no height fit (fewer than 2 level marks agree)";
            else if (d.Parts.Count > 1) d.Note += $"; two height fits (a part at x {d.Parts[1].X0:0} on has its own)";
        }
        return found.Select(f => f.Drawing).ToList();
    }

    static readonly JsonSerializerOptions Json = Freeze(new JsonSerializerOptions
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    });
    static JsonSerializerOptions Freeze(JsonSerializerOptions o) { o.MakeReadOnly(); return o; }

    public static string ToJson(IReadOnlyList<ElevationDrawing> drawings) => JsonSerializer.Serialize(new { version = 1, elevations = drawings }, Json);

    public static List<string> Save(IReadOnlyList<ElevationDrawing> drawings, string reportsRoot, string projectTitle, string? surveyFolder) =>
        SharedFiles.Write("elevation-index.json", ToJson(drawings), reportsRoot, projectTitle, surveyFolder);
}
