using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

public class PitchedRoofTests
{
    static readonly Polygon2 Box = new(new[] { new Vec2(0, 0), new Vec2(16000, 0), new Vec2(16000, 10000), new Vec2(0, 10000) });

    [Fact]
    public void Eaves_and_ridge_from_agreeing_heights()
    {
        // eaves 44.32 twice, ridge 44.70 twice, one stray chimney top
        var r = RoofsAndDetails.WithHeights(Box, new List<double> { 44320, 44330, 44700, 44710, 46000 }, "t");
        Assert.Equal(44320, r.EavesMm);
        Assert.Equal(44710, r.ElevationMm);
    }

    [Fact]
    public void Small_rise_is_flat()
    {
        var r = RoofsAndDetails.WithHeights(Box, new List<double> { 40860, 40900, 40950 }, "t");
        Assert.Null(r.EavesMm);
    }

    [Fact]
    public void Box_snaps_onto_wall_faces_below()
    {
        var walls = new List<WallCandidate>
        {
            new(new Segment2(new Vec2(-500, -300), new Vec2(16500, -300)), 300, 300, Confidence.High, "", ""),
            new(new Segment2(new Vec2(-500, 10600), new Vec2(16500, 10600)), 300, 300, Confidence.High, "", ""),
        };
        var spots = new List<(Vec2, double)> { (new Vec2(5000, 10700), 44320), (new Vec2(9000, 10700), 44330), (new Vec2(6000, 5000), 44700), (new Vec2(7000, 5000), 44710) };
        var r = RoofsAndDetails.OnWalls(new RoofCandidate(Box, 44700, "roof plan x") { EavesMm = 44400, Heights = spots }, walls);
        Assert.Equal(-450, r.Outline.Vertices.Min(v => v.Y), 1);
        Assert.Equal(10750, r.Outline.Vertices.Max(v => v.Y), 1);
        Assert.Equal(44320, r.EavesMm);
    }
}
