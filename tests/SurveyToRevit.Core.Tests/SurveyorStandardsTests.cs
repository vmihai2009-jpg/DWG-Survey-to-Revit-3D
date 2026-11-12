using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Tests;

/// <summary>Other surveyors' height-note forms, the office's own abbreviations from layers.txt, and the CHECK notes for a drawing the rules don't fit.</summary>
public class SurveyorStandardsTests
{
    static readonly Vec2 P = new(0, 0);
    static List<HeightNote> Read(params string[] texts) => SurveyHeights.Read(texts.Select(t => (P, t)));

    [Theory]
    [InlineData("SILL HT 0.940", HeightKind.Sill, 940)]
    [InlineData("Sill height: 0.94", HeightKind.Sill, 940)]
    [InlineData("CILL 940", HeightKind.Sill, 940)]
    [InlineData("0.94 SILL", HeightKind.Sill, 940)]
    [InlineData("S 0,94", HeightKind.Sill, 940)]
    [InlineData("S 940mm", HeightKind.Sill, 940)]
    [InlineData("HEAD HT 2.10", HeightKind.Head, 2100)]
    [InlineData("2.10 head", HeightKind.Head, 2100)]
    [InlineData("H 2.1m", HeightKind.Head, 2100)]
    [InlineData("Door head 2.03", HeightKind.DoorHead, 2030)]
    [InlineData("2.80m ceiling", HeightKind.Ceiling, 2800)]
    [InlineData("CEILING HEIGHT 2.52", HeightKind.Ceiling, 2520)]
    public void Other_ways_of_writing_a_height_are_read(string text, HeightKind kind, double mm)
    {
        var n = Assert.Single(Read(text));
        Assert.Equal(kind, n.Kind);
        Assert.Equal(mm, n.Mm);
    }

    [Theory]
    [InlineData("S 0.94 H 2.10")]
    [InlineData("S0.94/H2.10")]
    [InlineData("SH 0.94, HH 2.10")]
    [InlineData("SILL 0.94 & HEAD 2.10")]
    public void Two_heights_in_one_text_are_both_read(string text)
    {
        var n = Read(text);
        Assert.Equal(2, n.Count);
        Assert.Contains(n, x => x.Kind == HeightKind.Sill && x.Mm == 940);
        Assert.Contains(n, x => x.Kind == HeightKind.Head && x.Mm == 2100);
        Assert.True(SurveyHeights.IsOpeningNote(text));
    }

    [Theory]
    [InlineData("Lift")]
    [InlineData("FFL 27.05")]
    [InlineData("cl 21.50")]
    [InlineData("W12")]
    [InlineData("0.94 S")]       // a bare letter after the number is too loose to read
    [InlineData("S 0.94 Lift")]
    public void Other_texts_are_not_heights(string text) => Assert.Empty(Read(text));

    [Fact]
    public void The_offices_own_abbreviations_come_first()
    {
        var own = new Dictionary<string, HeightKind>(StringComparer.OrdinalIgnoreCase) { ["wh"] = HeightKind.OpeningHeight, ["sill lvl"] = HeightKind.Sill, ["h"] = HeightKind.OpeningHeight };
        Assert.Empty(Read("WH 1.20"));
        var n = SurveyHeights.Read(new[] { (P, "WH 1.20"), (P, "SILL LVL 0.90"), (P, "1.50 WH"), (P, "H 1.80") }, own);
        Assert.Equal(new[] { HeightKind.OpeningHeight, HeightKind.Sill, HeightKind.OpeningHeight, HeightKind.OpeningHeight }, n.Select(x => x.Kind));
        Assert.Equal(new double[] { 1200, 900, 1500, 1800 }, n.Select(x => x.Mm));
    }

    [Fact]
    public void Unread_notes_are_grouped_by_abbreviation()
    {
        var u = SurveyHeights.Unread(new[] { "WH 1.20", "WH 1.25", "wh 1.30", "TH 2.10", "S 0.94", "FFL 27.05", "W12", "Kitchen" });
        Assert.Equal(("WH", 3), (u[0].Word, u[0].Count));
        Assert.Equal(("TH", 1), (u[1].Word, u[1].Count));
        Assert.Equal(2, u.Count);
        var own = new Dictionary<string, HeightKind>(StringComparer.OrdinalIgnoreCase) { ["wh"] = HeightKind.OpeningHeight };
        Assert.DoesNotContain(SurveyHeights.Unread(new[] { "WH 1.20" }, own), x => x.Word == "WH");
    }

