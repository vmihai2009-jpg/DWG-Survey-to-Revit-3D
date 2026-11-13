using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class BuiltCheckTests
{
    static Segment2 S(double x1, double y1, double x2, double y2) => new(new Vec2(x1, y1), new Vec2(x2, y2));

    [Fact]
    public void Two_walls_on_one_line_are_a_duplicate_a_neighbour_is_not()
    {
        var walls = new List<(long, Segment2)>
        {
            (1, S(0, 0, 5000, 0)),
            (2, S(1000, 10, 4000, 10)),     // on it, 10 mm off: duplicate
            (3, S(0, 300, 5000, 300)),      // a parallel wall 300 mm away: not
            (4, S(4800, 0, 9000, 0)),       // continues it, overlap 200 mm only: not
            (5, S(2000, -2000, 2000, 2000)), // crosses it: not
        };
        var d = BuiltChecks.DuplicateWalls(walls);
        var p = Assert.Single(d);
        Assert.Equal((1L, 2L), (p.A, p.B));
        Assert.Equal(3000, p.OverlapMm);
    }

    [Fact]
    public void Findings_join_the_build_log()
    {
        var log = new BuildLog();
        foreach (var e in BuiltChecks.AsLog(new[] { new BuiltFinding("roof-through-wall", "Sixth Floor", new long[] { 7, 8 }, new double[] { 1, 2 }, "the roof cuts wall 8", "attach the wall's top to the roof", 6, "m of wall") })) log.Add(e);
        Assert.Equal("built: roof-through-wall", log.Items[0].Kind);
        Assert.Equal(2.4, log.Items[0].Score);
    }
}
