using SurveyToRevit.Core;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class GeometryTests
{
    const double Tol = 1.0; // mm

    static Segment2 S(double x1, double y1, double x2, double y2) => new(new Vec2(x1, y1), new Vec2(x2, y2));

    static void AssertNear(Vec2 expected, Vec2 actual) =>
        Assert.True(expected.DistanceTo(actual) <= Tol, $"Expected {expected}, got {actual}");

    static bool SameLine(Segment2 s, Vec2 p, Vec2 q) =>
        (s.A.DistanceTo(p) <= Tol && s.B.DistanceTo(q) <= Tol) || (s.A.DistanceTo(q) <= Tol && s.B.DistanceTo(p) <= Tol);

    [Fact]
    public void Units_convert_between_feet_and_mm()
    {
        Assert.Equal(304.8, UnitConversion.FeetToMm(1), 6);
        Assert.Equal(1.0, UnitConversion.MmToFeet(304.8), 6);
        Assert.Equal(1000, UnitConversion.MmPerUnit("m"));
        Assert.Equal(10, UnitConversion.MmPerUnit("cm"));
        Assert.Throws<ArgumentException>(() => UnitConversion.MmPerUnit("furlong"));
    }

    [Fact]
    public void Cleaner_merges_fragmented_duplicate_and_slightly_offset_lines()
    {
        var input = new[]
        {
            S(0, 0, 3000, 0),
            S(3005, 2, 6000, 2),     // 5 mm gap, 2 mm off line
            S(5000, 0, 10000, 0),    // overlap
            S(2000, 0, 2500, 0),     // duplicate piece
            S(0, 1000, 10000, 1000), // separate parallel line
        };
        var cleaned = SegmentCleaner.Clean(input, Tolerances.Default);
        Assert.Equal(2, cleaned.Count);
        var merged = cleaned.Single(s => s.Mid.Y < 500);
        Assert.Equal(10000, merged.Length, 0);
    }

    [Fact]
    public void Cleaner_drops_tiny_segments()
    {
        var cleaned = SegmentCleaner.Clean(new[] { S(0, 0, 20, 0), S(0, 100, 1000, 100) }, Tolerances.Default);
        Assert.Single(cleaned);
    }

    [Fact]
    public void Two_parallel_lines_make_one_wall_with_correct_thickness()
    {
        var r = WallDetector.Detect(new[] { S(0, 0, 5000, 0), S(0, 250, 5000, 250) });
        var w = Assert.Single(r.Walls);
        Assert.Equal(250, w.MeasuredThickness, 3);
        Assert.Equal("Concept Wall 250mm", w.TypeName);
        Assert.True(SameLine(w.Centerline, new Vec2(0, 125), new Vec2(5000, 125)));
        Assert.Equal(Confidence.High, w.Confidence);
    }

    [Fact]
    public void Rotated_wall_thickness_is_measured_perpendicular()
    {
        var ang = 30 * Math.PI / 180;
        var u = new Vec2(Math.Cos(ang), Math.Sin(ang));
        var n = u.Perp();
        var a = new Segment2(new Vec2(0, 0), u * 4000);
        var b = new Segment2(n * 200, n * 200 + u * 4000);
        var w = Assert.Single(WallDetector.Detect(new[] { a, b }).Walls);
        Assert.Equal(200, w.MeasuredThickness, 3);
    }

    [Fact]
    public void A_lone_line_is_reported_not_turned_into_a_wall()
    {
        var r = WallDetector.Detect(new[] { S(0, 0, 5000, 0), S(0, 250, 5000, 250), S(0, 3000, 4000, 3000) });
        Assert.Single(r.Walls);
        Assert.Single(r.UnpairedSegments);
    }

    [Fact]
    public void Lines_too_far_apart_are_not_a_wall()
    {
        var r = WallDetector.Detect(new[] { S(0, 0, 5000, 0), S(0, 1200, 5000, 1200) });
        Assert.Empty(r.Walls);
        Assert.Equal(2, r.UnpairedSegments.Count);
    }

    [Fact]
    public void Odd_thicknesses_snap_to_a_few_standard_wall_types()
    {
        var sizes = Tolerances.Default.WallTypeSizes;
        Assert.Equal(100, WallDetector.SnapThickness(92, sizes));
        Assert.Equal(150, WallDetector.SnapThickness(138, sizes));
        Assert.Equal(500, WallDetector.SnapThickness(523, sizes));
        Assert.Equal(700, WallDetector.SnapThickness(667, sizes));
        Assert.Equal(1200, WallDetector.SnapThickness(1180, sizes));
    }

    [Fact]
    public void Rectangle_with_partition_gives_four_external_walls_and_one_partition_meeting_at_centrelines()
    {
        var r = WallDetector.Detect(SyntheticPlans.RectangleWithPartition());

        Assert.Equal(5, r.Walls.Count);
        Assert.Equal(4, r.Walls.Count(w => w.TypeName == "Concept Wall 300mm"));
        Assert.Equal(1, r.Walls.Count(w => w.TypeName == "Concept Wall 100mm"));

        // External centre lines form a closed 9700 x 7700 rectangle.
        var ext = r.Walls.Where(w => w.TypeThickness == 300).Select(w => w.Centerline).ToList();
        Assert.Contains(ext, s => SameLine(s, new Vec2(150, 150), new Vec2(9850, 150)));
        Assert.Contains(ext, s => SameLine(s, new Vec2(9850, 150), new Vec2(9850, 7850)));
        Assert.Contains(ext, s => SameLine(s, new Vec2(150, 7850), new Vec2(9850, 7850)));
        Assert.Contains(ext, s => SameLine(s, new Vec2(150, 150), new Vec2(150, 7850)));

        // Partition runs to the centre lines of the walls it meets (T-junctions).
        var part = r.Walls.Single(w => w.TypeThickness == 100).Centerline;
        Assert.True(SameLine(part, new Vec2(5000, 150), new Vec2(5000, 7850)), $"Partition was {part}");
    }

    [Fact]
    public void Rectangle_survives_fragmented_and_duplicated_linework()
    {
        var segs = SyntheticPlans.RectangleWithPartition();
        // break the bottom outer face into three slightly misaligned pieces and duplicate one
        segs.RemoveAt(0);
        segs.Add(S(0, 0, 4000, 0));
        segs.Add(S(4003, 1.5, 7000, 1.5));
        segs.Add(S(7000, 0, 10000, 0));
        segs.Add(S(7000, 0, 10000, 0));

        var r = WallDetector.Detect(segs);
        Assert.Equal(5, r.Walls.Count);
        Assert.Empty(r.UnpairedSegments);
    }

    [Fact]
    public void Valid_rectangle_slab_passes_validation()
    {
        var p = new Polygon2(new[] { new Vec2(0, 0), new Vec2(10000, 0), new Vec2(10000, 8000), new Vec2(0, 8000) });
        Assert.Empty(p.Validate());
        Assert.Equal(80e6, p.Area, 0);
        Assert.True(p.SignedArea > 0);
    }

    [Fact]
    public void Concave_L_shape_is_valid()
    {
        var p = new Polygon2(new[] { new Vec2(0, 0), new Vec2(6000, 0), new Vec2(6000, 3000), new Vec2(3000, 3000), new Vec2(3000, 6000), new Vec2(0, 6000) });
        Assert.Empty(p.Validate());
        Assert.Equal(27e6, p.Area, 0);
    }

    [Fact]
    public void Self_intersecting_and_duplicate_vertex_outlines_are_rejected()
    {
        var bowtie = new Polygon2(new[] { new Vec2(0, 0), new Vec2(4000, 4000), new Vec2(4000, 0), new Vec2(0, 4000) });
        Assert.Contains(bowtie.Validate(), i => i.Contains("self-intersection"));

        var dup = new Polygon2(new[] { new Vec2(0, 0), new Vec2(4000, 0), new Vec2(4000, 0.2), new Vec2(4000, 4000), new Vec2(0, 4000) });
        Assert.Contains(dup.Validate(), i => i.Contains("Duplicate"));
    }

    [Fact]
    public void Wrong_units_show_up_as_unreasonable_area()
    {
        // a 10 x 8 m slab drawn in metres but read as mm
        var p = new Polygon2(new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 8), new Vec2(0, 8) });
        Assert.Contains(p.Validate(), i => i.Contains("too small"));
    }

    [Fact]
    public void Outline_from_unordered_segments_closes_or_reports_gap()
    {
        var closed = new[] { S(0, 0, 5000, 0), S(5000, 4000, 0, 4000), S(5000, 0, 5000, 4000), S(0, 4000, 0, 0) };
        var poly = Polygon2.FromSegments(closed, 5, out var problem);
        Assert.Null(problem);
        Assert.Equal(20e6, poly!.Area, 0);

        var open = new[] { S(0, 0, 5000, 0), S(5000, 0, 5000, 4000), S(5000, 4000, 0, 4000), S(0, 4000, 0, 500) };
        Assert.Null(Polygon2.FromSegments(open, 5, out problem));
        Assert.Contains("open", problem);
    }
}
