using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

/// <summary>v0.3 plan reading: wall material map, façade runs, single-line walls.</summary>
public class PlanReadingTests
{
    static Segment2 S(double x1, double y1, double x2, double y2, string layer = "EF_25-Wall") => new(new Vec2(x1, y1), new Vec2(x2, y2), layer);

    static WallCandidate W(double x1, double y1, double x2, double y2, double t) =>
        new(S(x1, y1, x2, y2), t, t, Confidence.High, "", "");

    static readonly Polygon2 Box = new(new[] { new Vec2(0, 0), new Vec2(20000, 0), new Vec2(20000, 10000), new Vec2(0, 10000) });

    [Fact]
    public void Line_pair_with_room_between_is_not_a_wall_when_the_material_map_says_so()
    {
        // two lines 800 apart: thickness-valid, but the strip between is a corridor
        var lines = new[] { S(0, 0, 6000, 0), S(0, 800, 6000, 800) };
        Assert.Single(WallDetector.Detect(lines).Walls);
        Assert.Empty(WallDetector.Detect(lines, isMaterial: _ => false).Walls);
    }

    [Fact]
    public void Facade_piers_and_sill_panels_become_one_wall_of_the_masonry_thickness()
    {
        // south façade (outside below y=0): piers 600 thick, panels under windows 200 thick
        var walls = new List<WallCandidate>
        {
            W(0, 300, 3000, 300, 600), W(3000, 100, 4500, 100, 200),
            W(4500, 300, 8000, 300, 600), W(8000, 100, 9500, 100, 200),
            W(9500, 300, 14000, 300, 600),
            W(5000, 5000, 5000, 9000, 100), // internal partition stays
        };
        var result = FacadeWalls.Consolidate(walls, Box, Tolerances.Default);
        Assert.Equal(2, result.Count);
        var facade = Assert.Single(result, w => w.TypeThickness == 600);
        Assert.Equal(300, facade.Centerline.A.Y, 0);   // outer face stays on y = 0
        Assert.Equal(14000, facade.Centerline.Length, 0);
    }

    [Fact]
    public void Single_lines_inside_become_thin_walls_but_faces_of_found_walls_do_not()
    {
        var found = new List<WallCandidate> { W(0, 2000, 8000, 2000, 300) };
        var unpaired = new[]
        {
            S(1000, 6000, 9000, 6000),    // lone partition line → wall
            S(0, 1850, 8000, 1850),       // face of the 300 wall → not another wall
            S(30000, 5000, 35000, 5000),  // outside the building → ignored
            S(2000, 8000, 2500, 8000),    // too short → ignored
        };
        var result = WallDetector.FromSingleLines(unpaired, found, Box, Tolerances.Default);
        Assert.Equal(2, result.Count);
        var lone = Assert.Single(result, w => w.Confidence == Confidence.Low);
        Assert.Equal(100, lone.TypeThickness);
    }

    [Fact]
    public void Default_ceiling_is_2700_or_600_below_the_floor_above()
    {
        Assert.Equal(2700, SurveyAnalyzer.DefaultCeilingHeight(4000));
        Assert.Equal(2400, SurveyAnalyzer.DefaultCeilingHeight(3000));
        Assert.Equal(2100, SurveyAnalyzer.DefaultCeilingHeight(2200));
    }

    [Fact]
    public void Window_lines_in_a_wall_make_one_window_with_surveyed_sill_and_head()
    {
        var walls = new List<WallCandidate> { W(0, 300, 10000, 300, 600) };
        var glass = new[] { S(2000, 250, 3200, 250, "Ss_25_30_95-Window"), S(2000, 350, 3200, 350, "Ss_25_30_95-Window"),
                            S(2000, 250, 2000, 350, "Ss_25_30_95-Window"), S(3200, 250, 3200, 350, "Ss_25_30_95-Window") };
        var texts = new List<(Vec2, string)> { (new Vec2(2600, 900), "S 0.94"), (new Vec2(2600, 1200), "H 2.58") };
        var w = Assert.Single(WindowFinder.Find(glass, walls, texts, 3500));
        Assert.Equal(1200, w.WidthMm);
        Assert.Equal(940, w.SillMm);
        Assert.Equal(2580, w.HeadMm);
        Assert.True(w.HeightsFromSurvey);
    }

