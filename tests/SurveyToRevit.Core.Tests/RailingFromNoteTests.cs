using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

public class RailingFromNoteTests
{
    [Fact]
    public void Pair_of_lines_beside_the_note_becomes_a_low_wall()
    {
        var lines = new List<Segment2>
        {
            new(new Vec2(-9882, 11487), new Vec2(-9847, -3557)),
            new(new Vec2(-9762, 11485), new Vec2(-9727, -3557)),
            new(new Vec2(-5000, 0), new Vec2(-5000, 9000)), // a wall line far off
        };
        var w = SurveyAnalyzer.RailingFrom(lines, new Vec2(-10400, 2900), 900);
        Assert.NotNull(w);
        Assert.Equal(900, w!.HeightMm);
        Assert.InRange(w.TypeThickness, 110, 130);
        Assert.InRange(w.Centerline.Mid.X, -9830, -9770);
        Assert.True(w.Centerline.Length > 14000);
    }

    [Fact]
    public void Nothing_when_no_pair_is_near()
    {
        var lines = new List<Segment2> { new(new Vec2(0, 0), new Vec2(0, 5000)) };
        Assert.Null(SurveyAnalyzer.RailingFrom(lines, new Vec2(200, 2500), 900));
    }
}
