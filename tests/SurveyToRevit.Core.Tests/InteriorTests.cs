using SurveyToRevit.Core;
using SurveyToRevit.Core.Interior;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Tests;

public class InteriorTests
{
    /// <summary>A closed box as triangles (12), mm.</summary>
    static double[] Box(double x0, double y0, double x1, double y1, double z0, double z1)
    {
        var c = new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };
        var t = new List<double>();
        void Tri((double, double, double) a, (double, double, double) b, (double, double, double) d)
        { t.AddRange(new[] { a.Item1, a.Item2, a.Item3, b.Item1, b.Item2, b.Item3, d.Item1, d.Item2, d.Item3 }); }
        for (int i = 0; i < 4; i++)
        {
            var (ax, ay) = c[i]; var (bx, by) = c[(i + 1) % 4];
            Tri((ax, ay, z0), (bx, by, z0), (bx, by, z1));
            Tri((ax, ay, z0), (bx, by, z1), (ax, ay, z1));
        }
        Tri((x0, y0, z0), (x1, y0, z0), (x1, y1, z0)); Tri((x0, y0, z0), (x1, y1, z0), (x0, y1, z0));
        Tri((x0, y0, z1), (x1, y0, z1), (x1, y1, z1)); Tri((x0, y0, z1), (x1, y1, z1), (x0, y1, z1));
        return t.ToArray();
    }

    /// <summary>The exterior of a 10 x 8 m building: four 300 mm walls, 0 to 3000 mm, moved by (dx, dy).</summary>
    static ExistingBuilding Shell(double dx = 0, double dy = 0, params ExistingElement[] more)
    {
        var walls = new List<ExistingElement>
        {
            new("Walls", "south", Box(dx, dy, dx + 10000, dy + 300, 0, 3000)),
            new("Walls", "north", Box(dx, dy + 7700, dx + 10000, dy + 8000, 0, 3000)),
            new("Walls", "west", Box(dx, dy, dx + 300, dy + 8000, 0, 3000)),
            new("Walls", "east", Box(dx + 9700, dy, dx + 10000, dy + 8000, 0, 3000)),
        };
        walls.AddRange(more);
        return new ExistingBuilding(walls, new[] { new ExistingLevel("Level 0", 0, true), new ExistingLevel("Level 1", 3000, true) });
    }

    static SurveyLevel Floor(IReadOnlyList<WallCandidate> walls, IReadOnlyList<WindowCandidate>? windows = null)
    {
        var plan = new SurveyDrawing
        {
            Path = "test.dwg", Name = new DrawingName("test.dwg", "00"), MmPerUnit = 1, UnitsNote = "",
            WallLines = SyntheticPlans.RectangleWithPartition(), OpeningLines = Array.Empty<Segment2>(),
            Texts = Array.Empty<SurveyText>(), LayerCounts = new Dictionary<string, int>(),
        };
        var outline = new Polygon2(new[] { new Vec2(0, 0), new Vec2(10000, 0), new Vec2(10000, 8000), new Vec2(0, 8000) });
        var result = new WallDetectionResult(walls, Array.Empty<Segment2>(), Array.Empty<double>(), 0, 0, Array.Empty<double>());
        return new SurveyLevel("00", "Ground Floor", 0, 0, null, plan, result, outline, Array.Empty<UnclearRegion>())
        {
            Windows = windows ?? Array.Empty<WindowCandidate>(), CeilingHeightMm = 2500, WallHeightMm = 3000,
        };
    }

    static WallCandidate Wall(double x1, double y1, double x2, double y2, double t) =>
        new(new Segment2(new Vec2(x1, y1), new Vec2(x2, y2)), t, t, Confidence.High, "", "");

    [Fact]
    public void BoxCutIsItsOutline()
    {
        var e = new ExistingElement("Walls", "w", Box(0, 0, 4000, 300, 0, 3000));
        var cut = e.Cut(1200);
        Assert.Equal(4300 * 2, cut.Sum(s => s.Length), 0);
        Assert.Empty(e.Cut(3500));
    }

    [Fact]
    public void DatumFromTheLevelsThatAgree()
    {
        // survey floors in mm AOD; the project's levels at 0 and 3000, plus one survey floor that is 220 mm off
        var (datum, matched, _) = InteriorFit.Datum(new[] { 26690.0, 29690, 33000 }, new[] { new ExistingLevel("0", 0, true), new ExistingLevel("1", 3000, true), new ExistingLevel("2", 6090, true) }, null);
        Assert.Equal(26690, datum, 0);
        Assert.Equal(2, matched);
    }

    [Fact]
    public void ComposedFrameMapsTheSurveyWhereTheMoveSays()
    {
        var frame0 = new SurveyFrame(new Vec2(529000000, 181190000), 12.6);
        var move = new PlanMove(0.3, 1500, -800, 1);
        Vec2 cs = new(2000, 1000), ce = new(-35000, 12000);
        var frame = InteriorFit.Compose(frame0, move, cs, ce);
        foreach (var p in new[] { new Vec2(529010000, 181200000), new Vec2(528990000, 181170000) })
        {
            var s = frame0.ToModel(p) - cs;
            // the move maps the existing (centred) onto the survey (centred): undo it
            double c = Math.Cos(-move.Angle), n = Math.Sin(-move.Angle);
            var d = s - new Vec2(move.Dx, move.Dy);
            var expected = new Vec2(c * d.X - n * d.Y, n * d.X + c * d.Y) + ce;
            Assert.True(frame.ToModel(p).DistanceTo(expected) < 0.01, $"{frame.ToModel(p)} vs {expected}");
        }
    }

    [Fact]
    public void FacadeWallsLeftOutInteriorKeptAndMetToTheFace()
    {
        // survey: the four outside walls (on the existing ones) and a partition drawn to the outer walls' centre lines
        var walls = new List<WallCandidate>
        {
            Wall(0, 150, 10000, 150, 300), Wall(0, 7850, 10000, 7850, 300), Wall(150, 0, 150, 8000, 300), Wall(9850, 0, 9850, 8000, 300),
            Wall(5000, 150, 5000, 7850, 100),
        };
        var windows = new[] { new WindowCandidate(0, new Vec2(3000, 150), 1200, 900, 2100, true) };
        var level = Floor(walls, windows);
        var r = InteriorSplit.Level(level, Shell(), new[] { 300.0, 1200, 2400 });
        Assert.Equal(4, r.WallsLeftOut);
        Assert.Equal(1, r.WindowsLeftOut);
        var kept = Assert.Single(level.Walls.Walls);
        // the partition now runs from the inner face of the south wall to the inner face of the north wall
        Assert.InRange(Math.Min(kept.Centerline.A.Y, kept.Centerline.B.Y), 280, 330);
        Assert.InRange(Math.Max(kept.Centerline.A.Y, kept.Centerline.B.Y), 7670, 7720);
        // the floor stops at the inner faces: 9.4 x 7.4 m
        Assert.InRange(level.Outline!.Area / 1e6, 9.4 * 7.4 - 1.5, 9.4 * 7.4 + 1.5);
    }

    [Fact]
    public void WallOutsideTheExistingEnvelopeIsLeftOut()
    {
        // a terrace parapet 1.5 m out from the existing north wall; an interior wall well inside
        var walls = new List<WallCandidate> { Wall(1000, 9500, 9000, 9500, 200), Wall(2000, 2000, 8000, 2000, 100) };
        var level = Floor(walls);
        var r = InteriorSplit.Level(level, Shell(), new[] { 300.0, 1200, 2400 });
        Assert.Equal(1, r.WallsLeftOut);
        Assert.Equal(2000, Assert.Single(level.Walls.Walls).Centerline.A.Y, 0);
    }

    [Fact]
    public void FitFindsAShiftedExterior()
    {
        // the survey's lines (the synthetic rectangle) against the same exterior 6.5 m away in the project
        var level = Floor(new List<WallCandidate>());
        level.ElevationMm = 26690;
        var model = new SurveyModel { Frame = new SurveyFrame(new Vec2(0, 0), 0), Levels = new[] { level }, Skipped = Array.Empty<SkippedDrawing>(), Notes = Array.Empty<string>() };
        var existing = new ExistingBuilding(Shell(-6500, 40).Elements, new[] { new ExistingLevel("GF", 0, true) });
        var fit = InteriorFit.Fit(model, existing);
        Assert.True(fit.Good, string.Join(" ", fit.Notes));
        Assert.Equal(26690, fit.DatumMm, 0);
        var p = fit.Frame.ToModel(new Vec2(10000, 8000));
        Assert.True(p.DistanceTo(new Vec2(3500, 8040)) < 30, p.ToString());
    }
}
