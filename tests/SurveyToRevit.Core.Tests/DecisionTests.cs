using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class DecisionTests
{
    static SurveyLevel Level(params WallCandidate[] walls) =>
        new("06", "Sixth Floor", 6, 47000, null, null!, new WallDetectionResult(walls, Array.Empty<Segment2>(), Array.Empty<double>(), 0, 0, Array.Empty<double>()), null, Array.Empty<UnclearRegion>());

    static WallCandidate W(double x1, double y1, double x2, double y2, double t = 100) =>
        new(new Segment2(new Vec2(x1, y1), new Vec2(x2, y2), "W"), t, t, Confidence.Low, "W", "W");

    const string File = """
    {
      "version": 1,
      "notWalls": [ { "level": "06", "line": [0, 20, 6000, 20], "reason": "mansard skin cut by the plan", "by": "mcp" },
                    { "level": "06", "line": [0, 5000, 3000, 5000] },
                    { "level": "Sixth Floor", "line": [9000, 9000, 9500, 9000] } ],
      "walls": [ { "level": "06", "line": [0, 8000, 4000, 8000], "thickness": 215, "reason": "missed: on a hidden layer" } ],
      "levels": [ { "level": "06", "aod": 47250, "source": "elevation" } ],
      "facadeTop": [ { "facade": "F29", "class": "Mansard", "evidence": "section slope 66" } ],
      "keep": [ 326461 ]
    }
    """;

    [Fact]
    public void Reads_the_file_case_insensitively()
    {
        var d = SurveyDecisions.Parse(File);
        Assert.Equal(3, d.NotWalls.Count);
        Assert.Equal(215, d.Walls[0].Thickness);
        Assert.Equal("Mansard", d.FacadeTop[0].Class);
        Assert.Equal(326461, d.Keep[0]);
    }

    [Fact]
    public void NotWalls_removes_the_wall_on_its_line_and_walls_adds_one()
    {
        var d = SurveyDecisions.Parse(File);
        var skin = W(0, 0, 6000, 0);            // the decision's line is its face, 20 mm off
        var dormer = W(0, 5000, 3000, 5000);     // holds a window: kept
        var other = W(0, 2000, 6000, 2000);      // nothing said about it
        var lv = Level(skin, dormer, other);
        var windowLines = new List<Segment2> { new(new Vec2(1000, 5050), new Vec2(2000, 5050), "Window") };
        var (walls, outs) = DecisionRules.ApplyWalls(d, lv, lv.Walls.Walls, windowLines);
        Assert.DoesNotContain(skin, walls);
        Assert.Contains(dormer, walls);
        Assert.Contains(other, walls);
        var added = Assert.Single(walls, w => w.FromDecision);
        Assert.Equal(215, added.TypeThickness);
        Assert.Contains(outs, o => o.Kind == "notWalls" && o.State == "applied");
        Assert.Contains(outs, o => o.Kind == "notWalls" && o.State == "contradicted");
        Assert.Contains(outs, o => o.Kind == "notWalls" && o.State == "stale"); // nothing at 9000, 9000
        Assert.Contains(outs, o => o.Kind == "walls" && o.State == "applied");
    }

    [Fact]
    public void A_wall_already_there_is_not_added_twice()
    {
        var d = SurveyDecisions.Parse("""{ "walls": [ { "level": "06", "line": [0, 0, 4000, 0] } ] }""");
        var lv = Level(W(0, 0, 4000, 0));
        var (walls, outs) = DecisionRules.ApplyWalls(d, lv, lv.Walls.Walls, new List<Segment2>());
        Assert.Single(walls);
        Assert.Equal("stale", Assert.Single(outs).State);
    }

    [Fact]
    public void Levels_take_the_decided_height_and_unknown_kinds_wait()
    {
        var d = SurveyDecisions.Parse(File);
        var lv = Level();
        var outs = DecisionRules.ApplyLevels(d, new List<SurveyLevel> { lv });
        Assert.Equal(47250, lv.ElevationMm);
        Assert.Equal("applied", Assert.Single(outs).State);
        var wait = DecisionRules.Waiting(d);
        Assert.Contains(wait, o => o.Kind == "facadeTop" && o.State == "waiting");
        Assert.Contains(wait, o => o.Kind == "keep");
    }

    [Fact]
    public void Without_the_file_nothing_changes()
    {
        var notes = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "dec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.Null(SurveyDecisions.Find(new[] { dir }, notes));
        System.IO.File.WriteAllText(Path.Combine(dir, SurveyDecisions.FileName), File);
        var d = SurveyDecisions.Find(new[] { dir }, notes);
        Assert.NotNull(d);
        Assert.EndsWith(SurveyDecisions.FileName, d!.Path);
        Assert.Empty(notes);
    }
}