    [Fact]
    public void Window_without_texts_gets_default_heights_below_the_floor_above()
    {
        var walls = new List<WallCandidate> { W(0, 300, 10000, 300, 600) };
        var glass = new[] { S(5000, 300, 6000, 300, "Ss_25_30_95-Window") };
        var w = Assert.Single(WindowFinder.Find(glass, walls, new List<(Vec2, string)>(), 2300));
        Assert.Equal(WindowFinder.DefaultSillMm, w.SillMm);
        Assert.Equal(2150, w.HeadMm); // capped 150 below the next floor
    }

    [Fact]
    public void Door_gap_is_closed_and_a_hanging_end_runs_on_to_the_next_wall()
    {
        var walls = new List<WallCandidate>
        {
            W(0, 0, 4000, 0, 100), W(4900, 0, 9000, 0, 100),   // 900 door gap, no door drawn
            W(2000, 1200, 2000, 5000, 100),                     // stops 1200 short of the wall below
        };
        var closed = WallDetector.CloseGaps(walls, Tolerances.Default, Array.Empty<Segment2>());
        Assert.Equal(2, closed.Count);
        var along = Assert.Single(closed, w => w.Centerline.AngleDeg < 1);
        Assert.Equal(9000, along.Centerline.Length, 0);
        var cross = Assert.Single(closed, w => w.Centerline.AngleDeg > 89);
        Assert.Equal(0, Math.Min(cross.Centerline.A.Y, cross.Centerline.B.Y), 0);
    }

    [Fact]
    public void Door_swing_gives_a_door_in_the_wall_it_is_hinged_on()
    {
        var walls = new List<WallCandidate> { W(0, 0, 10000, 0, 100) };
        // leaf hinged at x=2000, drawn open at 60°, 900 long, swinging towards +x
        var leaf = S(2000, 0, 2000 + 450, 779, "Door");
        var d = Assert.Single(DoorFinder.Find(new[] { leaf }, walls));
        Assert.Equal(900, d.WidthMm);
        Assert.Equal(2450, d.Center.X, 0);
        Assert.False(d.Double);
    }

    [Fact]
    public void Two_leaves_meeting_in_one_opening_are_a_double_door()
    {
        var walls = new List<WallCandidate> { W(0, 0, 10000, 0, 200) };
        var left = S(3000, 0, 3000, 800, "Door");   // square to the wall: side from where the wall is longer
        var right = S(4600, 0, 4600 - 400, 693, "Door");
        var d = Assert.Single(DoorFinder.Find(new[] { left, right }, walls));
        Assert.True(d.Double);
        Assert.Equal(1600, d.WidthMm);
        Assert.Equal(3800, d.Center.X, 0);
    }

    [Fact]
    public void Lines_along_the_wall_and_doors_on_windows_are_not_doors()
    {
        var walls = new List<WallCandidate> { W(0, 0, 10000, 0, 200) };
        Assert.Empty(DoorFinder.Find(new[] { S(1000, 0, 1900, 0, "Door") }, walls));
        var window = new WindowCandidate(0, new Vec2(2450, 0), 1200, 900, 2100, false);
        Assert.Empty(DoorFinder.Find(new[] { S(2000, 0, 2450, 779, "Door") }, walls, new[] { window }));
    }

    [Fact]
    public void Window_is_hosted_in_the_facade_not_the_lining_in_front_of_it()
    {
        var walls = new List<WallCandidate> { W(0, 0, 10000, 0, 450), W(0, 300, 10000, 300, 100) };
        var win = Assert.Single(WindowFinder.Find(new[] { S(4000, 150, 5200, 150, "Window") }, walls, Array.Empty<(Vec2, string)>(), 3500));
        Assert.Equal(0, win.HostIndex);
    }

