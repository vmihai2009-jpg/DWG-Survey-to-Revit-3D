using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class ElevationIndexTests
{
    static SurveyLevel L(string code, double z, Polygon2 outline, params WindowCandidate[] windows) =>
        new(code, "Floor " + code, double.Parse(code), z, null, null!, new WallDetectionResult(Array.Empty<WallCandidate>(), Array.Empty<Segment2>(), Array.Empty<double>(), 0, 0, Array.Empty<double>()), outline, Array.Empty<UnclearRegion>())
        { Windows = windows };

    static WindowCandidate Win(double x, double y) => new(0, new Vec2(x, y), 1200, 900, 2400, false);

    // 20 x 12 m; street front y = 0 has windows at x = 2, 5, 9.5, 15 m (irregular, so only one way round fits); rear at 4, 10, 16 m
    static List<SurveyLevel> Building()
    {
        var box = new Polygon2(new[] { new Vec2(0, 0), new Vec2(20000, 0), new Vec2(20000, 12000), new Vec2(0, 12000) });
        var front = new[] { 2000.0, 5000, 9500, 15000 }; var rear = new[] { 4000.0, 10000, 16000 };
        WindowCandidate[] Floor() => front.Select(x => Win(x, 150)).Concat(rear.Select(x => Win(x, 11850))).ToArray();
        return new List<SurveyLevel> { L("00", 30000, box, Floor()), L("01", 33500, box, Floor()) };
    }

    [Fact]
    public void Window_lines_merge_into_columns() =>
        Assert.Equal(new[] { 2000.0, 5000 }, ElevationIndex.Columns(new[] { (1400.0, 2600.0), (1400.0, 2600.0), (4400.0, 5600.0) }));

    [Fact]
    public void A_mirrored_street_elevation_is_matched_to_the_street_facade_and_fitted_in_two_parts()
    {
        // drawn seen from the street, so the sheet runs the other way to the outline's edge; sheet x = 50000 - t
        var cols = new List<double> { 2000, 5000, 9500, 15000 }.Select(t => 50000 - t).ToList();
        var d = new ElevationDrawing { File = "E.dwg", Title = "FRONT ELEVATION", Box = new double[] { 30000, 0, 50000, 20000 }, Columns = cols };
        // marks: the main block at z = y + 30000; a part at x > 46000 drawn 1 m lower (z = y + 31000)
        var marks = new List<(double, double, double)>
        {
            (31000, 0, 30000), (31000, 3500, 33500), (35000, 7000, 37000), (40000, 3500, 33500),
            (47000, 0, 31000), (47500, 2000, 33000), (48000, 4000, 35000),
        };
        var res = ElevationIndex.Build(new[] { (d, marks) }, Building()).Single();
        Assert.Equal("F1", res.Facade);
        Assert.True(res.Mirrored);
        Assert.Equal(1.0, res.Score, 2);
        Assert.Equal(2, res.Parts.Count);
        Assert.Equal(30000, res.Parts[0].B, 0);
        Assert.Equal(31000, res.Parts[1].B, 0);
    }

    // a classical front: 10 irregular window columns, only 7 of them drawn where the plan has them (the rest differ) among 14 drawn
    static List<SurveyLevel> ClassicalBuilding()
    {
        var box = new Polygon2(new[] { new Vec2(0, 0), new Vec2(40000, 0), new Vec2(40000, 12000), new Vec2(0, 12000) });
        var front = new[] { 2000.0, 4300, 7900, 11000, 14600, 18100, 21500, 25800, 30200, 36000 }; var rear = new[] { 4000.0, 10000, 16000 };
        WindowCandidate[] Floor() => front.Select(x => Win(x, 150)).Concat(rear.Select(x => Win(x, 11850))).ToArray();
        return new List<SurveyLevel> { L("00", 30000, box, Floor()) };
    }

    [Fact]
    public void A_classical_front_with_only_some_columns_lining_up_is_matched_by_the_looser_second_pass()
    {
        var drawn = new List<double> { 2000, 4300, 7900, 11000, 14600, 18100, 21500, 3000, 6100, 9300, 12700, 16000, 19800, 23300 };
        var d = new ElevationDrawing { File = "E.dwg", Title = "ELEVATION 3", Box = new double[] { 0, 0, 40000, 20000 }, Columns = drawn };
        var res = ElevationIndex.Build(new[] { (d, new List<(double, double, double)>()) }, ClassicalBuilding()).Single();
        Assert.Equal("F1", res.Facade);
        Assert.InRange(res.Score, 0.3, 0.69);
        Assert.Contains("looser", res.Note);
    }

    [Fact]
    public void A_few_matching_columns_are_not_taken_even_loosely()
    {
        var d = new ElevationDrawing { File = "E.dwg", Title = "ELEVATION 3", Box = new double[] { 0, 0, 40000, 20000 }, Columns = new List<double> { 2000, 4300, 7900, 1500, 6000, 9000, 12000, 17000 } };
        var res = ElevationIndex.Build(new[] { (d, new List<(double, double, double)>()) }, ClassicalBuilding()).Single();
        Assert.Null(res.Facade);
    }

    [Fact]
    public void An_elevation_decision_with_a_facade_line_assigns_an_unmatched_drawing()
    {
        var d = new ElevationDrawing { File = "000002-ELEV.dwg", Title = "ELEVATION 1", Box = new double[] { 100, 0, 20000, 20000 }, Columns = new List<double> { 2000, 5000, 9500, 15000 } };
        var res = ElevationIndex.Build(new[] { (d, new List<(double, double, double)>()) }, Building());
        d.Facade = null; d.FacadeLine = null;
        var dec = new SurveyDecisions { Elevations = { new ElevationDecision { Sheet = "ELEV", XRange = new double[] { 0, 30000 }, Facade = "F99", FacadeLine = new double[] { 0, 0, 20000, 0 } } } };
        var outcomes = ElevationIndex.ApplyDecisions(res, dec, Building());
        Assert.Single(outcomes, o => o.State == "applied");
        Assert.NotNull(res.Single().Facade);
        Assert.Contains("by decision", res.Single().Note);
        Assert.Empty(DecisionRules.Waiting(dec).Where(o => o.Kind == "elevations"));
    }

    [Fact]
    public void A_drawing_whose_windows_match_no_facade_is_left_unmatched()
    {
        var d = new ElevationDrawing { File = "E.dwg", Title = "SIDE ELEVATION", Box = new double[] { 0, 0, 10000, 10000 }, Columns = new List<double> { 1000, 1700, 8000 } };
        var res = ElevationIndex.Build(new[] { (d, new List<(double, double, double)>()) }, Building()).Single();
        Assert.Null(res.Facade);
        Assert.Contains("unmatched", res.Note);
    }
}
