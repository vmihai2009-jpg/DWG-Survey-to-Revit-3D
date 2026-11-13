using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class FacadeTopTests
{
    static WallCandidate W(double x1, double y1, double x2, double y2, double t) =>
        new(new Segment2(new Vec2(x1, y1), new Vec2(x2, y2), "W"), t, t, Confidence.Medium, "W", "W");

    static SurveyLevel L(string code, double z, Polygon2 outline, params WallCandidate[] walls) =>
        new(code, "Floor " + code, double.Parse(code), z, null, null!, new WallDetectionResult(walls, Array.Empty<Segment2>(), Array.Empty<double>(), 0, 0, Array.Empty<double>()), outline, Array.Empty<UnclearRegion>());

    // a 20 x 12 m block; the street front is y = 0 (the stretch from (0,0) to (20000,0))
    static readonly Polygon2 Box = new(new[] { new Vec2(0, 0), new Vec2(20000, 0), new Vec2(20000, 12000), new Vec2(0, 12000) });

    static (List<SurveyLevel> Levels, SurveyLevel Top) Building(params WallCandidate[] topWalls)
    {
        var below = L("05", 40000, Box, W(0, 150, 20000, 150, 300), W(150, 0, 150, 12000, 300));
        var top = L("06", 43500, Box, topWalls);
        return (new List<SurveyLevel> { below, top }, top);
    }

    static FacadeTopCall Street(List<FacadeTopCall> calls) => calls.Single(c => c.Line[1] == 0 && c.Line[3] == 0);

    [Fact]
    public void Thick_set_back_walls_are_a_set_back_storey_from_the_plan_alone()
    {
        var (levels, _) = Building(W(0, 1650, 20000, 1650, 300));
        var c = Street(FacadeTops.Classify(levels, new List<Segment2>()));
        Assert.Equal("SetBackStorey", c.Class);
        Assert.Equal("Low", c.Confidence);
        Assert.Equal(1500, c.SetbackMm);
    }

    [Fact]
    public void Thin_windowless_set_back_lines_stay_walls_and_ask_for_a_section()
    {
        var (levels, top) = Building(W(0, 1100, 20000, 1100, 100));
        var calls = FacadeTops.Classify(levels, new List<Segment2>());
        var c = Street(calls);
        Assert.Equal("Unknown", c.Class);
        Assert.Contains("section", c.Settle);
        Assert.Equal(top.Walls.Walls.Count, FacadeTops.LeaveOutSkins(top, calls, new List<Segment2>()).Count); // nothing removed from the plan alone
    }

    [Fact]
    public void A_steep_section_makes_it_a_mansard_and_its_skin_lines_go_but_dormer_fronts_stay()
    {
        var skin = W(0, 1100, 9000, 1100, 100);
        var dormer = W(10000, 1100, 14000, 1100, 100);
        var (levels, top) = Building(skin, dormer);
        var profile = new StretchProfile(new Segment2(new Vec2(5000, 0), new Vec2(5000, 0.1)), new[] { (0.0, 40000.0), (0.0, 43500.0), (-1360.0, 46500.0), (-1400.0, 46520.0) }, "Section-A");
        // the profile's line runs across the facade; give it along the stretch instead
        profile = profile with { Line = new Segment2(new Vec2(4000, 0), new Vec2(6000, 0)) };
        var windows = new List<Segment2> { new(new Vec2(11000, 1150), new Vec2(12000, 1150), "Window") };
        var calls = FacadeTops.Classify(levels, windows, profiles: new[] { profile });
        var c = Street(calls);
        Assert.Equal("Mansard", c.Class);
        Assert.Equal("High", c.Confidence);
        Assert.InRange(c.SlopeDeg!.Value, 65, 67);
        var kept = FacadeTops.LeaveOutSkins(top, calls, windows);
        Assert.DoesNotContain(skin, kept);
        Assert.Contains(dormer, kept);
        Assert.Single(c.SkinLinesLeftOut);
        Assert.Single(c.DormerFrontsKept);
    }

    [Fact]
    public void A_decision_wins_and_a_flush_facade_under_a_flat_roof_is_a_parapet()
    {
        var (levels, _) = Building(W(0, 150, 20000, 150, 300), W(150, 0, 150, 12000, 300));
        var roofs = new[] { new RoofCandidate(new Polygon2(new[] { new Vec2(300, 300), new Vec2(19700, 300), new Vec2(19700, 11700), new Vec2(300, 11700) }), 46800, "roof plan") };
        var calls = FacadeTops.Classify(levels, new List<Segment2>(), roofs);
        Assert.Equal("FlatParapet", Street(calls).Class);
        var d = SurveyDecisions.Parse("""{ "facadeTop": [ { "facade": "F29", "class": "Mansard", "line": [0, 0, 20000, 0], "evidence": "photo" } ] }""");
        var c2 = Street(FacadeTops.Classify(levels, new List<Segment2>(), roofs, decisions: d));
        Assert.Equal("Mansard", c2.Class);
        Assert.Equal("High", c2.Confidence);
    }

    [Fact]
    public void A_building_with_one_storey_or_no_outline_gets_no_calls()
    {
        Assert.Empty(FacadeTops.Classify(new List<SurveyLevel> { L("00", 0, Box, W(0, 0, 1000, 0, 300)) }, new List<Segment2>()));
        Assert.Contains("\"stretches\"", FacadeTops.ToJson(new List<FacadeTopCall>()));
    }
}
