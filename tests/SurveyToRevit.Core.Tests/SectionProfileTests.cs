using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class SectionProfileTests
{
    static WallCandidate W(double x1, double y1, double x2, double y2, double t) =>
        new(new Segment2(new Vec2(x1, y1), new Vec2(x2, y2), "W"), t, t, Confidence.Medium, "W", "W");

    static SurveyLevel L(string code, double z, Polygon2 outline, params WallCandidate[] walls) =>
        new(code, "Floor " + code, double.Parse(code), z, null, null!, new WallDetectionResult(walls, Array.Empty<Segment2>(), Array.Empty<double>(), 0, 0, Array.Empty<double>()), outline, Array.Empty<UnclearRegion>());

    static readonly Polygon2 Box = new(new[] { new Vec2(0, 0), new Vec2(20000, 0), new Vec2(20000, 12000), new Vec2(0, 12000) });

    [Fact]
    public void Height_fit_rejects_a_mark_that_disagrees()
    {
        var f = SectionProfiles.Fit(new List<(double, double)> { (100, 40000), (3600, 43500), (6600, 46500), (5100, 45200) })!;
        Assert.Equal(1, f.A, 3);
        Assert.Equal(39900, f.B, 0);
        Assert.Single(f.Rejected);
        Assert.Equal(45200, f.Rejected[0][0]);
    }

    // a 20 x 12 m block, street front y = 0; cut along x = 5000 from y = -2000 to 14000; drawn mirrored (sheet x = 20000 - t)
    [Fact]
    public void A_mirrored_section_registers_and_its_street_end_reads_as_a_mansard()
    {
        var below = L("05", 40000, Box, W(0, 150, 20000, 150, 300), W(0, 4000, 20000, 4000, 100), W(0, 11850, 20000, 11850, 300));
        var skin = W(0, 1100, 20000, 1100, 100);
        var top = L("06", 43500, Box, skin, W(0, 11850, 20000, 11850, 300));
        var levels = new List<SurveyLevel> { below, top };
        double X(double t) => 20000 - t;
        Segment2 V(double t, double z0, double z1) => new(new Vec2(X(t), z0), new Vec2(X(t), z1), "S");
        var lines = new List<Segment2>
        {
            V(2000, 40000, 43500), V(2300, 40000, 43500),                // street wall
            new(new Vec2(X(2000), 43500), new Vec2(X(3360), 46500), "S"), // mansard skin, 66°
            V(5950, 40000, 43500), V(6050, 40000, 43500),                // partition
            V(13700, 40000, 46500), V(14000, 40000, 46500),              // rear wall, flat top
            new(new Vec2(X(3360), 46500), new Vec2(X(14000), 46500), "S"),
        };
        var marks = new List<(double, double)> { (40000, 40000), (43500, 43500), (46500, 46500) };
        var cut = new Segment2(new Vec2(5000, -2000), new Vec2(5000, 14000), "section", "A");
        var sec = new SurveySection("A", cut, new double[] { 40000, 43500, 46500 }, "S.dwg", "");
        var (entry, profiles) = SectionProfiles.Build(sec, lines, marks, levels);
        Assert.NotNull(entry.Registration);
        Assert.True(entry.Registration!.Mirrored);
        Assert.True(entry.Registration.Sure, entry.Registration.Why);
        var a = entry.Ends.Single(e => e.End == "A");
        Assert.Equal("Mansard", a.Class);
        Assert.Equal(0, a.OffsetMm!.Value, 0);
        Assert.Equal(2, profiles.Count);
        // and the facade-top classes take it: the street stretch is a mansard from the section, its skin line left out
        var calls = FacadeTops.Classify(levels, new List<Segment2>(), profiles: profiles);
        var street = calls.Single(c => c.Line[1] == 0 && c.Line[3] == 0);
        Assert.Equal("Mansard", street.Class);
        Assert.Equal("High", street.Confidence);
        Assert.DoesNotContain(skin, FacadeTops.LeaveOutSkins(top, calls, new List<Segment2>()));
    }

    [Fact]
    public void A_section_drawn_440_mm_out_keeps_its_profile_and_reports_the_offset()
    {
        var below = L("05", 40000, Box, W(0, 150, 20000, 150, 300), W(0, 4000, 20000, 4000, 100), W(0, 11850, 20000, 11850, 300));
        var top = L("06", 43500, Box, W(0, 11850, 20000, 11850, 300));
        Segment2 V(double t, double z0, double z1) => new(new Vec2(t, z0), new Vec2(t, z1), "S");
        var lines = new List<Segment2> { V(1560, 40000, 43500), V(2300, 40000, 43500), V(5950, 40000, 43500), V(6050, 40000, 43500), V(13700, 40000, 46500), V(14000, 40000, 46500) };
        var cut = new Segment2(new Vec2(5000, -2000), new Vec2(5000, 14000), "section", "A");
        var (entry, _) = SectionProfiles.Build(new SurveySection("A", cut, new double[0], "S.dwg", ""), lines, new List<(double, double)> { (40000, 40000), (46500, 46500) }, new List<SurveyLevel> { below, top });
        var a = entry.Ends.Single(e => e.End == "A");
        Assert.Equal(440, a.OffsetMm!.Value, 0);
        Assert.All(a.Profile.Where(p => p[1] < 43500), p => Assert.Equal(0, p[0], 0));
    }
}
