using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class SurveyTests
{
    static Segment2 S(double x1, double y1, double x2, double y2, string layer = "EF_25-Wall") => new(new Vec2(x1, y1), new Vec2(x2, y2), layer);

    static Segment2 Rotate(Segment2 s, double deg)
    {
        var r = deg * Math.PI / 180;
        Vec2 R(Vec2 p) => new(p.X * Math.Cos(r) - p.Y * Math.Sin(r), p.X * Math.Sin(r) + p.Y * Math.Cos(r));
        return s with { A = R(s.A), B = R(s.B) };
    }

    [Fact]
    public void Main_direction_of_a_rotated_building_is_found_and_crooked_lines_straightened()
    {
        var plan = SyntheticPlans.RectangleWithPartition().Select(s => Rotate(s, 12.6)).ToList();
        plan[0] = Rotate(plan[0], 1.0); // one survey line 1° off
        var axes = AxisSnapper.FindAxes(plan, 2);
        Assert.Equal(12.6, AxisSnapper.MainRotationDeg(axes), 1);

        var snapped = AxisSnapper.Snap(plan, axes, 2);
        Assert.All(snapped, s => Assert.True(axes.Min(a => SegmentCleaner.AngleDiffDeg(s.AngleDeg, a)) < 1e-6));
    }

    [Fact]
    public void Rotated_building_still_gives_five_walls()
    {
        var plan = SyntheticPlans.RectangleWithPartition().Select(s => Rotate(s, 12.6)).ToList();
        Assert.Equal(5, WallDetector.Detect(plan).Walls.Count);
    }

    [Fact]
    public void Facade_with_window_recesses_becomes_one_wall_keeping_its_outer_face()
    {
        // Outer face y=0, 12 m long. Behind it: piers 600 thick, window bays recessed to 300 thick.
        var segs = new List<Segment2> { S(0, 0, 12000, 0) };
        for (int i = 0; i < 4; i++)
        {
            double x = i * 3000;
            segs.Add(S(x, 600, x + 1000, 600));          // pier inner face
            segs.Add(S(x + 1000, 300, x + 3000, 300));   // bay inner face
            segs.Add(S(x + 1000, 300, x + 1000, 600));   // reveal
            if (i < 3) segs.Add(S(x + 3000, 300, x + 3000, 600));
        }
        var r = WallDetector.Detect(segs);
        var w = Assert.Single(r.Walls.Where(x => SegmentCleaner.AngleDiffDeg(x.Centerline.AngleDeg, 0) < 1));
        Assert.True(w.Length >= 11900, $"length {w.Length}");
        Assert.Equal(300, w.TypeThickness); // bays are most of the length
        Assert.Equal(150, Math.Abs(w.Centerline.Mid.Y), 0); // outer face stays at y = 0
    }

    [Fact]
    public void Gap_with_room_label_is_not_a_wall()
    {
        var segs = new[] { S(0, 0, 5000, 0), S(0, 800, 5000, 800) };
        Assert.Single(WallDetector.Detect(segs).Walls);
        Assert.Empty(WallDetector.Detect(segs, spaceMarkers: new[] { new Vec2(2500, 400) }).Walls);
    }

    [Fact]
    public void Wall_pieces_either_side_of_a_door_join_into_one_wall()
    {
        var segs = new[] { S(0, 0, 3000, 0), S(0, 150, 3000, 150), S(3900, 0, 7000, 0), S(3900, 150, 7000, 150) };
        Assert.Equal(2, WallDetector.Detect(segs).Walls.Count);
        var door = new[] { S(3000, 75, 3900, 75, "Ss_25_30_20-Door") };
        var w = Assert.Single(WallDetector.Detect(segs, openings: door).Walls);
        Assert.Equal(7000, w.Length, 0);
        Assert.Equal("Concept Wall 150mm", w.TypeName);
    }

    [Fact]
    public void Floor_level_is_the_height_most_spot_heights_agree_on()
    {
        var heights = new[] { 36.78, 36.78, 36.79, 36.77, 37.66, 37.64, 36.99, 36.78 }.Select(m => m * 1000);
        var e = LevelEstimator.Estimate(heights)!;
        Assert.Equal(36780, e.FloorLevelMm);
        Assert.Equal(5, e.Supporting);
        Assert.Contains(e.OtherLevels, o => Math.Abs(o.LevelMm - 37650) <= 10);
    }

    [Theory]
    [InlineData("27.05", 27050.0)]
    [InlineData("FFL 27.05", 27050.0)]
    [InlineData("-1.20", -1200.0)]
    public void Spot_heights_parse(string text, double mm) => Assert.Equal(mm, LevelEstimator.ParseMm(text)!.Value, 6);

    [Theory]
    [InlineData("S 0.98")]
    [InlineData("Lift")]
    [InlineData("2.80m ceiling")]
    public void Other_texts_are_not_spot_heights(string text) => Assert.Null(LevelEstimator.ParseMm(text));

    [Theory]
    [InlineData("000002-PCL-ZZZ-100-M2-G-01000-P02-S3.dwg", "100", "Ground Floor")]
    [InlineData("000002-PCL-ZZZ-100M-M2-G-01000-P02-S3.dwg", "100M", "Ground Floor Mezzanine")]
    [InlineData("000002-PCL-M01-099-M2-G-01000-P02-S3.dwg", "099", "Basement")]
    [InlineData("000002-PCL-ZZZ-107-M2-G-01000-P03-S3.dwg", "107", "Seventh Floor")]
    public void Level_comes_from_the_file_name(string file, string code, string name)
    {
        var d = DrawingName.Parse(file);
        Assert.Equal(code, d.LevelCode);
        Assert.Equal(name, DrawingName.DescribeLevel(code).Name);
    }

    [Theory]
    [InlineData("000002-PCL-ZZZ-ZZZ-M2-G-03000-P03-S3.dwg")]
    [InlineData("000002-PCL-M04-XXX-M2-G-01000-P01-S3.dwg")]
    [InlineData("25074-BGA-ZZ-XX-DR-A-01271 Existing Elevation (Regents Street).dwg")]
    public void Multi_level_sheets_have_no_level(string file) => Assert.Null(DrawingName.Parse(file).LevelCode);

    [Fact]
    public void Levels_sort_basement_ground_mezzanine_first()
    {
        var order = new[] { "101", "100M", "099", "100" }.OrderBy(c => DrawingName.DescribeLevel(c).Order).ToArray();
        Assert.Equal(new[] { "099", "100", "100M", "101" }, order);
    }

    [Fact]
    public void Survey_frame_puts_base_point_at_origin_and_squares_the_building()
    {
        var f = new SurveyFrame(new Vec2(529_000_000, 181_190_000), 12.6);
        var p = f.ToModel(new Vec2(529_000_000, 181_190_000));
        Assert.Equal(0, p.X, 6);
        Assert.Equal(0, p.Y, 6);
        // a wall running along the building's main direction becomes horizontal
        var r = 12.6 * Math.PI / 180;
        var wall = new Segment2(new Vec2(529_000_000, 181_190_000), new Vec2(529_000_000 + 5000 * Math.Cos(r), 181_190_000 + 5000 * Math.Sin(r)));
        Assert.True(SegmentCleaner.AngleDiffDeg(f.ToModel(wall).AngleDeg, 0) < 1e-6);
    }

    [Fact]
    public void Unitless_survey_drawn_in_metres_is_detected()
    {
        var metres = new List<(double, double, double, double)> { (528969, 181161, 529041, 181218) };
        Assert.Equal(1000, SurveyDrawing.Units(ACadSharp.Types.Units.UnitsType.Unitless, metres).mmPerUnit);
        var mm = new List<(double, double, double, double)> { (0, 0, 72000, 57000) };
        Assert.Equal(1, SurveyDrawing.Units(ACadSharp.Types.Units.UnitsType.Unitless, mm).mmPerUnit);
    }

    [Fact]
    public void Uniclass_and_plain_layer_names_are_classified()
    {
        var r = LayerRules.Default;
        Assert.Equal(LayerRole.Wall, r.Classify("EF_25-Wall"));
        Assert.Equal(LayerRole.Wall, r.Classify("Ss_25_10_30-Partition"));
        Assert.Equal(LayerRole.Wall, r.Classify("A-WALL"));
        Assert.Equal(LayerRole.Window, r.Classify("Ss_25_30_95-Window"));
        Assert.Equal(LayerRole.Door, r.Classify("Ss_25_30_20-Fire_Door"));
        Assert.Equal(LayerRole.LevelText, r.Classify("Zz_60_45-Level"));
        Assert.Equal(LayerRole.Other, r.Classify("Zz_20_40_05-exRestrictedHeadroom"));
        Assert.Equal(LayerRole.Other, r.Classify("Pr_40_30_30-FittedFurn"));
    }
}
