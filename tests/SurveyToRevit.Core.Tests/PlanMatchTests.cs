using SurveyToRevit.Core;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class PlanMatchTests
{
    // an L-shaped building with a few inside walls (no symmetry), mm
    static List<Segment2> Building()
    {
        var pts = new[] { (0, 0), (24000, 0), (24000, 9000), (11000, 9000), (11000, 21000), (0, 21000) };
        var walls = new List<Segment2>();
        for (int i = 0; i < pts.Length; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Length];
            walls.Add(new Segment2(new Vec2(a.Item1, a.Item2), new Vec2(b.Item1, b.Item2)));
        }
        walls.Add(new Segment2(new Vec2(0, 6000), new Vec2(11000, 6000)));
        walls.Add(new Segment2(new Vec2(5000, 6000), new Vec2(5000, 21000)));
        walls.Add(new Segment2(new Vec2(17000, 0), new Vec2(17000, 9000)));
        walls.Add(new Segment2(new Vec2(0, 14500), new Vec2(5000, 14500)));
        return walls;
    }

    static List<Segment2> Move(IEnumerable<Segment2> walls, PlanMove m) => walls.Select(w => new Segment2(m.Apply(w.A), m.Apply(w.B))).ToList();

    [Theory]
    [InlineData(7.3, 12300, -4500)]
    [InlineData(-92.0, -55000, 81000)]
    [InlineData(180.0, 3000, 2000)]
    [InlineData(0.0, 450, -320)]
    public void Finds_where_a_model_drawn_elsewhere_sits_on_the_survey(double turnDeg, double dx, double dy)
    {
        var survey = Building();
        var model = Move(survey, new PlanMove(turnDeg * Math.PI / 180, dx, dy, 0));
        var fit = PlanMatch.Fit(survey, model);
        Assert.True(fit.Score > 0.95, $"score {fit.Score:0.00}");
        foreach (var w in model)
            Assert.True(fit.Apply(w.A).DistanceTo(survey[model.IndexOf(w)].A) < 20);
    }

    [Fact]
    public void Copes_with_a_model_that_has_only_some_walls_and_extra_ones()
    {
        var survey = Building();
        var truth = new PlanMove(3.0 * Math.PI / 180, -8000, 15000, 0);
        var model = Move(survey.Take(8), truth);
        model.Add(new Segment2(new Vec2(-30000, -30000), new Vec2(-25000, -30000))); // something else in the model
        var fit = PlanMatch.Fit(survey, model);
        var back = fit.Apply(truth.Apply(new Vec2(24000, 9000)));
        Assert.True(back.DistanceTo(new Vec2(24000, 9000)) < 20, back.ToString());
    }
}
