using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class BuildLogTests
{
    [Fact]
    public void Worst_first_by_severity_times_doubt()
    {
        var log = new BuildLog();
        log.Add(new BuildLogEntry { Kind = "a", Confidence = "High", Severity = 20 });   // 2
        log.Add(new BuildLogEntry { Kind = "b", Confidence = "Low", Severity = 10 });    // 7
        log.Add(new BuildLogEntry { Kind = "c", Confidence = "Medium", Severity = 10 }); // 4
        log.Sort();
        Assert.Equal(new[] { "b", "c", "a" }, log.Items.Select(e => e.Kind));
        Assert.Equal(7, log.Items[0].Score);
    }

    [Fact]
    public void Writes_json_with_its_own_resolver()
    {
        var log = new BuildLog { Project = "P" };
        log.Add(new BuildLogEntry { Kind = "bridge", Level = "Ground", Confidence = "Low", Severity = 1.2, Line = new double[] { 0, 0, 1200, 0 } });
        var json = log.ToJson();
        Assert.Contains("\"kind\": \"bridge\"", json);
        Assert.Contains("\"entries\": 1", json);
        Assert.Contains("\"byKind\"", json);
    }

    [Fact]
    public void Revit_refusals_become_entries()
    {
        var log = new BuildLog();
        log.AddBuildLines(new[] { "Ground: 12 walls", "REVIT REFUSED Ground floor slab: curve loops cannot compose a valid boundary" });
        var e = Assert.Single(log.Items);
        Assert.Equal("revit-refused", e.Kind);
    }

    [Fact]
    public void A_reading_logs_its_unsure_walls_and_levels()
    {
        var sure = new WallCandidate(new Segment2(new Vec2(0, 0), new Vec2(10000, 0), "W"), 300, 300, Confidence.High, "W", "W");
        var single = new WallCandidate(new Segment2(new Vec2(0, 5000), new Vec2(4000, 5000), "W"), 100, 100, Confidence.Low, "W", "") { DrawnFaceSide = 1 };
        var bridge = new WallCandidate(new Segment2(new Vec2(10000, 0), new Vec2(10000, 1200), "bridge", "bridge"), 300, 300, Confidence.Low, "bridge", "bridge");
        var walls = new WallDetectionResult(new[] { sure, single, bridge }, Array.Empty<Segment2>(), Array.Empty<double>(), 0, 0, Array.Empty<double>());
        var outline = new Polygon2(new[] { new Vec2(0, 0), new Vec2(10000, 0), new Vec2(10000, 8000), new Vec2(0, 8000) });
        var lv = new SurveyLevel("00", "Ground Floor", 0, 26470, new LevelEstimate(26470, 3, 10, new[] { (26900.0, 4) }), null!, walls, outline, Array.Empty<UnclearRegion>());
        var model = new SurveyModel { Frame = new SurveyFrame(new Vec2(0, 0), 0), Levels = new[] { lv }, Skipped = Array.Empty<SkippedDrawing>(), Notes = new[] { "CHECK Ground Floor: split level" } };
        var log = BuildLog.From(model, "P");
        Assert.DoesNotContain(log.Items, e => e.Line != null && e.Line[2] == 10000 && e.Line[1] == 0 && e.Kind.StartsWith("wall"));
        Assert.Contains(log.Items, e => e.Kind == "wall-from-single-line" && e.Severity == 4);
        Assert.Contains(log.Items, e => e.Kind == "bridge");
        var level = Assert.Single(log.Items, e => e.Kind == "level");
        Assert.Equal("Low", level.Confidence);
        Assert.Equal(80, level.Severity);
        Assert.Equal("level", log.Items[0].Kind); // 80 m² of floor, unsure, outweighs a few metres of wall
        Assert.Contains(log.Items, e => e.Kind == "note" && e.Level == "Ground Floor");
    }
}
