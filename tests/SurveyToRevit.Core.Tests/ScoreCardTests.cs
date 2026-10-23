using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class ScoreCardTests
{
    static ScoreWall W(double x1, double y1, double x2, double y2, double t = 215, string level = "GF") => new(level, new Segment2(new Vec2(x1, y1), new Vec2(x2, y2)), t);

    [Fact]
    public void Same_model_scores_all_right()
    {
        var walls = new[] { W(0, 0, 10000, 0), W(10000, 0, 10000, 8000), W(0, 4000, 6000, 4000, 100) };
        var win = new[] { new ScoreOpening("GF", new Vec2(3000, 0), 1200) };
        var s = ScoreCard.Score(walls, walls, win, win, [], []);
        var (w, wi, _) = s.Total();
        Assert.Equal((3, 0, 0, 0), (w.Right, w.Off, w.Missed, w.Invented));
        Assert.Equal(1, wi.Right);
        Assert.Equal(1.0, w.Recall);
    }

    [Fact]
    public void Counts_off_missed_and_invented()
    {
        var reference = new[] { W(0, 0, 10000, 0), W(10000, 0, 10000, 8000), W(0, 4000, 6000, 4000, 100) };
        var ours = new[]
        {
            W(0, 60, 10000, 60),           // right: 60 mm out
            W(10250, 0, 10250, 8000),      // off: 250 mm out
            W(20000, 0, 20000, 5000),      // invented
        };
        var refWin = new[] { new ScoreOpening("GF", new Vec2(3000, 0), 1200), new ScoreOpening("GF", new Vec2(7000, 0), 900) };
        var ourWin = new[] { new ScoreOpening("GF", new Vec2(3100, 0), 1500) };
        var s = ScoreCard.Score(ours, reference, ourWin, refWin, [], []);
        var (w, wi, _) = s.Total();
        Assert.Equal((1, 1, 1, 1), (w.Right, w.Off, w.Missed, w.Invented));
        Assert.Equal((0, 1, 1, 0), (wi.Right, wi.Off, wi.Missed, wi.Invented));
    }

    [Fact]
    public void Review_list_round_trips_with_ticks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2r-review-" + Guid.NewGuid().ToString("N")[..6]);
        var path = ReviewList.PathFor(dir, "Swallow Place: existing.rvt");
        var list = ReviewList.From("Swallow Place", new[] { new CheckItem("L01", CheckKind.UnreachableRoom, new Vec2(1500, -2500), "a room") }, []);
        list.Items[0].Done = true;
        list.Save(path);
        var back = ReviewList.Load(path)!;
        Assert.True(back.Items[0].Done);
        Assert.Equal(1500, back.Items[0].X);
        Assert.Equal("L01", back.Items[0].Level);
        Directory.Delete(dir, true);
    }
}
