using SurveyToRevit.Core;

namespace SurveyToRevit.Core.Tests;

/// <summary>Hand-built survey linework with known answers, in mm.</summary>
public static class SyntheticPlans
{
    static Segment2 S(double x1, double y1, double x2, double y2, string layer = "A-WALL") =>
        new(new Vec2(x1, y1), new Vec2(x2, y2), layer);

    /// <summary>
    /// 10 x 8 m rectangle, 300 mm external walls, one 100 mm partition at x = 5000.
    /// The inner faces of the long walls are broken where the partition meets them,
    /// as they would be in a real survey.
    /// </summary>
    public static List<Segment2> RectangleWithPartition() => new()
    {
        // outer faces
        S(0, 0, 10000, 0), S(10000, 0, 10000, 8000), S(10000, 8000, 0, 8000), S(0, 8000, 0, 0),
        // inner faces of left and right walls
        S(300, 300, 300, 7700), S(9700, 300, 9700, 7700),
        // inner faces of bottom and top walls, broken at the partition
        S(300, 300, 4950, 300), S(5050, 300, 9700, 300),
        S(300, 7700, 4950, 7700), S(5050, 7700, 9700, 7700),
        // partition faces
        S(4950, 300, 4950, 7700), S(5050, 300, 5050, 7700),
    };
}
