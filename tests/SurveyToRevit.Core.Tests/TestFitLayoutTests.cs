using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Core.Tests;

public class TestFitLayoutTests
{
    const string Json = """
        {"kind":"testfit-layout","version":1,"desks":1,"nia":120.5,
         "items":[{"k":"desk","cat":"desks","zone":"z1","x":2,"y":3,"w":1.6,"d":0.8,"fx":0,"fy":1},
                  {"k":"chair","cat":"desks","zone":"z1","x":2,"y":3.8,"w":0.5,"d":0.5,"fx":0,"fy":-1},
                  {"k":"arm","cat":"soft","zone":"z2","x":5,"y":5,"w":0.8,"d":0.8,"fx":1,"fy":0}],
         "walls":[{"x1":0,"y1":0,"x2":4,"y2":0,"part":"glazed","zone":"z3"}],
         "doors":[{"x":1,"y":0,"dx":1,"dy":0,"nx":0,"ny":1,"w":0.9,"part":"solid","zone":"z3"}],
         "zones":[{"id":"z3","name":"Meeting 01","type":"meeting","part":"glazed","x":0,"y":0,"w":4,"d":3}]}
        """;

    [Fact]
    public void Page_metres_become_model_millimetres_with_y_up()
    {
        var l = TestFitLayout.Parse(Json, new Vec2(10000, 50000));
        Assert.Equal(new Vec2(12000, 47000), l.Items[0].Center);
        Assert.Equal(1600, l.Items[0].WidthMm, 6);
        Assert.Equal(new Vec2(10000, 50000), l.Walls[0].A);
        Assert.True(l.Walls[0].Glazed);
        var door = l.Doors[0];
        Assert.Equal(new Vec2(0, -1), door.Into);      // page down = model south
        Assert.Equal(new Vec2(11450, 50000), door.Center);
        var room = Assert.Single(l.Zones);
        Assert.True(room.Enclosed);
        Assert.Equal(new Vec2(12000, 48500), room.Center);
        Assert.Equal(1, l.Desks);
    }

    [Fact]
    public void A_drawn_polygon_keeps_its_outline_and_room_point()
    {
        const string poly = """
            {"kind":"testfit-layout","zones":[{"id":"z9","name":"Meeting L","type":"meeting","part":"solid","x":0,"y":0,"w":4,"d":4,
              "pts":[[0,0],[4,0],[4,2],[2,2],[2,4],[0,4]],"ix":1,"iy":3}]}
            """;
        var z = Assert.Single(TestFitLayout.Parse(poly, new Vec2(0, 0)).Zones);
        Assert.Equal(6, z.Outline!.Count);
        Assert.Equal(new Vec2(4000, -2000), z.Outline[2]);
        Assert.Equal(new Vec2(1000, -3000), z.RoomPoint);
    }

    [Fact]
    public void Furniture_turns_so_its_front_faces_the_way_the_page_drew_it()
    {
        var l = TestFitLayout.Parse(Json, new Vec2(0, 0));
        // a family faces -Y; turned by Rotation (anticlockwise) its front must point where the page says
        static Vec2 Front(double r) => new(Math.Sin(r), -Math.Cos(r));
        Assert.Equal(0, Front(l.Items[0].Rotation).X, 6);   // desk faces page-down = model -Y
        Assert.Equal(-1, Front(l.Items[0].Rotation).Y, 6);
        Assert.Equal(1, Front(l.Items[1].Rotation).Y, 6);   // its chair faces back at it
        Assert.Equal(1, Front(l.Items[2].Rotation).X, 6);   // the armchair faces east
    }

    [Fact]
    public void Dividers_assets_the_summary_and_not_nia_spaces_come_through()
    {
        const string json = """
            {"kind":"testfit-layout","sheet":false,
             "walls":[{"x1":0,"y1":0,"x2":0,"y2":3,"part":"divider","zone":"z2"}],
             "zones":[{"id":"z2","name":"Plant","type":"soft","part":"open","x":0,"y":0,"w":2,"d":2,"a":4,"seats":3,"nonia":true}],
             "assets":{"work:desk":{"id":"f:C:/lib/M_Desk.rfa","type":"1600x800","rot":90},"soft:sofa":{"id":""}},
             "summary":{"nia":600,"gross":700,"notNia":40,"desks":50,"target":60,"density":10,"achieved":12,"band":"Standard",
               "seats":{"desks":50,"meeting":8},"totalSeats":58,
               "mix":[{"type":"work","label":"Workspace","m2":400,"pct":66.7}],
               "partitions":{"glazed":20.5,"solid":10,"doors":3},
               "checks":[{"level":"OK","text":"All fit."}]}}
            """;
        var l = TestFitLayout.Parse(json, new Vec2(0, 0));
        Assert.True(l.Walls[0].Divider);
        Assert.False(l.Walls[0].Glazed);
        var z = Assert.Single(l.Zones);
        Assert.True(z.NotNia);
        Assert.Equal(3, z.Seats);
        Assert.False(l.Sheet);
        var a = Assert.Single(l.Assets); // one with no id is not a pick
        Assert.Equal(("f:C:/lib/M_Desk.rfa", "1600x800", 90.0), a.Value);
        var s = l.Summary!;
        Assert.Equal(600, s.Nia);
        Assert.Equal(60, s.Target);
        Assert.Equal(2, s.Seats.Count);
        Assert.Equal("Workspace", s.Mix[0].Label);
        Assert.Equal(3, s.Doors);
        Assert.Equal("All fit.", s.Checks[0].Text);
    }
}
