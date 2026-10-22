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
    public void Single_line_glazed_screen_is_a_curtain_wall_cut_by_its_door()
    {
        var walls = new List<WallCandidate> { W(0, 0, 0, 5000, 200) };
        var screen = Assert.Single(GlassScreens.Find(new[] { S(100, 3000, 4000, 3000, "PartGlass"), S(100, 3040, 4000, 3040, "PartGlass") }, walls));
        Assert.True(screen.Curtain);
        Assert.Equal(3020, screen.Centerline.A.Y, 0);
        // a glass line lying along an existing wall is not a second wall
        Assert.Empty(GlassScreens.Find(new[] { S(0, 500, 0, 4000, "PartGlass") }, walls));
        var cut = GlassScreens.CutForDoors(new List<WallCandidate> { screen }, new[] { W(1500, 3020, 2500, 3020, 100) });
        Assert.Equal(2, cut.Count);
    }

    [Fact]
    public void Level_comes_from_the_sheet_title_when_the_file_name_has_none()
    {
        Assert.Equal("00", DrawingName.Parse("16382-02.dwg", new[] { "Ground Floor Plan" }).LevelCode);
        Assert.Equal("04", DrawingName.Parse("16382-06.dwg", new[] { "Fourth Floor Plan and Attic" }).LevelCode);
        Assert.Null(DrawingName.Parse("16382-10.dwg", new[] { "Sections 1 and 3" }).LevelCode);
    }

    [Fact]
    public void Cill_and_height_notes_give_sill_and_window_height()
    {
        var notes = SurveyHeights.Read(new[] { (new Vec2(0, 0), "c800"), (new Vec2(0, 0), "h2400") });
        Assert.Contains(notes, n => n.Kind == HeightKind.Sill && n.Mm == 800);
        Assert.Contains(notes, n => n.Kind == HeightKind.OpeningHeight && n.Mm == 2400);
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
        Assert.Equal(460, w.TypeThickness); // 450 is halfway between 20 mm steps: the thicker
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

    [Theory]
    [InlineData("29463B-2 GROUND FLOOR PLAN.dwg", "Ground Floor")]
    [InlineData("29463B-1 (BASEMENT PLAN).dwg", "Basement")]
    [InlineData("29463B-5_THIRD_FLOOR_PLAN.dwg", "Third Floor")]
    [InlineData("Lower Ground Floor.dwg", "Lower Ground Floor")]
    [InlineData("Level 3 plan.dwg", "Third Floor")]
    [InlineData("000002-PCL-ZZZ-101-M2-G-01000-P02-S3.dwg", "First Floor")]
    [InlineData("29463B-8 ROOF PLAN.dwg", "Roof")]
    public void Floor_comes_from_the_file_name(string file, string floor) =>
        Assert.Equal(floor, DrawingName.DescribeLevel(DrawingName.Parse(file).LevelCode!).Name);

    [Theory]
    [InlineData("29463B-9 (SECTIONS & ELEVATIONS).dwg")]
    [InlineData("000002-PCL-ZZZ-ZZZ-M2-G-03000-P03-S3.dwg")]
    public void Sections_and_elevations_are_not_plans(string file) => Assert.Null(DrawingName.Parse(file).LevelCode);

    [Fact]
    public void Facade_corner_left_open_is_closed_even_next_to_a_thick_stub()
    {
        var top = W(0, 9700, 18500, 9700, 600);
        var east = W(19800, 0, 19800, 8500, 200);
        var stub = W(19300, 8000, 19800, 8000, 900); // a pier block across the east wall, just short of its end
        var result = WallDetector.CloseFacade(new List<WallCandidate> { top, east, stub }, Box, new Tolerances());
        var joins = result.Where(w => w.FaceA == "join").ToList();
        Assert.Equal(2, joins.Count);
        Assert.All(joins, j => Assert.True(j.Centerline.A.DistanceTo(new Vec2(19800, 9700)) < 1 || j.Centerline.B.DistanceTo(new Vec2(19800, 9700)) < 1));
    }

    [Fact]
    public void Surveyed_heights_are_read_in_both_common_styles()
    {
        var p = new Vec2(0, 0);
        var a = SurveyHeights.Read(new[] { (p, "S 0.94"), (p, "H 2.58"), (p, "dh 2.03"), (p, "u/s 2.52"), (p, "cl 21.50"), (p, "S 940") });
        Assert.Equal(new[] { HeightKind.Sill, HeightKind.Head, HeightKind.DoorHead, HeightKind.Ceiling, HeightKind.Sill }, a.Select(n => n.Kind));
        Assert.Equal(new[] { 940.0, 2580, 2030, 2520, 940 }, a.Select(n => n.Mm));
        // with floor-to-sill ("fs") on the plan, "sh" is the window's height from sill to head
        var b = SurveyHeights.Read(new[] { (p, "fs 0.92"), (p, "sh 1.88") });
        Assert.Equal(new[] { HeightKind.Sill, HeightKind.OpeningHeight }, b.Select(n => n.Kind));
        Assert.Single(SurveyHeights.Read(new[] { (p, "sh 0.90") }), n => n.Kind == HeightKind.Sill);
    }

    [Fact]
    public void Window_with_floor_to_sill_and_window_height_gets_its_head_from_both()
    {
        var walls = new List<WallCandidate> { W(0, 300, 10000, 300, 600) };
        var texts = new List<(Vec2, string)> { (new Vec2(2600, 900), "fs 0.92"), (new Vec2(2600, 1200), "sh 1.88") };
        var w = Assert.Single(WindowFinder.Find(new[] { S(2000, 300, 3200, 300, "Window") }, walls, texts, 3500));
        Assert.Equal(920, w.SillMm);
        Assert.Equal(2800, w.HeadMm);
        Assert.True(w.HeightsFromSurvey);
    }

    [Fact]
    public void Door_takes_a_surveyed_head_height_and_ceiling_the_middle_surveyed_height()
    {
        var walls = new List<WallCandidate> { W(0, 0, 10000, 0, 100) };
        var notes = SurveyHeights.Read(new[] { (new Vec2(2450, 300), "dh 2.03"), (new Vec2(5000, 3000), "u/s 2.52"), (new Vec2(6000, 3000), "u/s 2.10"), (new Vec2(7000, 3000), "u/s 2.60") });
        var d = Assert.Single(DoorFinder.Find(new[] { S(2000, 0, 2450, 779, "Door") }, walls, heights: notes, storeyMm: 3000));
        Assert.Equal(2030, d.HeightMm);
        Assert.True(d.HeightFromSurvey);
        Assert.Equal(2520, SurveyHeights.CeilingHeight(notes, 3000));
        Assert.Null(SurveyHeights.CeilingHeight(notes, 2000)); // none fit under a floor 2 m above
    }

    [Fact]
    public void Dogleg_stair_is_two_flights_climbed_from_the_up_note()
    {
        var lines = new List<Segment2>();
        // flight 1: risers across x 0..1000, 10 of them going north from y 0; flight 2 beside it going south
        for (int i = 0; i < 10; i++) lines.Add(S(0, i * 250, 1000, i * 250, "EF_35_10-Stair"));
        for (int i = 0; i < 10; i++) lines.Add(S(1200, i * 250, 2200, i * 250, "EF_35_10-Stair"));
        lines.Add(S(0, -100, 0, 2400, "EF_35_10-Stair")); // a stringer: too long to be a riser
        var stairs = StairFinder.Find(lines, new List<(Vec2, string)> { (new Vec2(1700, -300), "UP") });
        var stair = Assert.Single(stairs);
        Assert.Equal(2, stair.Flights.Count);
        Assert.True(stair.DirectionKnown);
        Assert.Equal(1700, stair.Flights[0].Bottom.X, 1); // starts at the UP note
        Assert.Equal(0, stair.Flights[0].Bottom.Y, 1);
        Assert.Equal(2250, stair.Flights[1].Bottom.Y, 1); // second flight from the landing end
        Assert.Equal(250, stair.Flights[0].Going, 1);
        Assert.Equal(1000, stair.Flights[0].Width, 1);
    }

    [Fact]
    public void Curved_flight_risers_fan_out_and_stay_one_flight()
    {
        var lines = new List<Segment2>();
        for (int i = 0; i < 12; i++)
        {
            double a = i * 0.08;
            var u = new Vec2(Math.Cos(a), Math.Sin(a));
            lines.Add(new Segment2(u * 2000, u * 3500, "Stair"));
        }
        var f = Assert.Single(Assert.Single(StairFinder.Find(lines, new List<(Vec2, string)>())).Flights);
        Assert.Equal(12, f.Risers.Count);
        Assert.True(f.Curved);
    }

    [Fact]
    public void Shape_drawn_as_loose_lines_is_filled_to_its_outline()
    {
        // a 400 x 400 column drawn as four separate lines, and an L-shaped pier
        var lines = new List<Segment2>
        {
            new(new Vec2(0, 0), new Vec2(400, 0)), new(new Vec2(400, 0), new Vec2(400, 400)),
            new(new Vec2(400, 400), new Vec2(0, 400)), new(new Vec2(0, 400), new Vec2(0, 0)),
            new(new Vec2(2000, 0), new Vec2(3000, 0)), new(new Vec2(3000, 0), new Vec2(3000, 300)), new(new Vec2(3000, 300), new Vec2(2300, 300)),
            new(new Vec2(2300, 300), new Vec2(2300, 1000)), new(new Vec2(2300, 1000), new Vec2(2000, 1000)), new(new Vec2(2000, 1000), new Vec2(2000, 0)),
        };
        var grid = ShapeGrid.Around(lines.SelectMany(l => new[] { l.A, l.B }), 25, 100);
        var shapes = grid.Outlines(grid.Enclosed(grid.Draw(lines)), 0.02e6).OrderBy(p => p.Area).ToList();
        Assert.Equal(2, shapes.Count);
        Assert.InRange(shapes[0].Area, 0.15e6, 0.19e6);  // 0.16 m² plus the line width
        Assert.InRange(shapes[1].Area, 0.48e6, 0.58e6);  // the L: 0.3 + 0.21 m², plus the line width
        Assert.InRange(shapes[1].Vertices.Count, 6, 10); // straight edges, not a staircase of cells
    }

    [Fact]
    public void Door_symbol_gives_its_opening_and_a_wall_across_an_empty_opening()
    {
        // two walls running north, ends 900 apart at y=0 (a doorway between wall ends, no wall across it)
        var walls = new List<WallCandidate> { W(0, -3000, 0, 0, 200), W(0, 900, 0, 4000, 200) };
        // leaf hinged at (0,0), open at 45° into +x; swing back to (0,900)
        var leaf = S(0, 0, 636, 636, "Door");
        var arc = new Arc2(new Vec2(0, 0), 900, Math.PI / 4, Math.PI / 2, "Door");
        var added = new List<WallCandidate>();
        var d = Assert.Single(DoorFinder.Find(new[] { leaf }, walls, arcs: new[] { arc }, added: added));
        Assert.Equal(900, d.WidthMm);
        Assert.Equal(450, d.Center.Y, 0);
        var wall = Assert.Single(added);
        Assert.Equal(2, d.HostIndex);
        Assert.True(wall.Centerline.Length >= 900);
    }

    [Fact]
    public void Facade_is_widened_to_the_masonry_behind_it()
    {
        // building 10 x 8 m; the drawing's material runs 600 mm in from the outside face, but a line pair found only 200 mm of it
        var outline = new Polygon2(new[] { new Vec2(0, 0), new Vec2(10000, 0), new Vec2(10000, 8000), new Vec2(0, 8000) });
        var wall = W(1000, 100, 9000, 100, 200);
        bool material(Vec2 p) => p.Y >= 0 && p.Y <= 600 && p.X >= 0 && p.X <= 10000;
        var fitted = Assert.Single(WallFit.Fit(new List<WallCandidate> { wall }, material, outline, Tolerances.Default));
        Assert.InRange(fitted.TypeThickness, 580, 620);
        Assert.InRange(fitted.Centerline.A.Y, 280, 320);
    }

    [Fact]
    public void A_wall_inside_another_is_removed()
    {
        var walls = new List<WallCandidate> { W(0, 0, 5000, 0, 600), W(1000, 100, 3000, 100, 100), W(2000, 0, 2000, 3000, 100) };
        var kept = WallFit.RemoveOverlaps(walls, Tolerances.Default);
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, k => k.TypeThickness == 100 && k.Centerline.AngleDeg < 1);
    }

    [Fact]
    public void Stair_footprint_covers_its_flights()
    {
        var lines = new List<Segment2>();
        for (int i = 0; i < 8; i++) lines.Add(S(0, i * 250, 1000, i * 250, "Stair"));
        var stair = Assert.Single(StairFinder.Find(lines, new List<(Vec2, string)>()));
        var f = stair.Footprint();
        Assert.InRange(f.Area, 1.75e6, 2.1e6);
    }
}
