using SurveyToRevit.Core;
using SurveyToRevit.Core.Learning;
using Xunit;

public class LearningTests
{
    static WallExample Ex(string project, WallOutcome o, string layer = "BUILDINGS", string surveyor = "PCL", string conf = "Low", double t = 100) =>
        new() { Project = project, Surveyor = surveyor, Layer = layer, Confidence = conf, ThicknessMm = t, Outcome = o };

    [Fact]
    public void Ranking_drops_a_kind_people_nearly_always_delete_on_several_projects()
    {
        var ex = Enumerable.Range(0, 10).Select(i => Ex(i < 5 ? "A" : "B", i == 0 ? WallOutcome.Kept : WallOutcome.Deleted)).ToList();
        var m = WallModel.Train(ex);
        var w = new WallCandidate(new Segment2(new Vec2(0, 0), new Vec2(1000, 0), "BUILDINGS"), 100, 100, Confidence.Low, null, null);
        Assert.True(m.ShouldDrop("PCL", w));
        Assert.False(m.ShouldDrop("OTHER", w));          // another surveyor's habits are not this one's
        Assert.False(m.ShouldDrop("unknown", w));        // never for an unknown surveyor
    }

    [Fact]
    public void Ranking_needs_more_than_one_project()
    {
        var ex = Enumerable.Range(0, 12).Select(_ => Ex("A", WallOutcome.Deleted)).ToList();
        var w = new WallCandidate(new Segment2(new Vec2(0, 0), new Vec2(1000, 0), "BUILDINGS"), 100, 100, Confidence.Low, null, null);
        Assert.False(WallModel.Train(ex).ShouldDrop("PCL", w));
    }

    [Fact]
    public void Ranking_never_drops_sure_walls()
    {
        var ex = Enumerable.Range(0, 10).Select(i => Ex(i < 5 ? "A" : "B", WallOutcome.Deleted, conf: "High")).ToList();
        var w = new WallCandidate(new Segment2(new Vec2(0, 0), new Vec2(1000, 0), "BUILDINGS"), 100, 100, Confidence.High, null, null);
        Assert.False(WallModel.Train(ex).ShouldDrop("PCL", w));
    }

    [Fact]
    public void Profile_proposes_a_layer_deleted_everywhere()
    {
        var ex = Enumerable.Range(0, 24).Select(i => Ex(i % 2 == 0 ? "A" : "B", WallOutcome.Deleted, layer: "KERB")).ToList();
        var p = SurveyorProfiles.Propose(ex);
        Assert.Contains(p["PCL"], l => l.StartsWith("KERB = Other"));
    }

    [Fact]
    public void Tuning_overrides_a_tolerance_by_name()
    {
        var t = Tuning.Apply(Tolerances.Default, "{\"maxpiermm\": 4000}");
        Assert.Equal(4000, t.MaxPierMm);
        Assert.Equal(Tolerances.Default.Precision, t.Precision);
        Assert.Contains("MaxPierMm", Tuning.Names());
    }

    [Fact]
    public void Surveyor_from_iso_file_names()
    {
        Assert.Equal("PCL", SurveyorId.From(new[] { @"c:\x\000002-PCL-ZZZ-101-M2-G-01000-P02-S3.dwg" }, Array.Empty<string>()));
        Assert.Equal("unknown", SurveyorId.From(new[] { @"c:\x\29463B-1 (BASEMENT PLAN).dwg" }, Array.Empty<string>()));
    }
}
