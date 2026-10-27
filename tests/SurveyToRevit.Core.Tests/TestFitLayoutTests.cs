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
}
