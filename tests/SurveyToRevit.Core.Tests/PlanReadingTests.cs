using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

/// <summary>v0.3 plan reading: wall material map, façade runs, single-line walls.</summary>
public class PlanReadingTests
{
    static Segment2 S(double x1, double y1, double x2, double y2, string layer = "EF_25-Wall") => new(new Vec2(x1, y1), new Vec2(x2, y2), layer);

    static WallCandidate W(double x1, double y1, double x2, double y2, double t) =>
        new(S(x1, y1, x2, y2), t, t, Confidence.High, "", "");

    static readonly Polygon2 Box = new(new[] { new Vec2(0, 0), new Vec2(20000, 0), new Vec2(20000, 10000), new Vec2(0, 10000) });

    [Fact]
    public void Line_pair_with_room_between_is_not_a_wall_when_the_material_map_says_so()
    {
        // two lines 800 apart: thickness-valid, but the strip between is a corridor
        var lines = new[] { S(0, 0, 6000, 0), S(0, 800, 6000, 800) };
        Assert.Single(WallDetector.Detect(lines).Walls);
        Assert.Empty(WallDetector.Detect(lines, isMaterial: _ => false).Walls);
    }

    [Fact]
    public void Facade_piers_and_sill_panels_become_one_wall_of_the_masonry_thickness()
    {
        // south façade (outside below y=0): piers 600 thick, panels under windows 200 thick
        var walls = new List<WallCandidate>
        {
            W(0, 300, 3000, 300, 600), W(3000, 100, 4500, 100, 200),
            W(4500, 300, 8000, 300, 600), W(8000, 100, 9500, 100, 200),
            W(9500, 300, 14000, 300, 600),
            W(5000, 5000, 5000, 9000, 100), // internal partition stays
        };
        var result = FacadeWalls.Consolidate(walls, Box, Tolerances.Default);
        Assert.Equal(2, result.Count);
        var facade = Assert.Single(result, w => w.TypeThickness == 600);
        Assert.Equal(300, facade.Centerline.A.Y, 0);   // outer face stays on y = 0
        Assert.Equal(14000, facade.Centerline.Length, 0);
    }

    [Fact]
    public void Single_lines_inside_become_thin_walls_but_faces_of_found_walls_do_not()
    {
        var found = new List<WallCandidate> { W(0, 2000, 8000, 2000, 300) };
        var unpaired = new[]
        {
            S(1000, 6000, 9000, 6000),    // lone partition line → wall
            S(0, 1850, 8000, 1850),       // face of the 300 wall → not another wall
            S(30000, 5000, 35000, 5000),  // outside the building → ignored
            S(2000, 8000, 2500, 8000),    // too short → ignored
        };
        var result = WallDetector.FromSingleLines(unpaired, found, Box, Tolerances.Default);
        Assert.Equal(2, result.Count);
        var lone = Assert.Single(result, w => w.Confidence == Confidence.Low);
        Assert.Equal(100, lone.TypeThickness);
    }

    [Fact]
    public void Default_ceiling_is_2700_or_600_below_the_floor_above()
    {
        Assert.Equal(2700, SurveyAnalyzer.DefaultCeilingHeight(4000));
        Assert.Equal(2400, SurveyAnalyzer.DefaultCeilingHeight(3000));
        Assert.Equal(2100, SurveyAnalyzer.DefaultCeilingHeight(2200));
    }
}
