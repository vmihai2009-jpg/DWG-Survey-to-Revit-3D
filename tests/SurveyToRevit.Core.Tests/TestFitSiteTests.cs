using System.Text.Json;
using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Core.Tests;

public class TestFitSiteTests
{
    static IReadOnlyList<Vec2> Box(double x0, double y0, double x1, double y1) =>
        [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    static JsonElement Site(TestFitSite s)
    {
        var js = s.ToScript();
        Assert.StartsWith("window.TESTFIT_SITE=", js);
        return JsonDocument.Parse(js["window.TESTFIT_SITE=".Length..].TrimEnd(';', '\n')).RootElement;
    }

    [Fact]
    public void Page_is_in_metres_from_the_top_left_with_y_down()
    {
        var s = new TestFitSite { Title = "T", Level = "L3" };
        s.Faces.Add([Box(5000, 2000, 45000, 27000), Box(20000, 10000, 23000, 15000)]);
        s.Walls.Add(new SiteWall(new Vec2(5000, 27000), new Vec2(45000, 27000), 300));
        s.Columns.Add((new Vec2(12300, 24300), new Vec2(12700, 24700)));
        var j = Site(s);
        Assert.Equal(40, j.GetProperty("w").GetDouble());
        Assert.Equal(25, j.GetProperty("d").GetDouble());
        Assert.Equal(5, j.GetProperty("origin").GetProperty("x").GetDouble());
        Assert.Equal(27, j.GetProperty("origin").GetProperty("y").GetDouble());
        var hole = j.GetProperty("faces")[0][1];
        Assert.Equal(15, hole[0][0].GetDouble());   // x 20 m - 5 m
        Assert.Equal(17, hole[0][1].GetDouble());   // 27 m - y 10 m
        var wall = j.GetProperty("walls")[0];
        Assert.Equal(0, wall.GetProperty("y1").GetDouble()); // the north wall is at the top
        Assert.Equal(0.3, wall.GetProperty("t").GetDouble());
        var col = j.GetProperty("cols")[0];
        Assert.Equal(7.3, col.GetProperty("x").GetDouble(), 3);
        Assert.Equal(2.3, col.GetProperty("y").GetDouble(), 3);
        Assert.Equal(0.4, col.GetProperty("w").GetDouble(), 3);
        Assert.Equal(0.4, col.GetProperty("h").GetDouble(), 3);
    }

    [Fact]
    public void Stairs_lifts_and_toilets_become_core_spaces()
    {
        Assert.True(TestFitSite.IsCore("Stair 2"));
        Assert.True(TestFitSite.IsCore("Female WC"));
        Assert.True(TestFitSite.IsCore("LIFT LOBBY"));
        Assert.False(TestFitSite.IsCore("Office"));
        Assert.False(TestFitSite.IsCore("Meeting Room"));
        var s = new TestFitSite();
        s.Faces.Add([Box(0, 0, 20000, 10000)]);
        s.Rooms.Add(new SiteRoom("Stair", Box(0, 0, 3000, 5000)));
        s.Rooms.Add(new SiteRoom("Office", Box(3000, 0, 20000, 10000)));
        var cores = Site(s).GetProperty("cores");
        Assert.Equal(1, cores.GetArrayLength());
        Assert.Equal(5, cores[0].GetProperty("y").GetDouble()); // 10 m - 5 m
        Assert.Equal(3, cores[0].GetProperty("w").GetDouble());
    }

    [Fact]
    public void Names_cannot_break_out_of_the_script()
    {
        var s = new TestFitSite { Title = "</script><script>alert(1)</script>" };
        s.Faces.Add([Box(0, 0, 1000, 1000)]);
        Assert.DoesNotContain("</script>", s.ToScript());
    }
}
