using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class ElevationWindowTests
{
    static IEnumerable<Segment2> Rect(double x0, double y0, double x1, double y1)
    {
        yield return new(new Vec2(x0, y0), new Vec2(x1, y0), "WINDOWS"); yield return new(new Vec2(x1, y0), new Vec2(x1, y1), "WINDOWS");
        yield return new(new Vec2(x1, y1), new Vec2(x0, y1), "WINDOWS"); yield return new(new Vec2(x0, y1), new Vec2(x0, y0), "WINDOWS");
    }

    [Fact]
    public void Elevation_sets_the_plan_windows_heights_and_adds_the_ones_the_plan_missed()
    {
        var box = new Polygon2(new[] { new Vec2(0, 0), new Vec2(20000, 0), new Vec2(20000, 12000), new Vec2(0, 12000) });
        var wall = new WallCandidate(new Segment2(new Vec2(0, 150), new Vec2(20000, 150), "W"), 300, 300, Confidence.High, "W", "W");
        var planWindow = new WindowCandidate(0, new Vec2(2000, 150), 1200, 900, 2400, false);
        var level = new SurveyLevel("00", "Ground", 0, 30000, null, null!, new WallDetectionResult(new[] { wall }, Array.Empty<Segment2>(), Array.Empty<double>(), 0, 0, Array.Empty<double>()), box, Array.Empty<UnclearRegion>())
        { Windows = new[] { planWindow }, WallHeightMm = 3500 };
        var d = new ElevationDrawing
        {
            File = "E.dwg", Title = "FRONT ELEVATION", Facade = "F1", FacadeLine = new double[] { 0, 0, 20000, 0 }, Scale = 1, ShiftMm = 0, Score = 0.95,
            Parts = { new ElevationPart(0, 20000, 1, 30000, 0, Array.Empty<double[]>(), Array.Empty<double[]>()) },
            Lines = Rect(1400, 800, 2600, 2500).Concat(Rect(8000, 1000, 9000, 2600)).ToList(),
        };
        var r = ElevationWindows.Apply(new[] { d }, new[] { level }, LayerRules.Default);
        Assert.Equal(2, r.Drawn);
        Assert.Equal(1, r.HeightsSet);
        Assert.Equal(1, r.Added);
        var fromPlan = level.Windows.Single(w => !w.FromElevation);
        Assert.Equal(800, fromPlan.SillMm); Assert.Equal(2500, fromPlan.HeadMm); Assert.Equal(1200, fromPlan.WidthMm);
        var added = level.Windows.Single(w => w.FromElevation);
        Assert.Equal(1000, added.WidthMm); Assert.Equal(8500, added.Center.X, 0); Assert.Equal(150, added.Center.Y, 0);
        Assert.Equal(-1, added.Outward!.Value.Y, 3);
    }

    [Fact]
    public void Without_a_matched_elevation_nothing_changes()
    {
        var d = new ElevationDrawing { File = "E.dwg", Title = "SIDE", Lines = Rect(0, 0, 1000, 1500).ToList() };
        Assert.Empty(ElevationWindows.Read(d, LayerRules.Default));
    }
}
