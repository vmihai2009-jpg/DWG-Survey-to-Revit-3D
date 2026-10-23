using SurveyToRevit.Launcher;

namespace SurveyToRevit.Core.Tests;

public class ToolSlotsTests
{
    [Fact]
    public void Tools_keep_their_slot_and_new_ones_fill_hidden_slots_first()
    {
        var start = ToolSlots.Assign([], ["review", "score", "testfit"]);
        Assert.Equal(new[] { "review", "score", "testfit" }, start);
        // score dropped, layout added: layout reuses score's hidden slot, the others stay put
        var next = ToolSlots.Assign(start, ["review", "testfit", "layout"]);
        Assert.Equal(new[] { "review", "layout", "testfit" }, next);
        // an older engine without layout: its slot is hidden
        Assert.Equal(new string?[] { "review", null, "testfit" }, ToolSlots.Assign(next, ["review", "testfit"]));
    }

    [Fact]
    public void No_more_than_the_slots_there_are()
    {
        var many = Enumerable.Range(0, 15).Select(i => "t" + i).ToList();
        Assert.Equal(ToolSlots.Capacity, ToolSlots.Assign([], many).Length);
    }
}