    [Theory]
    [InlineData("+27.050", 27050.0)]
    [InlineData("FFL: +27.05", 27050.0)]
    [InlineData("LVL 27.05", 27050.0)]
    [InlineData("27.05 AOD", 27050.0)]
    [InlineData("27.05mAOD", 27050.0)]
    [InlineData("27.050 FFL", 27050.0)]
    public void More_spot_height_forms_parse(string text, double mm) => Assert.Equal(mm, LevelEstimator.ParseMm(text)!.Value, 6);

    [Fact]
    public void Label_lines_in_layers_txt_are_read_and_kept()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var file = Path.Combine(dir, LayerRules.OverrideFileName);
            File.WriteAllLines(file, new[] { "MY_WALLS* = Wall", "label WH = OpeningHeight", "label  Sill Lvl = sill", "LABEL TEXT = Annotation", "label X = Nonsense" });
            var problems = new List<string>();
            var rules = LayerRules.Default.WithOverrides(file, problems);
            Assert.Equal(HeightKind.OpeningHeight, rules.LabelWords["wh"]);
            Assert.Equal(HeightKind.Sill, rules.LabelWords["sill lvl"]);
            Assert.Equal(LayerRole.Annotation, rules.Classify("LABEL TEXT"));   // a layer called LABEL..., not a label word
            Assert.Single(problems);                                              // "label X = Nonsense"
            // rewriting the layer roles keeps the label lines
            Assert.Empty(SurveyLayers.Write(dir, new[] { ("A-WALL*", "Wall") }));
            var again = LayerRules.Default.WithOverrides(file, new List<string>());
            Assert.Equal(HeightKind.OpeningHeight, again.LabelWords["wh"]);
            Assert.Equal(LayerRole.Wall, again.Classify("A-WALL-EXT"));
        }
        finally { Directory.Delete(dir, true); }
    }

    static SurveyDrawing Drawing(Dictionary<string, int> layers, params string[] texts) => new()
    {
        Path = "ground.dwg", Name = new DrawingName("ground.dwg", "00"), MmPerUnit = 1, UnitsNote = "mm",
        WallLines = Array.Empty<Segment2>(), OpeningLines = Array.Empty<Segment2>(),
        Texts = texts.Select(t => new SurveyText(P, t, "TXT")).ToList(), LayerCounts = layers,
    };

    [Fact]
    public void A_drawing_the_rules_fit_gives_no_checks()
    {
        var d = Drawing(new() { ["A-WALL"] = 900, ["A-GLAZ"] = 200, ["A-DOOR"] = 100, ["A-ANNO-LEVL"] = 40, ["A-ANNO-TEXT"] = 300 }, "S 0.94", "H 2.10", "Kitchen");
        Assert.Empty(LayerCheck.Plan(d, LayerRules.Default));
    }

    [Fact]
    public void Another_surveyors_layers_and_labels_are_named()
    {
        // walls read, but windows, doors and levels are on layers the rules don't know, and sills are written "WH"
        var d = Drawing(new() { ["A-WALL"] = 900, ["FENSTER"] = 300, ["TUER"] = 120, ["HOEHEN"] = 60, ["0"] = 5 },
                        "WH 1.20", "WH 1.25", "WH 1.30", "WH 1.10");
        var w = LayerCheck.Plan(d, LayerRules.Default);
        Assert.Contains(w, x => x.Contains("no layer is read as windows") && x.Contains("FENSTER (300 items)") && x.Contains("TUER (120)"));
        Assert.Contains(w, x => x.Contains("no layer is read as doors"));
        Assert.Contains(w, x => x.Contains("no layer is read as spot heights"));
        Assert.Contains(w, x => x.Contains("WH x4") && x.Contains("label WH = Sill"));
        Assert.All(w, x => Assert.StartsWith("CHECK ground.dwg", x));
        // once layers.txt names them, the checks go
        var rules = new LayerRules
        {
            Overrides = new List<(System.Text.RegularExpressions.Regex, LayerRole)>
            {
                (new("^FENSTER$"), LayerRole.Window), (new("^TUER$"), LayerRole.Door), (new("^HOEHEN$"), LayerRole.LevelText),
            },
            LabelWords = new Dictionary<string, HeightKind> { ["wh"] = HeightKind.Sill },
        };
        Assert.Empty(LayerCheck.Plan(d, rules));
    }

    [Fact]
    public void A_floor_plan_with_its_walls_on_unknown_layers_says_which()
    {
        var d = Drawing(new() { ["MAUERWERK"] = 2400, ["FENSTER"] = 300 });
        var why = LayerCheck.FewWalls(d, LayerRules.Default);
        Assert.Contains("only 0 lines on wall layers", why);
        Assert.Contains("\"MAUERWERK = Wall\"", why);
    }
}
