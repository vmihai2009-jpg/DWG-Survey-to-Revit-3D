using System.Globalization;
using System.Text;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;
using Xunit;

namespace SurveyToRevit.Core.Tests;

/// <summary>LiDAR surroundings: reading the grids, and blocks from synthetic DSM/DTM.</summary>
public class SiteContextTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("plain.tif")]
    [InlineData("lzw.tif")]
    [InlineData("deflate_tiled.tif")]
    [InlineData("deflate_pred3.tif")]
    [InlineData("lzw_pred3_big.tif")]
    [InlineData("grid.asc")]
    public void Height_grids_read_with_their_map_position(string file)
    {
        // fixtures: 40 x 30 one-metre cells, top-left E 529000 N 181300, value = 10 + 0.5 col + 0.25 row, one no-data cell at row 5 col 7
        var g = GeoRaster.Read(Fixture(file), 529000, 181270, 529040, 181300)!;
        Assert.Equal(40, g.Width);
        Assert.Equal(30, g.Height);
        Assert.Equal(10 + 0.5 * 12 + 0.25 * 20, g.At(529012.5, 181300 - 20.5), 3);
        Assert.True(double.IsNaN(g.At(529007.5, 181300 - 5.5)));
        Assert.True(double.IsNaN(g.At(528999, 181290)));
    }

    [Fact]
    public void Only_the_requested_box_is_kept()
    {
        var g = GeoRaster.Read(Fixture("deflate_tiled.tif"), 529020, 181280, 529030, 181290)!;
        Assert.Equal(10, g.Width);
        Assert.Equal(10, g.Height);
        Assert.Equal(10 + 0.5 * 25 + 0.25 * 15, g.At(529025.5, 181300 - 15.5), 3);
        Assert.Null(GeoRaster.Read(Fixture("lzw.tif"), 600000, 100000, 600010, 100010));
    }

    [Fact]
    public void Integer_grids_with_a_predictor_read()
    {
        var g = GeoRaster.Read(Fixture("int16_pred2.tif"), 529000, 181270, 529040, 181300)!;
        Assert.Equal((short)((10 + 0.5 * 12 + 0.25 * 20) * 10), g.At(529012.5, 181300 - 20.5), 0);
    }

    [Fact]
    public void File_names_say_which_model_a_tile_is() 
    {
        Assert.Equal("DSM", SiteContextBuilder.Kind("TQ2881_DSM_1M.tif"));
        Assert.Equal("DTM", SiteContextBuilder.Kind("lidar_composite_dtm_2022_1m_TQ28sw.tif"));
        Assert.Null(SiteContextBuilder.Kind("TQ2881.tif"));
    }

    [Fact]
    public void Neighbours_become_blocks_trees_do_not_and_the_surveyed_building_is_left_to_the_survey()
    {
        // 120 m square at 1 m around E 529000 N 181000; ground at 20 m
        const int n = 120; const double x0 = 528940, top = 181060;
        var dsm = new double[n, n]; var dtm = new double[n, n];
        var rng = new Random(1);
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                dtm[r, c] = 20; dsm[r, c] = 20;
                double x = x0 + c + 0.5, y = top - r - 0.5;
                if (x > 528990 && x < 529010 && y > 180990 && y < 181010) dsm[r, c] = 50;   // the surveyed building
                if (x > 528950 && x < 528970 && y > 181020 && y < 181050) dsm[r, c] = 35;   // neighbour, 15 m
                if (x > 528970 && x < 528980 && y > 181020 && y < 181050) dsm[r, c] = 29;   // lower wing, 9 m
                if (Math.Pow(x - 529040, 2) + Math.Pow(y - 180970, 2) < 25) dsm[r, c] = 26 + rng.NextDouble() * 6; // a tree
            }
        var dir = Directory.CreateTempSubdirectory().FullName;
        Write(Path.Combine(dir, "site_DSM.asc"), dsm, x0, top);
        Write(Path.Combine(dir, "site_DTM.asc"), dtm, x0, top);
        var frame = new SurveyFrame(new Vec2(529000_000, 181000_000), 0);
        var surveyed = new Polygon2(new[] { new Vec2(-10000, -10000), new Vec2(10000, -10000), new Vec2(10000, 10000), new Vec2(-10000, 10000) });

        var site = SiteContextBuilder.Build(new[] { Path.Combine(dir, "site_DSM.asc"), Path.Combine(dir, "site_DTM.asc") }, frame, new[] { surveyed }, radiusM: 60);

        Assert.Equal(2, site.Blocks.Count);
        var main = site.Blocks.Single(b => b.TopMm == 35000);
        var wing = site.Blocks.Single(b => b.TopMm == 29000);
        Assert.Equal(20000, main.BaseMm);
        Assert.InRange(main.Outline.Area, 590e6, 610e6);   // 20 x 30 m
        Assert.InRange(wing.Outline.Area, 290e6, 310e6);   // 10 x 30 m
        Assert.All(site.Blocks, b => Assert.All(b.Outline.Vertices, v => Assert.True(Math.Abs(v.X) > 10000 || Math.Abs(v.Y) > 10000)));
        Assert.Equal(50000, site.SurveyedRoofTopMm);
        Assert.Contains(site.Ground, g => g.ZMm == 20000);
    }

    [Fact]
    public void Footprints_give_exact_outlines_at_lidar_heights_and_stepped_ones_are_split()
    {
        const int n = 120; const double x0 = 528940, top = 181060;
        var dsm = new double[n, n]; var dtm = new double[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                dtm[r, c] = 20; dsm[r, c] = 20;
                double x = x0 + c + 0.5, y = top - r - 0.5;
                if (x > 528990 && x < 529010 && y > 180990 && y < 181010) dsm[r, c] = 50;
                if (x > 528950 && x < 528970 && y > 181020 && y < 181050) dsm[r, c] = 35;
                if (x > 528970 && x < 528980 && y > 181020 && y < 181050) dsm[r, c] = 29;
                if (x > 529020.3 && x < 529032.7 && y > 180960.2 && y < 180975.6) dsm[r, c] = 32; // flat roof, not on the 1 m grid
            }
        var dir = Directory.CreateTempSubdirectory().FullName;
        Write(Path.Combine(dir, "site_DSM.asc"), dsm, x0, top);
        Write(Path.Combine(dir, "site_DTM.asc"), dtm, x0, top);
        // clockwise rings, as shapefiles store outlines
        var shp = Path.Combine(dir, "buildings.shp");
        WriteShp(shp, new[]
        {
            Rect(528950, 181020, 528980, 181050),          // one footprint, two roof heights
            Rect(529020.3, 180960.2, 529032.7, 180975.6),  // exact outline
            Rect(528990, 180990, 529010, 181010),          // the surveyed building
        });
        var frame = new SurveyFrame(new Vec2(529000_000, 181000_000), 0);
        var surveyed = new Polygon2(new[] { new Vec2(-10000, -10000), new Vec2(10000, -10000), new Vec2(10000, 10000), new Vec2(-10000, 10000) });

        var site = SiteContextBuilder.Build(new[] { Path.Combine(dir, "site_DSM.asc"), Path.Combine(dir, "site_DTM.asc"), shp }, frame, new[] { surveyed }, radiusM: 60);

        Assert.Equal(3, site.Blocks.Count);
        var exact = site.Blocks.Single(b => b.TopMm == 32000);
        Assert.Equal(12.4 * 15.4 * 1e6, exact.Outline.Area, 1);
        Assert.Contains(exact.Outline.Vertices, v => Math.Abs(v.X - 20300) < 1 && Math.Abs(v.Y + 39800) < 1);
        Assert.Single(site.Blocks, b => b.TopMm == 35000);
        Assert.Single(site.Blocks, b => b.TopMm == 29000);
    }

    static Vec2[] Rect(double x0, double y0, double x1, double y1) =>
        new[] { new Vec2(x0, y0), new Vec2(x0, y1), new Vec2(x1, y1), new Vec2(x1, y0), new Vec2(x0, y0) };

    static void WriteShp(string path, IEnumerable<Vec2[]> rings)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Big(int v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); w.Write(b); }
        Big(9994); for (int i = 0; i < 5; i++) Big(0); Big(0); w.Write(1000); w.Write(5);
        for (int i = 0; i < 8; i++) w.Write(0.0);
        int id = 1;
        foreach (var ring in rings)
        {
            Big(id++); Big((4 + 32 + 4 + 4 + 4 + ring.Length * 16) / 2);
            w.Write(5);
            w.Write(ring.Min(p => p.X)); w.Write(ring.Min(p => p.Y)); w.Write(ring.Max(p => p.X)); w.Write(ring.Max(p => p.Y));
            w.Write(1); w.Write(ring.Length); w.Write(0);
            foreach (var p in ring) { w.Write(p.X); w.Write(p.Y); }
        }
        File.WriteAllBytes(path, ms.ToArray());
    }

    static void Write(string path, double[,] v, double x0, double top)
    {
        int n = v.GetLength(0);
        var sb = new StringBuilder($"ncols {n}\nnrows {n}\nxllcorner {x0.ToString(CultureInfo.InvariantCulture)}\nyllcorner {(top - n).ToString(CultureInfo.InvariantCulture)}\ncellsize 1\nNODATA_value -9999\n");
        for (int r = 0; r < n; r++)
            sb.AppendLine(string.Join(' ', Enumerable.Range(0, n).Select(c => v[r, c].ToString("0.00", CultureInfo.InvariantCulture))));
        File.WriteAllText(path, sb.ToString());
    }
}
