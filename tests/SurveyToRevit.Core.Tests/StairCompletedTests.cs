using SurveyToRevit.Core;
using Xunit;

public class StairCompletedTests
{
    static StairCandidate Flight(int n, double going = 250) =>
        new(new[] { new StairFlight(Enumerable.Range(0, n).Select(i => new Segment2(new Vec2(0, i * going), new Vec2(1000, i * going))).ToList()) }, true);

    [Fact]
    public void Cut_flight_runs_on_to_the_riser_count()
    {
        var s = Flight(12).Completed(18);
        Assert.Equal(18, s.Risers);
        var last = s.Flights[^1].Risers[^1];
        Assert.Equal(17 * 250, last.A.Y, 1);
        Assert.Equal(0, last.A.X, 1);
    }

    [Fact]
    public void Full_stair_is_unchanged()
    {
        var s = Flight(18);
        Assert.Same(s, s.Completed(18));
        Assert.Same(s, s.Completed(12));
    }

    [Fact]
    public void Odd_going_is_left_alone()
    {
        var s = Flight(5, going: 900);
        Assert.Same(s, s.Completed(18));
    }
}
