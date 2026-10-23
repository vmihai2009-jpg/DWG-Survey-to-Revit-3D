using System.Text.Json;
using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Survey;

/// <summary>One place to look at in Revit: the level (by name), where (model mm), what, and whether it has been looked at.</summary>
public sealed class ReviewItem
{
    public string Level { get; set; } = "";
    public string Kind { get; set; } = "";
    public string What { get; set; } = "";
    public string Message { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public int Priority { get; set; }
    public bool Done { get; set; }
}

/// <summary>
/// The self-checks of one build, saved next to the reports as JSON (one file per Revit project), so the
/// review list can be opened, worked through and ticked off over several sittings.
/// </summary>
public sealed class ReviewList
{
    public string Project { get; set; } = "";
    public DateTime Built { get; set; }
    public List<ReviewItem> Items { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ReviewList From(string project, IEnumerable<CheckItem> checks, IReadOnlyList<SurveyLevel> levels) => new()
    {
        Project = project,
        Built = DateTime.Now,
        Items = checks.OrderBy(c => c.Priority).ThenBy(c => c.LevelCode).Select(c => new ReviewItem
        {
            Level = levels.FirstOrDefault(l => l.Code == c.LevelCode)?.Name ?? c.LevelCode,
            Kind = c.Kind.ToString(),
            What = CheckItem.Describe(c.Kind),
            Message = c.Message,
            X = Math.Round(c.At.X), Y = Math.Round(c.At.Y),
            Priority = c.Priority,
        }).ToList(),
    };

    /// <summary>The list's file for a Revit project, in the reports folder's "review" folder.</summary>
    public static string PathFor(string reportsFolder, string project) =>
        Path.Combine(reportsFolder, "review", Regex.Replace(Path.GetFileNameWithoutExtension(project), @"[^\w\- ]", "_") + ".json");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static ReviewList? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ReviewList>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }
}
