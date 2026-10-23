using System.Globalization;
using System.Text;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;

// Usage: SurveyLab <folder-or-dwg>... [--out <dir>]
var outDir = "surveylab-out";
var inputs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--out") outDir = args[++i];
    else if (Directory.Exists(args[i])) inputs.AddRange(Directory.GetFiles(args[i], "*.dwg"));
    else inputs.Add(args[i]);
}
Directory.CreateDirectory(outDir);
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

// Classified grid per floor as a PPM image (y flipped so north is up):
// white unknown, grey outside, pale blue room, black line, red wall material.
SurveyAnalyzer.PlanDebug = (code, w, h, cls) =>
{
    var pal = new byte[][] { new byte[] { 255, 255, 255 }, new byte[] { 200, 200, 200 }, new byte[] { 225, 235, 250 }, new byte[] { 0, 0, 0 }, new byte[] { 220, 40, 40 } };
    using var fs = File.Create(Path.Combine(outDir, $"{code}-grid.ppm"));
    var head = Encoding.ASCII.GetBytes($"P6 {w} {h} 255\n");
    fs.Write(head);
    for (int y = h - 1; y >= 0; y--)
        for (int x = 0; x < w; x++) fs.Write(pal[cls[y * w + x]]);
};
var model = SurveyAnalyzer.Analyze(inputs);
CheckGeometry(model);
if (Environment.GetEnvironmentVariable("ARCS") is string arcLevel) foreach (var l in model.Levels.Where(l => l.Code == arcLevel)) ArcDebug.Dump(l, model.Frame);
if (Environment.GetEnvironmentVariable("TEXTS") != null)
    foreach (var l in model.Levels.Take(1))
        foreach (var g in l.Plan.Texts.GroupBy(t => t.Layer))
            Console.Error.WriteLine($"{g.Key}: {g.Count()} e.g. {string.Join(" | ", g.Select(t => t.Value.Replace("\n", "/")).Distinct().Take(12))}");
var sb = new StringBuilder();
sb.AppendLine(SurveyReport.Build(model));
File.WriteAllText(Path.Combine(outDir, "report.txt"), sb.ToString());
Console.WriteLine(sb);

foreach (var l in model.Levels)
    File.WriteAllText(Path.Combine(outDir, $"{l.Code}.svg"), Svg(l, model.Frame));

foreach (var l in model.Levels)
    File.WriteAllText(Path.Combine(outDir, $"{l.Code}-loose.svg"), LooseEnds(l, model.Frame, Environment.GetEnvironmentVariable("BOX")?.Split(",").Select(double.Parse).ToArray()));

