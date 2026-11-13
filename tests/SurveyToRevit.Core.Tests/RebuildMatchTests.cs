using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class RebuildMatchTests
{
    static Segment2 S(double x1, double y1, double x2, double y2) => new(new Vec2(x1, y1), new Vec2(x2, y2));

    [Fact]
    public void Same_wall_drawn_the_other_way_is_kept_a_moved_or_thicker_one_is_not()
    {
        var old = new List<(long, Segment2, double)> { (10, S(0, 0, 5000, 0), 215), (11, S(0, 3000, 5000, 3000), 100), (12, S(0, 6000, 5000, 6000), 100) };
        var fresh = new List<(int, Segment2, double)> { (0, S(5000, 8, 0, 8), 215), (1, S(0, 3060, 5000, 3060), 100), (2, S(0, 6000, 5000, 6000), 215) };
        var m = RebuildMatch.Walls(old, fresh);
        Assert.Equal(10, m[0]);
        Assert.False(m.ContainsKey(1)); // moved 60 mm
        Assert.False(m.ContainsKey(2)); // now 215 thick
    }

    [Fact]
    public void Openings_match_on_the_same_host_closest_first_and_once()
    {
        var old = new List<(long, long, Vec2, double)> { (20, 10, new Vec2(1000, 0), 900), (21, 10, new Vec2(3000, 0), 1200) };
        var fresh = new List<(int, long, Vec2, double)> { (0, 10, new Vec2(1040, 0), 900), (1, 10, new Vec2(1080, 0), 900), (2, 11, new Vec2(3000, 0), 1200), (3, 10, new Vec2(3000, 0), 1400) };
        var m = RebuildMatch.Openings(old, fresh);
        Assert.Equal(20, m[0]);
        Assert.False(m.ContainsKey(1)); // 20 already taken
        Assert.False(m.ContainsKey(2)); // another wall
        Assert.False(m.ContainsKey(3)); // 200 mm wider
    }
}
