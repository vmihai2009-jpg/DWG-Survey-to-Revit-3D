using System.Text;

namespace SurveyToRevit.Core;

/// <summary>Plain-text summary of what was read from a drawing and what was recognised.</summary>
public static class Report
{
    public static string Build(string drawingName, IReadOnlyList<Segment2> segments, int arcs, WallDetectionResult walls, IEnumerable<string>? notes = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Survey analysis: {drawingName}");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("All dimensions in millimetres, plan coordinates in the Revit project's internal origin.");
        sb.AppendLine();

        sb.AppendLine("LINEWORK READ");
        sb.AppendLine($"  Straight segments: {segments.Count}   Arcs (not yet used): {arcs}");
        if (segments.Count > 0)
        {
            double minX = segments.Min(s => Math.Min(s.A.X, s.B.X)), maxX = segments.Max(s => Math.Max(s.A.X, s.B.X));
            double minY = segments.Min(s => Math.Min(s.A.Y, s.B.Y)), maxY = segments.Max(s => Math.Max(s.A.Y, s.B.Y));
            sb.AppendLine($"  Extents: X {minX:0} to {maxX:0}, Y {minY:0} to {maxY:0}  (size {(maxX - minX) / 1000:0.00} x {(maxY - minY) / 1000:0.00} m)");
        }
        foreach (var layer in segments.GroupBy(s => s.Layer).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  Layer '{layer.Key}': {layer.Count()} segments");
        sb.AppendLine();

        sb.AppendLine("WALLS RECOGNISED (preview only, nothing created in the model)");
        sb.AppendLine($"  Lines after cleaning: {walls.CleanedSegments} (from {walls.InputSegments})");
        sb.AppendLine($"  Wall candidates: {walls.Walls.Count}, total length {walls.Walls.Sum(w => w.Length) / 1000:0.00} m");
        foreach (var g in walls.Walls.GroupBy(w => w.TypeName).OrderBy(g => g.Key))
            sb.AppendLine($"  {g.Key}: {g.Count()} walls, {g.Sum(w => w.Length) / 1000:0.00} m  " +
                          $"(confidence high {g.Count(w => w.Confidence == Confidence.High)}, medium {g.Count(w => w.Confidence == Confidence.Medium)}, low {g.Count(w => w.Confidence == Confidence.Low)})");
        sb.AppendLine($"  Lines not explained by any wall: {walls.UnpairedSegments.Count} (shown for review, not converted)");
        sb.AppendLine();

        var noteList = notes?.ToList() ?? new List<string>();
        if (noteList.Count > 0)
        {
            sb.AppendLine("NOTES AND ASSUMPTIONS");
            foreach (var n in noteList) sb.AppendLine($"  - {n}");
            sb.AppendLine();
        }

        sb.AppendLine("WALL LIST");
        int i = 1;
        foreach (var w in walls.Walls)
            sb.AppendLine($"  {i++,3}. {w.TypeName,-14} {w.Centerline.A} -> {w.Centerline.B}  length {w.Length:0}  measured {w.MeasuredThickness:0.#}  {w.Confidence}");
        return sb.ToString();
    }
}
