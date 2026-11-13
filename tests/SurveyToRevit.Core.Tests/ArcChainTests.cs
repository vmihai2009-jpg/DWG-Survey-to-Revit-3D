using SurveyToRevit.Core;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class ArcChainTests
{
    static Vec2 P(double deg) => new(5000 * Math.Sin(deg * Math.PI / 180), 5000 - 5000 * Math.Cos(deg * Math.PI / 180));

    static List<Segment2> Plan()
    {
        var l = new List<Segment2> { new(new Vec2(-6000, 0), new Vec2(0, 0)) };
        for (int i = 0; i < 20; i++) l.Add(i % 4 == 1 ? new(P((i + 1) * 4.5), P(i * 4.5)) : new(P(i * 4.5), P((i + 1) * 4.5)));
        l.Add(new(new Vec2(5000, 5000), new Vec2(5000, 12000)));
        for (int i = 0; i < 8; i++) l.Add(new(new Vec2(20000 + i * 300, i % 2 * 200), new Vec2(20000 + (i + 1) * 300, (i + 1) % 2 * 200)));
        for (int i = 0; i < 8; i++) l.Add(new(new Vec2(30000 + i * 500, 0), new Vec2(30000 + (i + 1) * 500, 0)));
        return l;
    }

    [Fact]
    public void A_curve_drawn_as_short_lines_is_one_arc()
    {
        var arcs = ArcChains.Find(Plan());
        var a = Assert.Single(arcs);
        Assert.InRange(a.Radius, 4998, 5002);
        Assert.InRange(a.Center.DistanceTo(new Vec2(0, 5000)), 0, 2);
        Assert.InRange(a.Sweep * 180 / Math.PI, 89.5, 90.5);
    }

    [Fact]
    public void A_wall_meeting_the_curve_splits_it()
    {
        var l = Plan();
        l.Add(new(P(45), new Vec2(0, 9000)));
        var arcs = ArcChains.Find(l);
        Assert.Equal(2, arcs.Count);
        Assert.All(arcs, a => Assert.InRange(a.Sweep * 180 / Math.PI, 44.5, 45.5));
    }
}
