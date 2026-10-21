using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Survey;

public enum LayerRole { Other, Wall, Window, Door, LevelText, RoomName }

/// <summary>
/// Decides what each CAD layer holds from its name. Defaults cover Uniclass
/// layer names (used by Plowman Craven and most UK surveyors), AIA names
/// (A-WALL, A-GLAZ…) and plain English names.
/// </summary>
public sealed class LayerRules
{
    public Regex Wall { get; init; } = new(@"(^EF_25|^Ss_25_10|^Ss_25-Wall|[-_ ]wall|^wall|partition|^A-WALL|^BUILDINGS?$|^EXT(ERNAL)?[-_ ]?WALLS?)", RegexOptions.IgnoreCase);
    public Regex Window { get; init; } = new(@"(^Ss_25_30_95|window|glaz|^A-GLAZ)", RegexOptions.IgnoreCase);
    public Regex Door { get; init; } = new(@"(^Ss_25_30_20|door)", RegexOptions.IgnoreCase);
    public Regex LevelText { get; init; } = new(@"(^Zz_60_45|[-_ ]level|^level|spot.?height|^A-ANNO-LEVL)", RegexOptions.IgnoreCase);
    /// <summary>Wall layers that also carry other building lines (roof outlines, rooflights): their lines count only as pairs, never as single-line walls.</summary>
    public Regex FacesOnly { get; init; } = new(@"^BUILDINGS?$", RegexOptions.IgnoreCase);
    public Regex RoomName { get; init; } = new(@"(^Zz_20_10|room.?name|^A-ANNO-ROOM|^A-AREA-IDEN)", RegexOptions.IgnoreCase);
    /// <summary>Layers never used for walls even if they mention "wall" (e.g. headroom notes, text).</summary>
    public Regex Exclude { get; init; } = new(@"(^Zz_|text|anno|hatch|dim)", RegexOptions.IgnoreCase);

    public static LayerRules Default { get; } = new();

    public LayerRole Classify(string layer)
    {
        if (LevelText.IsMatch(layer)) return LayerRole.LevelText;
        if (RoomName.IsMatch(layer)) return LayerRole.RoomName;
        if (Exclude.IsMatch(layer)) return LayerRole.Other;
        if (Window.IsMatch(layer)) return LayerRole.Window;
        if (Door.IsMatch(layer)) return LayerRole.Door;
        if (Wall.IsMatch(layer)) return LayerRole.Wall;
        return LayerRole.Other;
    }
}
