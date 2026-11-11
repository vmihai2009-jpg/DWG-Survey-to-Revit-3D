using System.Text.Json;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Tests;

/// <summary>The Revit-free parts behind the assistant tools: review item numbers, floor-by-floor review lists, layers.txt.</summary>
public class McpSupportTests
{
    static CheckItem Check(string level, CheckKind kind, double x = 0) => new(level, kind, new Vec2(x, 0), kind.ToString());

    [Fact]
    public void Review_items_are_numbered_from_one_in_list_order()
    {
        var list = ReviewList.From("P", new[] { Check("01", CheckKind.LooseWallEnd), Check("00", CheckKind.UnreachableRoom), Check("00", CheckKind.CrossingWalls) }, Array.Empty<SurveyLevel>());
        Assert.Equal(new[] { 1, 2, 3 }, list.Items.Select(i => i.Id));
        Assert.Equal("UnreachableRoom", list.Items[0].Kind); // most important first
    }

    [Fact]
    public void A_list_saved_before_ids_gets_them_on_load_and_keeps_its_ticks()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2r-review-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Project":"P","Built":"2026-10-01T10:00:00","Items":[{"Level":"Ground","Kind":"LooseWallEnd","Done":true},{"Level":"First","Kind":"CrossingWalls"}]}""");
            var list = ReviewList.Load(path)!;
            Assert.Equal(new[] { 1, 2 }, list.Items.Select(i => i.Id));
            Assert.True(list.Items[0].Done);
            list.Items[1].Note = "fixed";
            list.Save(path);
            var again = ReviewList.Load(path)!;
            Assert.Equal("fixed", again.Items[1].Note);
            Assert.Equal(2, again.Items[1].Id);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rebuilding_one_floor_replaces_its_items_and_keeps_the_others()
    {
        var old = ReviewList.From("P", new[] { Check("00", CheckKind.UnreachableRoom), Check("01", CheckKind.UnreachableRoom), Check("01", CheckKind.LooseWallEnd) }, Array.Empty<SurveyLevel>());
        old.Items.First(i => i.Level == "00").Done = true;
        old.Items.First(i => i.Level == "00").Note = "checked on site";
        var fresh = ReviewList.From("P", new[] { Check("00", CheckKind.CrossingWalls), Check("01", CheckKind.CrossingWalls) }, Array.Empty<SurveyLevel>());

        old.ReplaceLevels(fresh, new[] { "01" });

        var ground = Assert.Single(old.Items, i => i.Level == "00");
        Assert.True(ground.Done);
        Assert.Equal("checked on site", ground.Note);
        var first = Assert.Single(old.Items, i => i.Level == "01");
        Assert.Equal("CrossingWalls", first.Kind);
        Assert.Equal(old.Items.Count, old.Items.Select(i => i.Id).Distinct().Count()); // every number used once
        Assert.All(old.Items, i => Assert.True(i.Id > 0));
    }

    [Fact]
    public void Layer_file_is_written_checked_and_read_back_by_the_rules()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2r-layers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var bad = SurveyLayers.Write(folder, new[] { ("MY_WALLS*", "Wall"), ("THING", "Banana") });
            Assert.Single(bad);
            Assert.False(File.Exists(Path.Combine(folder, LayerRules.OverrideFileName))); // nothing written when a line is wrong

            Assert.Empty(SurveyLayers.Write(folder, new[] { ("MY_WALLS*", "wall"), ("GLASS", "CurtainWall") }, "from the surveyor's key"));
            var problems = new List<string>();
            var rules = SurveyLayers.RulesFor(new[] { Path.Combine(folder, "plan.dwg") }, problems);
            Assert.Empty(problems);
            Assert.Equal(LayerRole.Wall, rules.Classify("MY_WALLS_INT"));
            Assert.Equal(LayerRole.CurtainWall, rules.Classify("glass"));

            Assert.Empty(SurveyLayers.Write(folder, new[] { ("GLASS", "Window") }));
            Assert.True(File.Exists(Path.Combine(folder, LayerRules.OverrideFileName + ".bak"))); // the old file is kept
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Summary_json_has_no_values_json_cannot_hold()
    {
        var walls = WallDetector.Detect(SyntheticPlans.RectangleWithPartition(), Tolerances.Default);
        var plan = new SurveyDrawing
        {
            Path = "ground.dwg", Name = new DrawingName("ground.dwg", "00"), MmPerUnit = 1, UnitsNote = "mm",
            WallLines = Array.Empty<Segment2>(), OpeningLines = Array.Empty<Segment2>(), Texts = Array.Empty<SurveyText>(),
            LayerCounts = new Dictionary<string, int> { ["A-WALL"] = 12, ["MISC"] = 40 },
        };
        // a floor with no height in the survey: NaN must not reach the JSON
        var level = new SurveyLevel("00", "Ground Floor", 0, double.NaN, null, plan, walls, null, Array.Empty<UnclearRegion>());
        var model = new SurveyModel
        {
            Frame = new SurveyFrame(new Vec2(530000000, 180000000), 12.5), Levels = new[] { level },
            Skipped = new[] { new SkippedDrawing("fire.dwg", "duplicate of Ground Floor") }, Notes = new[] { "note" },
            Checks = new[] { Check("00", CheckKind.UnreachableRoom, 1234.4) },
        };

        var json = SurveySummary.From(model).ToJson();
        using var doc = JsonDocument.Parse(json);
        var floor = doc.RootElement.GetProperty("levels")[0];
        Assert.Equal("Ground Floor", floor.GetProperty("name").GetString());
        Assert.False(floor.TryGetProperty("elevationM", out _)); // unknown is left out, not NaN
        Assert.Equal(walls.Walls.Count, floor.GetProperty("walls").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("checksByKind").GetProperty("UnreachableRoom").GetInt32());
        Assert.Equal(1234, doc.RootElement.GetProperty("topChecks")[0].GetProperty("x").GetDouble());

        var layers = SurveyLayers.Of(new[] { plan }, LayerRules.Default);
        Assert.Equal("MISC", layers[0].Layer); // busiest first
        Assert.Equal("Other", layers[0].Role);
        Assert.Equal("Wall", layers[1].Role);
    }
}
