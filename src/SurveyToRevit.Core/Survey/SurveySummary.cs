using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurveyToRevit.Core.Survey;

/// <summary>
/// A survey analysis as plain numbers and words: what <see cref="SurveyReport"/> says in text, in a form a
/// program can read (JSON). <see cref="SurveyModel"/> itself cannot be saved as JSON (it holds functions and
/// the whole drawings), and is far too big to hand to an assistant; this is a few hundred words.
/// No Revit here: SurveyLab writes it too (summary.json).
/// </summary>
public sealed class SurveySummary
{
    public double BaseEastM { get; set; }
    public double BaseNorthM { get; set; }
    public double ProjectNorthDeg { get; set; }
    public string? LayerFile { get; set; }
    public List<LevelSummary> Levels { get; set; } = new();
    public List<SkippedDrawing> Skipped { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public int Roofs { get; set; }
    public int Sections { get; set; }
    /// <summary>Self-check findings by kind ("UnreachableRoom": 3).</summary>
    public Dictionary<string, int> ChecksByKind { get; set; } = new();
    /// <summary>The most important findings (all of them are in the review list after a build).</summary>
    public List<CheckSummary> TopChecks { get; set; } = new();
    public int Checks { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SurveySummary From(SurveyModel m, int maxChecks = 25, int maxNotes = 30)
    {
        static double? Num(double v, int digits = 0) => double.IsNaN(v) || double.IsInfinity(v) ? null : Math.Round(v, digits);
        var name = m.Levels.ToDictionary(l => l.Code, l => l.Name);
        return new SurveySummary
        {
            BaseEastM = Math.Round(m.Frame.BasePoint.X / 1000, 3),
            BaseNorthM = Math.Round(m.Frame.BasePoint.Y / 1000, 3),
            ProjectNorthDeg = Math.Round(m.Frame.RotationDeg, 2),
            LayerFile = m.Rules.OverrideFile,
            Levels = m.Levels.Select(l => new LevelSummary
            {
                Code = l.Code,
                Name = l.Name,
                Drawing = l.Plan.Name.FileName,
                ElevationM = Num(l.ElevationMm / 1000, 3),
                SpotHeights = l.Estimate == null ? "none" : $"{l.Estimate.Supporting} of {l.Estimate.Total} agree",
                WallHeightMm = Num(l.WallHeightMm),
                CeilingHeightMm = Num(l.CeilingHeightMm),
                CeilingFromSurvey = l.CeilingFromSurvey,
                Mezzanine = l.Partial,
                Include = l.Include,
                OutlineM2 = l.Outline == null ? null : Math.Round(l.Outline.Area / 1e6),
                Walls = l.Walls.Walls.Count,
                WallLengthM = Math.Round(l.Walls.Walls.Sum(w => w.Length) / 1000, 1),
                WallsHigh = l.Walls.Walls.Count(w => w.Confidence == Confidence.High),
                WallsMedium = l.Walls.Walls.Count(w => w.Confidence == Confidence.Medium),
                WallsLow = l.Walls.Walls.Count(w => w.Confidence == Confidence.Low),
                CurtainWalls = l.Walls.Walls.Count(w => w.Curtain),
                UnpairedLines = l.Walls.UnpairedSegments.Count,
                Windows = l.Windows.Count,
                Doors = l.Doors.Count,
                RoomNames = l.RoomNames.Count,
                Stairs = l.Stairs.Count,
                UnclearAreas = l.Unclear.Count,
                Details = l.Details.Count,
                Checks = m.Checks.Count(c => c.LevelCode == l.Code),
            }).ToList(),
            Skipped = m.Skipped.ToList(),
            Notes = m.Notes.Take(maxNotes).ToList(),
            Roofs = m.Roofs.Count,
            Sections = m.Sections.Count,
            Checks = m.Checks.Count,
            ChecksByKind = m.Checks.GroupBy(c => c.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            TopChecks = m.Checks.OrderBy(c => c.Priority).Take(maxChecks).Select(c => new CheckSummary
            {
                Level = name.TryGetValue(c.LevelCode, out var n) ? n : c.LevelCode,
                Kind = c.Kind.ToString(),
                What = CheckItem.Describe(c.Kind),
                Message = c.Message,
                X = Math.Round(c.At.X),
                Y = Math.Round(c.At.Y),
                Priority = c.Priority,
            }).ToList(),
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

public sealed class LevelSummary
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Drawing { get; set; } = "";
    /// <summary>Floor level above survey datum; null when the plan gives no height.</summary>
    public double? ElevationM { get; set; }
    public string SpotHeights { get; set; } = "";
    public double? WallHeightMm { get; set; }
    public double? CeilingHeightMm { get; set; }
    public bool CeilingFromSurvey { get; set; }
    public bool Mezzanine { get; set; }
    public bool Include { get; set; }
    public double? OutlineM2 { get; set; }
    public int Walls { get; set; }
    public double WallLengthM { get; set; }
    public int WallsHigh { get; set; }
    public int WallsMedium { get; set; }
    public int WallsLow { get; set; }
    public int CurtainWalls { get; set; }
    /// <summary>Wall-layer lines that paired with nothing (never made into walls).</summary>
    public int UnpairedLines { get; set; }
    public int Windows { get; set; }
    public int Doors { get; set; }
    public int RoomNames { get; set; }
    public int Stairs { get; set; }
    public int UnclearAreas { get; set; }
    public int Details { get; set; }
    public int Checks { get; set; }
}

public sealed class CheckSummary
{
    public string Level { get; set; } = "";
    public string Kind { get; set; } = "";
    public string What { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>Model mm (the Revit model's X and Y).</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public int Priority { get; set; }
}

/// <summary>One CAD layer across a set of drawings: how much is on it and what the layer rules take it for.</summary>
public sealed record LayerSummary(string Layer, string Role, int Entities, int Drawings);

public static class SurveyLayers
{
    /// <summary>
    /// Every layer in the drawings with the role the rules give it, busiest first. A busy layer whose role is
    /// Other is the usual reason a survey reads badly: name it in layers.txt ("layer name = Wall").
    /// </summary>
    public static List<LayerSummary> Of(IEnumerable<SurveyDrawing> drawings, LayerRules rules) =>
        drawings.SelectMany(d => d.LayerCounts)
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LayerSummary(g.Key, rules.Classify(g.Key).ToString(), g.Sum(kv => kv.Value), g.Count()))
            .OrderByDescending(l => l.Entities).ToList();

    /// <summary>The layer rules for a set of drawings: the built-in ones plus a layers.txt next to them, as the analysis uses.</summary>
    public static LayerRules RulesFor(IEnumerable<string> paths, List<string> problems)
    {
        var file = paths.Select(p => Path.Combine(Path.GetDirectoryName(p) ?? "", LayerRules.OverrideFileName)).FirstOrDefault(File.Exists);
        return file == null ? LayerRules.Default : LayerRules.Default.WithOverrides(file, problems);
    }

    /// <summary>Checks and writes a layers.txt. Returns the lines that were not understood (nothing is written then).</summary>
    public static List<string> Write(string folder, IEnumerable<(string Layer, string Role)> rules, string? note = null)
    {
        var problems = new List<string>();
        var lines = new List<string> { "# Survey to Revit layer roles: layer name = role (* matches anything)" };
        if (!string.IsNullOrWhiteSpace(note)) lines.Add("# " + note.Replace("\r", " ").Replace("\n", " "));
        foreach (var (layer, role) in rules)
        {
            if (string.IsNullOrWhiteSpace(layer) || layer.Contains('\n')) { problems.Add($"Layer name not usable: \"{layer}\""); continue; }
            if (!Enum.TryParse<LayerRole>(role, true, out var r)) { problems.Add($"\"{role}\" is not a role. Roles: {string.Join(", ", Enum.GetNames<LayerRole>())}"); continue; }
            lines.Add($"{layer.Trim()} = {r}");
        }
        if (problems.Count > 0) return problems;
        var path = Path.Combine(folder, LayerRules.OverrideFileName);
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.WriteAllLines(path, lines);
        return problems;
    }
}
