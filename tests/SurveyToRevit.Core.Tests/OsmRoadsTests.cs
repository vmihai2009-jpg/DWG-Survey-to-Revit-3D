using SurveyToRevit.Core.Site;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class OsmRoadsTests
{
    [Fact]
    public void Wgs84_to_national_grid_lands_within_metres()
    {
        // Elizabeth Tower (Big Ben): about E 530268, N 179640
        var (e, n) = Bng.FromWgs84(51.500729, -0.124625);
        Assert.InRange(e, 530255, 530285);
        Assert.InRange(n, 179625, 179655);
        var (lat, lon) = Bng.ToWgs84(e, n);
        Assert.Equal(51.500729, lat, 6);
        Assert.Equal(-0.124625, lon, 6);
    }

    [Fact]
    public void Reads_roads_with_widths_and_skips_footpaths_and_tunnels()
    {
        const string json = """
            {"elements":[
             {"type":"way","tags":{"highway":"primary","name":"Regent Street"},"geometry":[{"lat":51.5130,"lon":-0.1400},{"lat":51.5140,"lon":-0.1405}]},
             {"type":"way","tags":{"highway":"residential","width":"7.5 m"},"geometry":[{"lat":51.5130,"lon":-0.1400},{"lat":51.5131,"lon":-0.1390}]},
             {"type":"way","tags":{"highway":"footway"},"geometry":[{"lat":51.5130,"lon":-0.1400},{"lat":51.5131,"lon":-0.1390}]},
             {"type":"way","tags":{"highway":"primary","tunnel":"yes"},"geometry":[{"lat":51.5130,"lon":-0.1400},{"lat":51.5131,"lon":-0.1390}]}
            ]}
            """;
        var roads = OsmRoads.Parse(json);
        Assert.Equal(2, roads.Count);
        Assert.Equal("Regent Street", roads[0].Name);
        Assert.Equal(11, roads[0].WidthM);
        Assert.Equal(7.5, roads[1].WidthM);
        Assert.InRange(roads[0].Points[0].X, 528000, 530000);
    }

    [Fact]
    public void Query_asks_for_the_area_in_latitude_and_longitude()
    {
        var q = OsmRoads.Query(528849, 181040, 529149, 181340);
        Assert.StartsWith("[out:json]", q);
        Assert.Contains("way[\"highway\"](51.51", q);
    }

    [Fact]
    public void Lidar_block_surface_is_closed_and_follows_the_roof()
    {
        // an L-shaped block on a 10 x 10 grid with a pitched roof (ridge along the middle row)
        int n = 10;
        var top = new double[n * n];
        var cells = new List<int>();
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                top[r * n + c] = 20 - Math.Abs(r - 4.5);
                if (c < 4 || r >= 6) cells.Add(r * n + c);
            }
        var mesh = SiteContextBuilder.Surface(cells, top, n, 0, 10, 10);
        // every edge is used once each way: a closed surface
        var edges = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in mesh.Triangles)
            foreach (var e in new[] { (a, b), (b, c), (c, a) }) edges[e] = edges.GetValueOrDefault(e) + 1;
        Assert.All(edges, e => Assert.Equal(1, edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1))));
        // the roof is higher in the middle than at the edges
        var tops = mesh.Points.Where(p => p.Z > 10000).ToList();
        Assert.True(tops.Max(p => p.Z) - tops.Min(p => p.Z) > 2000);
        Assert.Contains(mesh.Points, p => p.Z == 10000);
    }
}

public class OutlineRepairTests
{
    [Fact]
    public void A_self_crossing_outline_becomes_clean_parts()
    {
        // a bow tie: two triangles touching at a crossing
        var bow = new Polygon2(new[] { new Vec2(0, 0), new Vec2(10000, 10000), new Vec2(10000, 0), new Vec2(0, 10000) });
        var parts = OutlineRepair.Clean(bow);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.True(p.IsSimple()));
        Assert.InRange(parts.Sum(p => p.Area), 45e6, 55e6);
    }
}
