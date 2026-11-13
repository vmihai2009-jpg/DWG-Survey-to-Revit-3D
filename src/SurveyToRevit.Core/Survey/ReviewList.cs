using System.Text.Json;
using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Survey;

/// <summary>One place to look at in Revit: the level (by name), where (model mm), what, and whether it has been looked at.</summary>
public sealed class ReviewItem
{
    /// <summary>The item's number in its list (1, 2, 3...), so it can be named: "item 14". 0 in lists saved before ids.</summary>
    public int Id { get; set; }
    /// <summary>What was found or done when it was looked at (optional).</summary>
    public string Note { get; set; } = "";
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
        Items = checks.OrderBy(c => c.Priority).ThenBy(c => c.LevelCode).Select((c, i) => new ReviewItem
        {
            Id = i + 1,
            Level = levels.FirstOrDefault(l => l.Code == c.LevelCode)?.Name ?? c.LevelCode,
            Kind = c.Kind.ToString(),
            What = CheckItem.Describe(c.Kind),
            Message = c.Message,
            X = Math.Round(c.At.X), Y = Math.Round(c.At.Y),
            Priority = c.Priority,
        }).ToList(),
    };

    /// <summary>The list's file for a Revit project, in the reports folder's "review" folder.</summary>
    /// <summary>The build log's worst calls that aren't self-checks (those are on the list already), so a person
    /// without the MCP side sees where to act too (item V).</summary>
    public void AddLog(BuildLog log, int max = 30)
    {
        int id = Items.Count == 0 ? 0 : Items.Max(i => i.Id);
        foreach (var e in log.Items.Where(e => !e.Kind.StartsWith("check:", StringComparison.Ordinal) && e.Score > 0).Take(max))
        {
            var at = e.Line is { Length: 4 } l ? new[] { (l[0] + l[2]) / 2, (l[1] + l[3]) / 2 } : e.At;
            Items.Add(new ReviewItem
            {
                Id = ++id, Level = e.Level, Kind = e.Kind, What = "Build log: " + e.Kind,
                Message = $"{e.Confidence}: {e.Why}" + (string.IsNullOrEmpty(e.Settle) ? "" : $" To settle: {e.Settle}"),
                X = at.Length > 0 ? Math.Round(at[0]) : 0, Y = at.Length > 1 ? Math.Round(at[1]) : 0,
                Priority = e.Confidence == "Low" ? 2 : 3,
            });
        }
    }

    public static string PathFor(string reportsFolder, string project) =>
        Path.Combine(reportsFolder, "review", Regex.Replace(Path.GetFileNameWithoutExtension(project), @"[^\w\- ]", "_") + ".json");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static ReviewList? Load(string path)
    {
        try
        {
            var list = File.Exists(path) ? JsonSerializer.Deserialize<ReviewList>(File.ReadAllText(path)) : null;
            list?.NumberItems();
            return list;
        }
        catch { return null; }
    }

    /// <summary>Gives every item without a number the next free one (lists saved before ids, items added by a floor rebuilt on its own).</summary>
    public void NumberItems()
    {
        int next = Items.Count == 0 ? 1 : Items.Max(i => i.Id) + 1;
        foreach (var i in Items.Where(i => i.Id <= 0)) i.Id = next++;
    }

    /// <summary>
    /// After rebuilding some floors on their own: their items are replaced by the new ones; the other floors'
    /// items, with their ticks and notes, are kept.
    /// </summary>
    public void ReplaceLevels(ReviewList fresh, IEnumerable<string> levelNames)
    {
        var names = levelNames.ToHashSet();
        Items.RemoveAll(i => names.Contains(i.Level));
        foreach (var i in fresh.Items.Where(i => names.Contains(i.Level))) { i.Id = 0; Items.Add(i); }
        NumberItems();
        Built = fresh.Built;
    }
}
