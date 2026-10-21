using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Survey;

public enum LayerRole
{
    Other, Wall, Window, Door, LevelText, RoomName,
    /// <summary>Glazed walls and screens: walls built as curtain walls.</summary>
    CurtainWall,
    Stair, Ramp, Lift, Column, Roof, Rooflight, Chimney, Balcony, Railing,
    /// <summary>Fitted furniture, joinery, sanitaryware, kitchen units.</summary>
    Fitting,
    Ceiling,
    /// <summary>Area or room outlines (GIA, NIA, room boundaries).</summary>
    Area,
    /// <summary>Height notes (sill, head, ceiling): read as text wherever they are; the role only names the layer.</summary>
    HeightText,
    /// <summary>Things above the cut plane drawn dashed (beams, bulkheads, edges over).</summary>
    Overhead,
    /// <summary>Text, dimensions, frames, grids, hyperlinks: never geometry.</summary>
    Annotation,
}

/// <summary>
/// Decides what each CAD layer holds from its name. Defaults cover Uniclass
/// layer names (used by Plowman Craven and most UK surveyors), AIA names
/// (A-WALL, A-GLAZ…) and plain English names (WALLS-INT, STAIRS, ROOF…).
/// Rules are tried in order: the first that matches wins, so specific names
/// (a glazed partition, a wall plinth) come before general ones (partition, wall).
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

    public Regex HeightText { get; init; } = new(@"(^Zz_20-TextHt|height.?text|^A-ANNO-HGHT)", RegexOptions.IgnoreCase);
    public Regex CurtainWall { get; init; } = new(@"(PartGlass|curtain|glass.?(wall|screen|partition)|glazed.?(wall|screen|partition)|^A-WALL-(GLAZ|CWMG)|^Ss_25_12)", RegexOptions.IgnoreCase);
    public Regex Stair { get; init; } = new(@"(^EF_35_10|stair|steps?\b|^STEPS|^A-FLOR-STRS)", RegexOptions.IgnoreCase);
    public Regex Ramp { get; init; } = new(@"(^EF_35_20|ramp)", RegexOptions.IgnoreCase);
    public Regex Lift { get; init; } = new(@"(^Ss_80_50|\blifts?\b|elevator|^A-FLOR-EVTR)", RegexOptions.IgnoreCase);
    public Regex Column { get; init; } = new(@"(^Ss_20_10_75|column|^COLS?$|pillar|^S-COLS|steel|struct.?frame)", RegexOptions.IgnoreCase);
    public Regex Rooflight { get; init; } = new(@"(skylight|roof.?light|lantern|pave.?light)", RegexOptions.IgnoreCase);
    public Regex Roof { get; init; } = new(@"(^Ss_30-Roof|^EF_30|roof|^A-ROOF)", RegexOptions.IgnoreCase);
    public Regex Chimney { get; init; } = new(@"(chimney|^Ss_37_17)", RegexOptions.IgnoreCase);
    public Regex Balcony { get; init; } = new(@"(balcon|terrace|^Ss_30_12_85)", RegexOptions.IgnoreCase);
    public Regex Railing { get; init; } = new(@"(rail|balustrad|handrail|fence|^Ss_25_14)", RegexOptions.IgnoreCase);
    public Regex Fitting { get; init; } = new(@"(^Pr_40|joinery|furn|fitted|sanit|kitchen|^FF&?E|^A-FURN|^A-FLOR-(CASE|SPCL)|^P-SANR|counter|worktop)", RegexOptions.IgnoreCase);
    public Regex Ceiling { get; init; } = new(@"(^Ss_30_25|ceiling|^A-CLNG)", RegexOptions.IgnoreCase);
    public Regex Area { get; init; } = new(@"(^G[EI]A\b|\bN[IL]A\b|\bAREA\b|^A-AREA)", RegexOptions.IgnoreCase);
    public Regex Overhead { get; init; } = new(@"(overhead|^Zz_20_40_05|above|over$|bulkhead|beam)", RegexOptions.IgnoreCase);
    public Regex Annotation { get; init; } = new(@"(^Zz_|text|anno|dim|title|border|viewport|defpoints|scale|grid|ref$|^0$|hatch|frame|datum|elev|sect)", RegexOptions.IgnoreCase);
    /// <summary>Wall-like layers that are not walls (plinths, kerbs, skirtings).</summary>
    public Regex NotWall { get; init; } = new(@"(plinth|kerb|skirting|profile)", RegexOptions.IgnoreCase);

    /// <summary>The office's own layer names, tried before the built-in rules (see <see cref="WithOverrides"/>).</summary>
    public IReadOnlyList<(Regex Pattern, LayerRole Role)> Overrides { get; init; } = Array.Empty<(Regex, LayerRole)>();
    /// <summary>Where the overrides came from, for the report.</summary>
    public string? OverrideFile { get; init; }

    /// <summary>The name of the optional layer file placed next to the survey DWGs.</summary>
    public const string OverrideFileName = "layers.txt";

    /// <summary>
    /// These rules plus a layer file: one line per layer, "layer name = role" (e.g. "A-GLAZ-CURT = CurtainWall",
    /// "MY_STAIRS* = Stair"; * matches anything; lines starting with # are notes). Roles are the names
    /// in <see cref="LayerRole"/>. Unreadable lines are reported back, never guessed.
    /// </summary>
    public LayerRules WithOverrides(string path, List<string> problems)
    {
        var list = new List<(Regex, LayerRole)>();
        int n = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.LastIndexOf('=');
            if (eq <= 0 || !Enum.TryParse<LayerRole>(line[(eq + 1)..].Trim(), true, out var role))
            { problems.Add($"{System.IO.Path.GetFileName(path)} line {n} not understood: \"{line}\" (expected \"layer name = role\")"); continue; }
            var name = line[..eq].Trim();
            list.Add((new Regex("^" + Regex.Escape(name).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase), role));
        }
        return new LayerRules
        {
            Wall = Wall, Window = Window, Door = Door, LevelText = LevelText, FacesOnly = FacesOnly, RoomName = RoomName, Exclude = Exclude,
            HeightText = HeightText, CurtainWall = CurtainWall, Stair = Stair, Ramp = Ramp, Lift = Lift, Column = Column, Rooflight = Rooflight,
            Roof = Roof, Chimney = Chimney, Balcony = Balcony, Railing = Railing, Fitting = Fitting, Ceiling = Ceiling, Area = Area,
            Overhead = Overhead, Annotation = Annotation, NotWall = NotWall,
            Overrides = Overrides.Concat(list).ToList(), OverrideFile = path,
        };
    }

    public LayerRole Classify(string layer)
    {
        foreach (var (pattern, role) in Overrides)
            if (pattern.IsMatch(layer)) return role;
        if (LevelText.IsMatch(layer)) return LayerRole.LevelText;
        if (RoomName.IsMatch(layer)) return LayerRole.RoomName;
        if (HeightText.IsMatch(layer)) return LayerRole.HeightText;
        if (Exclude.IsMatch(layer)) return Annotation.IsMatch(layer) ? LayerRole.Annotation : LayerRole.Other;
        if (CurtainWall.IsMatch(layer)) return LayerRole.CurtainWall;
        if (Window.IsMatch(layer)) return LayerRole.Window;
        if (Door.IsMatch(layer)) return LayerRole.Door;
        if (Rooflight.IsMatch(layer)) return LayerRole.Rooflight;
        if (Chimney.IsMatch(layer)) return LayerRole.Chimney;
        if (Ramp.IsMatch(layer)) return LayerRole.Ramp;
        if (Stair.IsMatch(layer)) return LayerRole.Stair;
        if (Lift.IsMatch(layer)) return LayerRole.Lift;
        if (Railing.IsMatch(layer)) return LayerRole.Railing;
        if (Balcony.IsMatch(layer)) return LayerRole.Balcony;
        if (Overhead.IsMatch(layer)) return LayerRole.Overhead;
        if (NotWall.IsMatch(layer)) return LayerRole.Other;
        if (Wall.IsMatch(layer)) return LayerRole.Wall;
        if (Column.IsMatch(layer)) return LayerRole.Column;
        if (Roof.IsMatch(layer)) return LayerRole.Roof;
        if (Ceiling.IsMatch(layer)) return LayerRole.Ceiling;
        if (Fitting.IsMatch(layer)) return LayerRole.Fitting;
        if (Area.IsMatch(layer)) return LayerRole.Area;
        if (Annotation.IsMatch(layer)) return LayerRole.Annotation;
        return LayerRole.Other;
    }

    /// <summary>Roles whose lines are kept for building things other than walls and openings.</summary>
    public static bool IsFeature(LayerRole role) => role is LayerRole.Stair or LayerRole.Ramp or LayerRole.Lift or LayerRole.Column
        or LayerRole.Roof or LayerRole.Rooflight or LayerRole.Chimney or LayerRole.Balcony or LayerRole.Railing or LayerRole.Fitting
        or LayerRole.Ceiling or LayerRole.Area or LayerRole.Overhead;
}
