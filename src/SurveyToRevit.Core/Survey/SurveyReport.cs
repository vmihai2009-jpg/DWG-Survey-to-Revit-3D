using System.Text;

namespace SurveyToRevit.Core.Survey;

/// <summary>Plain-text report of a survey analysis: levels, heights, walls, assumptions, skipped files.</summary>
public static class SurveyReport
{
    public static string Build(SurveyModel m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SURVEY ANALYSIS");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Sizes in mm, heights in m as surveyed.");
        sb.AppendLine();
        sb.AppendLine("COORDINATES");
        sb.AppendLine($"  Base point: E {m.Frame.BasePoint.X / 1000:0.000}  N {m.Frame.BasePoint.Y / 1000:0.000}");
        sb.AppendLine($"  Project north turned {m.Frame.RotationDeg:0.00}° from grid north");
        sb.AppendLine();

        sb.AppendLine("LEVELS");
        foreach (var l in m.Levels)
        {
            var h = double.IsNaN(l.ElevationMm) ? "  ?   " : $"{l.ElevationMm / 1000,6:0.00}";
            var est = l.Estimate == null ? "no spot heights" : $"{l.Estimate.Supporting} of {l.Estimate.Total} spot heights agree";
            var others = l.Estimate?.OtherLevels.Count > 0 ? "; also " + string.Join(", ", l.Estimate.OtherLevels.Select(o => $"{o.LevelMm / 1000:0.00} ({o.Count})")) : "";
            sb.AppendLine($"  {l.Name,-26} {h} m   wall height {l.WallHeightMm:0} mm, ceiling {l.CeilingHeightMm:0} mm{(l.CeilingFromSurvey ? " (surveyed)" : "")}   [{est}{others}]");
            sb.AppendLine($"  {"",-26} from {l.Plan.Name.FileName}, units {l.Plan.UnitsNote}");
        }
        sb.AppendLine();

        sb.AppendLine("WALLS");
        foreach (var l in m.Levels)
        {
            var w = l.Walls;
            sb.AppendLine($"  {l.Name}: {w.Walls.Count} walls, {w.Walls.Sum(x => x.Length) / 1000:0.0} m total, " +
                          $"{w.ThicknessGroups.Count} wall types; floor outline {(l.Outline == null ? "not found" : $"{l.Outline.Area / 1e6:0} m²")}; {l.Unclear.Count} unclear areas (generic models); {l.Windows.Count} windows ({l.Windows.Count(x => x.HeightsFromSurvey)} with surveyed sill and head); {l.Doors.Count} doors ({l.Doors.Count(x => x.HeightFromSurvey)} with surveyed head); {l.RoomNames.Count} room names; {l.GapFills.Count} gap fills between walls; {l.Walls.Walls.Count(x => x.Curtain)} curtain walls; {l.Stairs.Count} stairs ({l.Stairs.Sum(x => x.Risers)} risers); detail blocks: {(l.Details.Count == 0 ? "none" : string.Join(", ", l.Details.GroupBy(d => d.Role).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}")))}");
            foreach (var g in w.Walls.GroupBy(x => x.TypeName).OrderBy(g => g.First().TypeThickness))
                sb.AppendLine($"      {g.Key,-22} {g.Count(),4} walls  {g.Sum(x => x.Length) / 1000,7:0.0} m");
        }
        if (m.Roofs.Count == 0) sb.AppendLine("  Roofs: none (no roof plan or roof layer with spot heights).");
        else
        {
            sb.AppendLine($"  Roofs: {m.Roofs.Count} flat concept roofs, {m.Roofs.Sum(r => r.Outline.Area) / 1e6:0} m² in all; the largest:");
            foreach (var r in m.Roofs.OrderByDescending(r => r.Outline.Area).Take(5))
                sb.AppendLine($"      {r.Outline.Area / 1e6,5:0} m² at {r.ElevationMm / 1000:0.00} m ({r.Note})");
        }
        var allTypes = m.Levels.SelectMany(l => l.Walls.Walls).GroupBy(x => x.TypeName).Count();
        sb.AppendLine($"  Whole building: {m.Levels.Sum(l => l.Walls.Walls.Count)} walls in {allTypes} wall types.");
        sb.AppendLine();

        // what every layer of the plans used was read as
        sb.AppendLine("LAYERS (what each layer was read as; add a layers.txt next to the DWGs to change one)");
        var layers = m.Levels.SelectMany(l => l.Plan.LayerCounts).GroupBy(kv => kv.Key).Select(g => (Layer: g.Key, Count: g.Sum(kv => kv.Value), Role: m.Rules.Classify(g.Key)));
        foreach (var g in layers.GroupBy(x => x.Role).OrderBy(g => g.Key == LayerRole.Other).ThenBy(g => g.Key.ToString()))
            sb.AppendLine($"  {RoleName(g.Key),-26} " + string.Join(", ", g.OrderByDescending(x => x.Count).Select(x => $"{x.Layer} ({x.Count})")));
        sb.AppendLine();

        if (m.Notes.Count > 0)
        {
            sb.AppendLine("ASSUMPTIONS AND NOTES");
            foreach (var n in m.Notes) sb.AppendLine($"  - {n}");
            sb.AppendLine();
        }
        if (m.Skipped.Count > 0)
        {
            sb.AppendLine("FILES NOT USED");
            foreach (var s in m.Skipped) sb.AppendLine($"  - {s.FileName}: {s.Reason}");
        }
        return sb.ToString();
    }

    static string RoleName(LayerRole r) => r switch
    {
        LayerRole.Wall => "Walls",
        LayerRole.CurtainWall => "Curtain walls / screens",
        LayerRole.Window => "Windows",
        LayerRole.Door => "Doors",
        LayerRole.LevelText => "Spot heights",
        LayerRole.RoomName => "Room names",
        LayerRole.HeightText => "Heights (sill, head…)",
        LayerRole.Stair => "Stairs",
        LayerRole.Ramp => "Ramps",
        LayerRole.Lift => "Lifts",
        LayerRole.Column => "Columns",
        LayerRole.Roof => "Roofs",
        LayerRole.Rooflight => "Rooflights",
        LayerRole.Chimney => "Chimneys",
        LayerRole.Balcony => "Balconies / terraces",
        LayerRole.Railing => "Railings / fences",
        LayerRole.Fitting => "Fittings / furniture",
        LayerRole.Ceiling => "Ceilings",
        LayerRole.Area => "Area / room outlines",
        LayerRole.Overhead => "Overhead (beams…)",
        LayerRole.Annotation => "Notes, frames, grids",
        _ => "Not used",
    };
}
