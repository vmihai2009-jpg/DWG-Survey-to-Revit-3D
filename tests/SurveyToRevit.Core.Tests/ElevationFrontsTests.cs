using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class ElevationFrontsTests
{
    [Theory]
    [InlineData("N O R T H  E L E V A T I O N", 0.0)]
    [InlineData("SOUTH-EAST ELEVATION", 135.0)]
    [InlineData("West Facade", 270.0)]
    public void Titles_name_their_compass_side(string title, double bearing) => Assert.Equal(bearing, ElevationIndex.TitleBearing(title));

    [Theory]
    [InlineData("FRONT ELEVATION")]
    [InlineData("NORTH-SOUTH SECTION")]
    public void Titles_without_one_side_name_none(string title) => Assert.Null(ElevationIndex.TitleBearing(title));

    [Fact]
    public void A_ribbon_on_one_floor_does_not_join_the_columns_above()
    {
        Segment2 S(double x0, double y0, double x1, double y1) => new(new Vec2(x0, y0), new Vec2(x1, y1));
        var lines = new List<Segment2>
        {
            // ground floor: one shopfront line 2.9 m long under three windows' places
            S(0, 0, 2900, 0), S(0, 0, 0, 2000), S(2900, 0, 2900, 2000), S(0, 2000, 2900, 2000),
            // first floor: three 800 mm windows 1 m apart
            S(0, 3000, 800, 3000), S(0, 4500, 800, 4500), S(1050, 3000, 1850, 3000), S(1050, 4500, 1850, 4500),
            S(2100, 3000, 2900, 3000), S(2100, 4500, 2900, 4500),
        };
        var cols = ElevationIndex.RowColumns(lines);
        Assert.Contains(cols, c => Math.Abs(c - 400) < 1);
        Assert.Contains(cols, c => Math.Abs(c - 1450) < 1);
        Assert.Contains(cols, c => Math.Abs(c - 2500) < 1);
    }

    [Fact]
    public void Pieces_of_one_front_join_into_one_facade()
    {
        var f = new List<(string, Segment2)>
        {
            ("F1", new Segment2(new Vec2(0, 0), new Vec2(6000, 0))),
            ("F2", new Segment2(new Vec2(6000, 150), new Vec2(8500, 400))),   // a skewed short piece, 0.3 m out
            ("F3", new Segment2(new Vec2(9000, 0), new Vec2(16000, 0))),
            ("F4", new Segment2(new Vec2(16000, 0), new Vec2(16000, 9000))), // round the corner: not joined
        };
        var front = ElevationIndex.Fronts(f).Single(x => x.Members.Length > 1);
        Assert.Equal(new[] { "F1", "F2", "F3" }, front.Members.OrderBy(m => m));
        Assert.Equal(16000, front.Line.Length, 0);
    }

    [Fact]
    public void A_block_wall_an_elevation_front_shows_is_left_out()
    {
        var fronts = new[]
        {
            new ContextFront("F1", new Segment2(new Vec2(0, 0), new Vec2(4000, 0)), 0, 10000, new(), ""),
            new ContextFront("F1", new Segment2(new Vec2(4000, 0), new Vec2(8000, 0)), 0, 12000, new(), ""),
        };
        Assert.True(ElevationContext.Covered(new Segment2(new Vec2(500, 800), new Vec2(7500, 800)), fronts));
        Assert.False(ElevationContext.Covered(new Segment2(new Vec2(0, 0), new Vec2(0, 7000)), fronts));
        Assert.False(ElevationContext.Covered(new Segment2(new Vec2(0, 5000), new Vec2(8000, 5000)), fronts));
    }
}
