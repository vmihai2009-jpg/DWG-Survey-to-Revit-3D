using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

public class RoofWallTests
{
    static readonly Polygon2 Roof = new(new[] { new Vec2(0, 0), new Vec2(10000, 0), new Vec2(10000, 8000), new Vec2(0, 8000) });
    static Segment2 S(double x1, double y1, double x2, double y2) => new(new Vec2(x1, y1), new Vec2(x2, y2));

    [Fact]
    public void PartitionUnderTheRoofIsInnerEvenWhereItMeetsTheOuterWall() =>
        Assert.Equal(RoofWallPlace.Inner, RoofWalls.Place(S(5000, 0, 5000, 8000), 100, Roof));

    [Fact]
    public void FacadeOnTheOutlineIsEdge() =>
        Assert.Equal(RoofWallPlace.Edge, RoofWalls.Place(S(0, 60, 10000, 60), 215, Roof));

    [Fact]
    public void WallRunningPastTheRoofIsCrossing() =>
        Assert.Equal(RoofWallPlace.Crossing, RoofWalls.Place(S(5000, 4000, 15000, 4000), 100, Roof));

    [Fact]
    public void WallElsewhereIsOutside() =>
        Assert.Equal(RoofWallPlace.Outside, RoofWalls.Place(S(12000, 1000, 12000, 7000), 100, Roof));
}
