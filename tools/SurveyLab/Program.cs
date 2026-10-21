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

var model = SurveyAnalyzer.Analyze(inputs);
var sb = new StringBuilder();
sb.AppendLine(SurveyReport.Build(model));
File.WriteAllText(Path.Combine(outDir, "report.txt"), sb.ToString());
Console.WriteLine(sb);

foreach (var l in model.Levels)
    File.WriteAllText(Path.Combine(outDir, $"{l.Code}.svg"), Svg(l, model.Frame));

static string Svg(SurveyLevel l, SurveyFrame f)
{
    var raw = l.Plan.WallLines.Select(f.ToModel).ToList();
    var walls = l.Walls.Walls.Select(w => (w, c: f.ToModel(w.Centerline))).ToList();
    var all = raw.SelectMany(s => new[] { s.A, s.B }).ToList();
    double minX = all.Min(p => p.X) - 1000, maxX = all.Max(p => p.X) + 1000, minY = all.Min(p => p.Y) - 1000, maxY = all.Max(p => p.Y) + 1000;
    var sb = new StringBuilder();
    sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{minX:0} {-maxY:0} {maxX - minX:0} {maxY - minY:0}' width='1600' style='background:#fff'>");
    foreach (var s in raw) sb.Append($"<line x1='{s.A.X:0}' y1='{-s.A.Y:0}' x2='{s.B.X:0}' y2='{-s.B.Y:0}' stroke='#bbb' stroke-width='20'/>");
    var palette = new[] { "#d62728", "#1f77b4", "#2ca02c", "#9467bd", "#ff7f0e", "#8c564b", "#e377c2", "#17becf", "#bcbd22" };
    var types = walls.Select(x => x.w.TypeThickness).Distinct().OrderBy(t => t).ToList();
    foreach (var (w, c) in walls)
    {
        var col = palette[types.IndexOf(w.TypeThickness) % palette.Length];
        sb.Append($"<line x1='{c.A.X:0}' y1='{-c.A.Y:0}' x2='{c.B.X:0}' y2='{-c.B.Y:0}' stroke='{col}' stroke-opacity='0.55' stroke-width='{w.TypeThickness:0}'/>");
    }
    sb.Append("</svg>");
    return sb.ToString();
}