static string Svg(SurveyLevel l, SurveyFrame f)
{
    var raw = l.Plan.WallLines.Select(f.ToModel).ToList();
    var walls = l.Walls.Walls.Select(w => (w, c: w.Centerline)).ToList();
    var all = raw.SelectMany(s => new[] { s.A, s.B }).ToList();
    double minX = all.Min(p => p.X) - 1000, maxX = all.Max(p => p.X) + 1000, minY = all.Min(p => p.Y) - 1000, maxY = all.Max(p => p.Y) + 1000;
    var sb = new StringBuilder();
    sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{minX:0} {-maxY:0} {maxX - minX:0} {maxY - minY:0}' width='{Environment.GetEnvironmentVariable("SVGW") ?? "1600"}' style='background:#fff'>");
    foreach (var s in raw) sb.Append($"<line x1='{s.A.X:0}' y1='{-s.A.Y:0}' x2='{s.B.X:0}' y2='{-s.B.Y:0}' stroke='#bbb' stroke-width='20'/>");
    // wall-layer lines drawn dashed/hidden or thin: not walls
    foreach (var s in l.Plan.HiddenLines.Select(f.ToModel)) sb.Append($"<line x1='{s.A.X:0}' y1='{-s.A.Y:0}' x2='{s.B.X:0}' y2='{-s.B.Y:0}' stroke='#a0f' stroke-width='40' stroke-dasharray='200 120'/>");
    var palette = new[] { "#d62728", "#1f77b4", "#2ca02c", "#9467bd", "#ff7f0e", "#8c564b", "#e377c2", "#17becf", "#bcbd22" };
    var types = walls.Select(x => x.w.TypeThickness).Distinct().OrderBy(t => t).ToList();
    foreach (var (w, c) in walls)
    {
        var col = palette[types.IndexOf(w.TypeThickness) % palette.Length];
        if (w.Arc is Arc2 arc)
            sb.Append($"<polyline points='{string.Join(" ", arc.Tessellate(200).Select(s => $"{s.A.X:0},{-s.A.Y:0}").Append($"{c.B.X:0},{-c.B.Y:0}"))}' fill='none' stroke='{col}' stroke-opacity='0.55' stroke-width='{w.TypeThickness:0}'/>");
        else
            sb.Append($"<line x1='{c.A.X:0}' y1='{-c.A.Y:0}' x2='{c.B.X:0}' y2='{-c.B.Y:0}' stroke='{col}' stroke-opacity='0.55' stroke-width='{w.TypeThickness:0}'/>");
    }
    foreach (var arc in l.Plan.WallArcs.Select(f.ToModel))
        sb.Append($"<polyline points='{string.Join(" ", arc.Tessellate(200).Select(s => $"{s.A.X:0},{-s.A.Y:0}"))}' fill='none' stroke='#888' stroke-width='20'/>");
    if (l.Outline != null)
        sb.Append($"<polygon points='{string.Join(" ", l.Outline.Vertices.Select(v => $"{v.X:0},{-v.Y:0}"))}' fill='#4a90d9' fill-opacity='0.08' stroke='#4a90d9' stroke-width='40'/>");
    foreach (var w in l.Windows)
    {
        var hw = l.Walls.Walls[w.HostIndex];
        var al = WallGeometry.Along(hw, w.Center);
        var a = WallGeometry.PointAt(hw, al - w.WidthMm / 2);
        var b = WallGeometry.PointAt(hw, al + w.WidthMm / 2);
        sb.Append($"<line x1='{a.X:0}' y1='{-a.Y:0}' x2='{b.X:0}' y2='{-b.Y:0}' stroke='#00a0ff' stroke-width='250'/>");
    }
    foreach (var d in l.Doors)
    {
        var hw = l.Walls.Walls[d.HostIndex];
        var al = WallGeometry.Along(hw, d.Center);
        var a = WallGeometry.PointAt(hw, al - d.WidthMm / 2);
        var b = WallGeometry.PointAt(hw, al + d.WidthMm / 2);
        sb.Append($"<line x1='{a.X:0}' y1='{-a.Y:0}' x2='{b.X:0}' y2='{-b.Y:0}' stroke='#e0007a' stroke-width='250'/>");
    }
    foreach (var d in l.Details)
        sb.Append($"<polygon points='{string.Join(" ", d.Outline.Vertices.Select(v => $"{v.X:0},{-v.Y:0}"))}' fill='{(d.Role == SurveyToRevit.Core.Survey.LayerRole.Wall ? "#000" : "#7a3db8")}' fill-opacity='0.75'/>");
    foreach (var (p, name) in l.RoomNames)
        sb.Append($"<text x='{p.X:0}' y='{-p.Y:0}' font-size='400' fill='#333'>{System.Security.SecurityElement.Escape(name)}</text>");
    foreach (var u in l.Unclear)
        sb.Append($"<polygon points='{string.Join(" ", u.Outline.Vertices.Select(v => $"{v.X:0},{-v.Y:0}"))}' fill='#f5a623' fill-opacity='0.6'/>");
    sb.Append("</svg>");
    return sb.ToString();
}

// Linework by layer, with loose ends (no other line within 25 mm) circled: shows why spaces don't close.
static string LooseEnds(SurveyLevel l, SurveyFrame f, double[]? box)
{
    var segs = l.Plan.WallLines.Concat(l.Plan.OpeningLines).Select(f.ToModel).ToList();
    if (box != null) segs = segs.Where(s => new[] { s.A, s.B }.Any(p => p.X >= box[0] && p.X <= box[2] && p.Y >= box[1] && p.Y <= box[3])).ToList();
    var all = segs.SelectMany(s => new[] { s.A, s.B }).ToList();
    double minX = box?[0] ?? all.Min(p => p.X), maxX = box?[2] ?? all.Max(p => p.X), minY = box?[1] ?? all.Min(p => p.Y), maxY = box?[3] ?? all.Max(p => p.Y);
    var sb = new StringBuilder($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{minX:0} {-maxY:0} {maxX - minX:0} {maxY - minY:0}' width='{Environment.GetEnvironmentVariable("SVGW") ?? "1600"}' style='background:#fff'>");
    var layers = segs.Select(s => s.Layer).Distinct().ToList();
    var pal = new[] { "#c06000", "#0050c0", "#00a050", "#a000a0", "#808000", "#00a0a0", "#e00060", "#606060" };
    foreach (var s in segs)
        sb.Append($"<line x1='{s.A.X:0}' y1='{-s.A.Y:0}' x2='{s.B.X:0}' y2='{-s.B.Y:0}' stroke='{pal[layers.IndexOf(s.Layer) % pal.Length]}' stroke-width='15'/>");
    foreach (var arc in l.Plan.WallArcs.Select(f.ToModel))
        sb.Append($"<polyline points='{string.Join(" ", arc.Tessellate(100).Select(s => $"{s.A.X:0},{-s.A.Y:0}"))}' fill='none' stroke='#ff0000' stroke-width='25'/>");
    static double DistToSeg(Vec2 p, Segment2 s)
    {
        var d = s.B - s.A; double len2 = d.X * d.X + d.Y * d.Y;
        double t = len2 == 0 ? 0 : Math.Clamp(((p.X - s.A.X) * d.X + (p.Y - s.A.Y) * d.Y) / len2, 0, 1);
        var q = s.A + d * t; return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
    }
    int loose = 0;
    for (int i = 0; i < segs.Count; i++)
        foreach (var p in new[] { segs[i].A, segs[i].B })
            if (!segs.Where((_, j) => j != i).Any(o => DistToSeg(p, o) < 25)) { loose++; sb.Append($"<circle cx='{p.X:0}' cy='{-p.Y:0}' r='60' fill='none' stroke='red' stroke-width='15'/>"); }
    for (int k = 0; k < layers.Count; k++)
        sb.Append($"<text x='{minX + 200:0}' y='{-maxY + 400 + k * 400:0}' font-size='300' fill='{pal[k % pal.Length]}'>{layers[k]}</text>");
    Console.Error.WriteLine($"{l.Code}: {segs.Count} lines, {loose} loose ends");
    return sb.Append("</svg>").ToString();
}

static void CheckGeometry(SurveyModel model)
{
    static bool Bad(Vec2 p) => !double.IsFinite(p.X) || !double.IsFinite(p.Y);
    foreach (var l in model.Levels)
    {
        var walls = l.Walls.Walls;
        int nonFinite = walls.Count(w => Bad(w.Centerline.A) || Bad(w.Centerline.B));
        int tiny = walls.Count(w => w.Centerline.Length < 10);
        double far = walls.Where(w => !Bad(w.Centerline.A) && !Bad(w.Centerline.B)).Select(w => Math.Max(w.Centerline.A.Length, w.Centerline.B.Length)).DefaultIfEmpty(0).Max();
        int badPoly = l.Unclear.Count(u => u.Outline.Vertices.Any(Bad) || u.Outline.Vertices.Count < 3) + (l.Outline?.Vertices.Any(Bad) == true ? 1 : 0);
        if (l.Outline != null && SelfIntersects(l.Outline)) Console.Error.WriteLine($"{l.Code}: OUTLINE SELF-INTERSECTS");
        Console.Error.WriteLine($"{l.Code}: non-finite walls {nonFinite}, under 10 mm {tiny}, farthest point {far / 1000:0} m, bad polygons {badPoly}");
    }
}

static bool SelfIntersects(Polygon2 p)
{
    var v = p.Vertices; int n = v.Count;
    for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            if (j == i + 1 || (i == 0 && j == n - 1)) continue;
            var s1 = new Segment2(v[i], v[(i + 1) % n]); var s2 = new Segment2(v[j], v[(j + 1) % n]);
            double d1 = s1.Direction.Cross(s2.A - s1.A), d2 = s1.Direction.Cross(s2.B - s1.A);
            double d3 = s2.Direction.Cross(s1.A - s2.A), d4 = s2.Direction.Cross(s1.B - s2.A);
            if (d1 * d2 < 0 && d3 * d4 < 0 && s1.Length > 0 && s2.Length > 0) return true;
        }
    return false;
}

static class ArcDebug
{
    public static void Dump(SurveyLevel l, SurveyFrame f)
    {
        var arcs = l.Plan.WallArcs.Select(f.ToModel).ToList();
        Console.Error.WriteLine($"{l.Name}: {arcs.Count} arcs, {l.Walls.Walls.Count(w => w.Arc != null)} curved walls");
        foreach (var a in arcs.OrderBy(a => a.Radius)) Console.Error.WriteLine($"   r={a.Radius:0} c=({a.Center.X:0},{a.Center.Y:0}) start={a.Start:0.000} sweep={a.Sweep:0.000} len={a.Length:0} {a.Layer}");
    }
}
