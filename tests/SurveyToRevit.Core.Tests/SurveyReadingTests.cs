using System.Text.Json;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Tests;

/// <summary>survey_reading's file: the contract the BG MCP hub's 1:1 score reads.</summary>
public class SurveyReadingTests
{
    static SurveyModel Model()
    {
        var walls = WallDetector.Detect(SyntheticPlans.RectangleWithPartition(), Tolerances.Default);
        var plan = new SurveyDrawing
        {
            Path = "ground.dwg", Name = new DrawingName("ground.dwg", "00"), MmPerUnit = 1, UnitsNote = "mm",
            // drawn in survey grid mm, 1 km east and 2 km north of the base point
            WallLines = new[] { new Segment2(new Vec2(1_000_000, 2_000_000), new Vec2(1_004_000, 2_000_000), "A-WALL") },
            OpeningLines = new[] { new Segment2(new Vec2(1_001_000, 2_000_000), new Vec2(1_002_000, 2_000_000), "A-GLAZ-WINDOW") },
            Texts = new[]
            {
                new SurveyText(new Vec2(1_001_500, 2_000_300), "S 0.94", "Zz_Text"),
                new SurveyText(new Vec2(1_001_500, 2_000_500), "H 2.58", "Zz_Text"),
                new SurveyText(new Vec2(1_002_000, 2_002_000), "+12.345", "Zz_60_45 Levels"),
            },
            LayerCounts = new Dictionary<string, int> { ["A-WALL"] = 1 },
        };
        var level = new SurveyLevel("00", "Ground Floor", 0, 12340, null, plan, walls, null, Array.Empty<UnclearRegion>())
        {
            WallHeightMm = 3000, CeilingHeightMm = 2600,
            RoomNames = new[] { (new Vec2(500, 500), "Kitchen") },
        };
        return new SurveyModel
        {
            Frame = new SurveyFrame(new Vec2(1_000_000, 2_000_000), 0), Levels = new[] { level },
            Skipped = Array.Empty<SkippedDrawing>(), Notes = Array.Empty<string>(),
        };
    }

    [Fact]
    public void Reading_is_in_model_mm_with_the_builds_labels_and_roles()
    {
        var json = SurveyReading.ToJson(Model()).ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(SurveyReading.Schema, root.GetProperty("schema").GetString());
        var floor = root.GetProperty("levels")[0];
        Assert.Equal("Ground Floor", floor.GetProperty("name").GetString());
        // survey grid -> model: the base point is the origin
        var face = floor.GetProperty("wallFaces")[0];
        Assert.Equal(0, face[0].GetDouble()); Assert.Equal(4000, face[2].GetDouble());
        Assert.Equal(1000, floor.GetProperty("windowLines")[0][0].GetDouble());
        // sill and head notes, parsed as the build parses them
        var kinds = floor.GetProperty("heights").EnumerateArray().Select(h => (h.GetProperty("kind").GetString(), h.GetProperty("mm").GetDouble())).ToList();
        Assert.Contains(("Sill", 940.0), kinds);
        Assert.Contains(("Head", 2580.0), kinds);
        // spot heights from the level layer, with where they are
        var spot = Assert.Single(floor.GetProperty("spotHeights").EnumerateArray());
        Assert.Equal(12345, spot.GetProperty("mm").GetDouble());
        Assert.Equal(2000, spot.GetProperty("y").GetDouble());
        Assert.Equal("Kitchen", floor.GetProperty("rooms")[0].GetProperty("name").GetString());
        Assert.True(floor.GetProperty("walls").GetArrayLength() > 0);
    }

    [Fact]
    public void Floors_can_be_picked_and_roles_looked_up()
    {
        using var doc = JsonDocument.Parse(SurveyReading.ToJson(Model(), new[] { "First Floor" }).ToJsonString());
        Assert.Equal(0, doc.RootElement.GetProperty("levels").GetArrayLength());
        var roles = SurveyReading.Roles(LayerRules.Default, new[] { "A-WALL", "Zz_60_45 Levels" });
        Assert.Equal("Wall", roles["A-WALL"]!.GetValue<string>());
        Assert.Equal("LevelText", roles["Zz_60_45 Levels"]!.GetValue<string>());
    }
}
