namespace SurveyToRevit.Core.Survey;

/// <summary>
/// Whether a drawing matches the layer rules and the height notes they read: a survey from a surveyor whose
/// layer names or abbreviations the rules do not know reads as "few walls", "no windows" or "no heights", with
/// nothing saying why. These checks say why, name the layers and texts, and say what to put in layers.txt.
/// Each message starts with CHECK, as the analysis notes do.
/// </summary>
public static class LayerCheck
{
    /// <summary>Layers with no role holding at least this share of the drawing (and <see cref="BusyMin"/> items) are worth a look.</summary>
    public const double BusyShare = 0.05;
    public const int BusyMin = 50;
    /// <summary>An unread abbreviation is reported once it is used this many times.</summary>
    public const int UnreadMin = 3;

    /// <summary>The busiest layers that the rules give no role (Other), most items first.</summary>
    public static List<(string Layer, int Count)> BusyUnassigned(SurveyDrawing d, LayerRules rules, int take = 5)
    {
        int total = d.LayerCounts.Values.Sum();
        return d.LayerCounts.Where(kv => kv.Value >= Math.Max(BusyMin, BusyShare * total) && rules.Classify(kv.Key) == LayerRole.Other)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(take)
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>"busiest layers with no role: A (1,200 items), B (900)", or "" when there are none.</summary>
    public static string BusyText(IReadOnlyList<(string Layer, int Count)> busy) =>
        busy.Count == 0 ? "" : "busiest layers with no role: " + string.Join(", ", busy.Select((b, i) => $"{b.Layer} ({b.Count:N0}{(i == 0 ? " items" : "")})"));

    /// <summary>The reason a drawing with too few wall lines is skipped, with the layers that may be its walls.</summary>
    public static string FewWalls(SurveyDrawing d, LayerRules rules)
    {
        string reason = $"only {d.WallLines.Count} lines on wall layers: not a floor plan";
        if (d.Name.LevelCode == null) return reason;
        // named as a floor plan: more likely its walls are on layers the rules don't know
        var busy = BusyUnassigned(d, rules);
        return busy.Count == 0 ? reason
            : reason + $", or its walls are on layers the rules don't know ({BusyText(busy)}). If so, name them in layers.txt (\"{busy[0].Layer} = Wall\"; survey_layer_rules) and analyse again";
    }

    /// <summary>What a floor plan holds that the rules do not read. Empty when everything is accounted for.</summary>
    public static List<string> Plan(SurveyDrawing d, LayerRules rules)
    {
        var w = new List<string>();
        string name = d.Name.FileName;
        var roles = d.LayerCounts.Where(kv => kv.Value > 0).Select(kv => rules.Classify(kv.Key)).ToHashSet();
        var busy = BusyUnassigned(d, rules);
        string busyText = BusyText(busy);
        bool busyTold = false;
        string Hint() { if (busy.Count == 0 || busyTold) return ""; busyTold = true; return $" The {busyText}."; }

        if (!roles.Contains(LayerRole.Window) && !roles.Contains(LayerRole.CurtainWall))
            w.Add($"CHECK {name}: no layer is read as windows, so this floor gets none.{Hint()} Name the window layer in layers.txt (\"LAYER = Window\").");
        if (!roles.Contains(LayerRole.Door) && d.DoorBlockLines.Count == 0 && d.DoorArcs.Count == 0)
            w.Add($"CHECK {name}: no layer is read as doors.{Hint()} Name the door layer in layers.txt (\"LAYER = Door\").");
        if (!roles.Contains(LayerRole.LevelText))
            w.Add($"CHECK {name}: no layer is read as spot heights, so the floor level comes from elsewhere or must be set by hand.{Hint()} Name the level layer in layers.txt (\"LAYER = LevelText\").");
        if (busy.Count > 0 && !busyTold)
            w.Add($"CHECK {name}: {busyText}. If they hold walls, windows, doors or heights, name them in layers.txt (survey_layer_rules); if not, \"LAYER = Annotation\" says so.");

        var unread = SurveyHeights.Unread(d.Texts.Select(t => t.Value), rules.LabelWords).Where(u => u.Count >= UnreadMin).Take(4).ToList();
        if (unread.Count > 0)
            w.Add($"CHECK {name}: {unread.Sum(u => u.Count)} texts look like height notes in a form that is not read: " +
                  string.Join(", ", unread.Select(u => $"{u.Word} x{u.Count} (\"{u.Example}\")")) +
                  $". If they are sills, heads or ceilings, add a line per abbreviation to layers.txt, e.g. \"label {unread[0].Word} = Sill\" " +
                  $"(kinds: {string.Join(", ", Enum.GetNames<HeightKind>())}), and analyse again.");
        return w;
    }
}
