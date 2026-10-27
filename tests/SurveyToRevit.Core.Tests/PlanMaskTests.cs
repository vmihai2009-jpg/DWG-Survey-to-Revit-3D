using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Core.Tests;

public class PlanMaskTests
{
    static IReadOnlyList<Vec2> Box(double x0, double y0, double x1, double y1) =>
        new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) };

    [Fact]
    public void A_masking_region_cuts_out_the_part_of_a_line_it_covers()
    {
        var line = new[] { new Vec2(0, 5), new Vec2(10, 5) };
        var masks = new[] { new[] { Box(3, 0, 6, 10) } };
        var pieces = PlanMask.Visible(line, masks);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(3, pieces[0][^1].X, 6);
        Assert.Equal(6, pieces[1][0].X, 6);
    }

    [Fact]
    public void A_hole_in_the_region_stays_visible_and_the_crop_trims_the_ends()
    {
        var line = new[] { new Vec2(0, 5), new Vec2(10, 5) };
        var masks = new[] { new[] { Box(2, 0, 8, 10), Box(4, 0.5, 6, 9.5) } };
        var crop = new[] { Box(1, 0, 9, 10) };
        var pieces = PlanMask.Visible(line, masks, crop);
        Assert.Equal(3, pieces.Count);           // 1–2, the hole 4–6, 8–9
        Assert.Equal(1, pieces[0][0].X, 6);
        Assert.Equal(4, pieces[1][0].X, 6);
        Assert.Equal(9, pieces[2][^1].X, 6);
    }

    [Fact]
    public void A_line_wholly_under_a_region_is_gone_and_one_outside_is_kept_whole()
    {
        var masks = new[] { new[] { Box(0, 0, 10, 10) } };
        Assert.Empty(PlanMask.Visible(new[] { new Vec2(1, 1), new Vec2(2, 2), new Vec2(3, 1) }, masks));
        var kept = Assert.Single(PlanMask.Visible(new[] { new Vec2(20, 1), new Vec2(21, 2), new Vec2(22, 1) }, masks));
        Assert.Equal(3, kept.Count);
    }
}