    [Fact]
    public void Bulged_polyline_piece_is_the_right_arc()
    {
        // quarter circle anticlockwise from (10,0) to (0,10) round the origin: bulge = tan(90°/4)
        var a = Arc2.FromBulge(new Vec2(10000, 0), new Vec2(0, 10000), Math.Tan(Math.PI / 8));
        Assert.Equal(0, a.Center.X, 3); Assert.Equal(0, a.Center.Y, 3);
        Assert.Equal(10000, a.Radius, 3);
        Assert.Equal(Math.PI / 2, a.Sweep, 6);
        var cw = Arc2.FromBulge(new Vec2(0, 10000), new Vec2(10000, 0), -Math.Tan(Math.PI / 8));
        Assert.Equal(0, cw.Center.X, 3); Assert.Equal(Math.PI / 2, cw.Sweep, 6);
    }

    [Fact]
    public void Two_arcs_round_one_centre_make_one_curved_wall_replacing_the_chords()
    {
        var outer = new Arc2(new Vec2(0, 0), 20450, 0.2, 0.8);
        var inner = new Arc2(new Vec2(0, 0), 20000, 0.2, 0.8);
        var chord = W(20225 * Math.Cos(0.3), 20225 * Math.Sin(0.3), 20225 * Math.Cos(0.4), 20225 * Math.Sin(0.4), 450);
        var walls = CurvedWalls.Add(new List<WallCandidate> { chord }, new[] { outer, inner }, Tolerances.Default);
        var w = Assert.Single(walls);
        Assert.NotNull(w.Arc);
        Assert.Equal(450, w.TypeThickness);
        Assert.InRange(w.Arc!.Radius, 20215, 20235); // to the 10 mm
        Assert.Equal(0.6, w.Arc.Sweep, 3);
    }

    [Fact]
    public void Curved_facade_with_straight_recesses_is_measured_from_the_material()
    {
        // outer face an arc; behind it 600 mm of wall material (inside the outline)
        var face = new Arc2(new Vec2(0, 0), 20000, 0.2, 0.8);
        bool material(Vec2 p) { double r = p.DistanceTo(new Vec2(0, 0)); return r < 20000 && r > 19400; }
        var outline = new Polygon2(new[] { new Vec2(0, 0), new Vec2(20000, 0), new Vec2(20000 * Math.Cos(0.5), 20000 * Math.Sin(0.5)), new Vec2(0, 20000) });
        var walls = CurvedWalls.Add(new List<WallCandidate>(), new[] { face }, Tolerances.Default, material, outline);
        var w = Assert.Single(walls);
        Assert.Equal(600, w.TypeThickness);
        Assert.Equal(19700, w.Arc!.Radius, 0);
    }

    [Fact]
    public void Facets_of_a_bend_are_bridged_across_window_gaps()
    {
        var a = W(0, 0, 5000, 0, 450);
        var b = W(6500, 260, 11000, 1050, 450); // turned 10°, 1.5 m on
        var window = new[] { S(5100, 50, 6400, 230, "Window") };
        var walls = WallDetector.BridgeBends(new List<WallCandidate> { a, b }, Tolerances.Default, window);
        Assert.Equal(3, walls.Count);
        var bridge = walls[2];
        Assert.Equal(5000, bridge.Centerline.A.X, 0);
        Assert.Equal(6500, bridge.Centerline.B.X, 0);
    }

    [Fact]
    public void Window_on_a_curved_wall_sits_on_the_curve()
    {
        var arc = new Arc2(new Vec2(0, 0), 20000, 0.2, 0.8);
        var wall = new WallCandidate(new Segment2(arc.PointAt(0.2), arc.PointAt(0.8)), 450, 450, Confidence.Medium, "", "") { Arc = arc };
        var p = arc.PointAt(0.5);
        var tangent = new Vec2(-Math.Sin(0.5), Math.Cos(0.5));
        var win = Assert.Single(WindowFinder.Find(new[] { new Segment2(p - tangent * 600, p + tangent * 600, "Window") }, new[] { wall }, Array.Empty<(Vec2, string)>(), 3500));
        Assert.Equal(20000, win.Center.DistanceTo(new Vec2(0, 0)), 0);
        Assert.InRange(win.WidthMm, 1180, 1220);
    }
}
