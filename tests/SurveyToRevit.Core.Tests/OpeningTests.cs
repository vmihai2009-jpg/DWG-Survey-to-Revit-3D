namespace SurveyToRevit.Core.Tests;

public class OpeningTests
{
    static Segment2 S(double x1, double y1, double x2, double y2, string layer = "EF_25-Wall") => new(new Vec2(x1, y1), new Vec2(x2, y2), layer);
    static WallCandidate W(double x1, double y1, double x2, double y2, double t) => new(S(x1, y1, x2, y2), t, t, Confidence.High, "", "");

    [Fact]
    public void A_window_running_past_its_wall_end_is_cut_into_the_next_wall_too()
    {
        var walls = new List<WallCandidate> { W(0, 0, 3000, 0, 300), W(3000, 0, 8000, 0, 300) };
        // 1200 wide, centred 200 short of the joint: 400 of it lies in the next wall
        var cut = Assert.Single(OpeningCuts.Across(walls, 0, new Vec2(2800, 0), 1200));
        Assert.Equal(1, cut.Wall);
        Assert.Equal(400, cut.WidthMm, 0);
        Assert.Equal(3200, cut.Center.X, 0);
    }

    [Fact]
    public void A_short_wall_filling_a_window_reveal_is_left_out_but_a_partition_is_kept()
    {
        var walls = new List<WallCandidate>
        {
            W(0, 0, 10000, 0, 600),       // facade
            W(2000, -300, 2000, 300, 900), // the reveal read as a wall across the facade: faces are the jambs
            W(6000, 300, 6000, 5000, 100), // a partition meeting the facade beside the second window
        };
        var drop = OpeningCuts.FillingOpenings(walls, new[] { (0, new Vec2(2000, 0), 900.0), (0, new Vec2(6600, 0), 900.0) });
        Assert.Equal(new[] { 1 }, drop);
    }

    [Fact]
    public void A_door_keeps_the_hinge_and_swing_side_of_its_symbol()
    {
        var walls = new List<WallCandidate> { W(0, 0, 10000, 0, 100) };
        // hinged at x=2000, open square into +y, swing back to (2900, 0)
        var leaf = S(2000, 0, 2000, 900, "Door");
        var arc = new Arc2(new Vec2(2000, 0), 900, 0, Math.PI / 2, "Door");
        var d = Assert.Single(DoorFinder.Find(new[] { leaf }, walls, arcs: new[] { arc }));
        Assert.Equal(2450, d.Center.X, 0);
        Assert.Equal(2000, d.Hinge!.Value.X, 0);
        Assert.True(d.Swing!.Value.Y > 400);

        // the same door drawn the other way round: hinge at 2900, opening into -y
        var leaf2 = S(2900, 0, 2900, -900, "Door");
        var arc2 = new Arc2(new Vec2(2900, 0), 900, -Math.PI / 2, 0, "Door");
        var d2 = Assert.Single(DoorFinder.Find(new[] { leaf2 }, walls, arcs: new[] { arc2 }));
        Assert.Equal(2900, d2.Hinge!.Value.X, 0);
        Assert.True(d2.Swing!.Value.Y < -400);
    }

    [Fact]
    public void Double_doors_without_swings_still_know_their_opening_side()
    {
        var walls = new List<WallCandidate> { W(0, 0, 10000, 0, 200) };
        var left = S(3000, 0, 3000, 800, "Door");
        var right = S(4600, 0, 4600 - 400, 693, "Door");
        var d = Assert.Single(DoorFinder.Find(new[] { left, right }, walls));
        Assert.True(d.Double);
        Assert.Null(d.Hinge);
        Assert.True(d.Swing!.Value.Y > 0);
    }
}
