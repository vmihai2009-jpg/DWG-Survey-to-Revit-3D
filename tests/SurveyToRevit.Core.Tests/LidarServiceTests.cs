using SurveyToRevit.Core.Site;
using Xunit;

namespace SurveyToRevit.Core.Tests;

public class LidarServiceTests
{
    // shape of the Environment Agency's answers (checked against the live DTM service, 2026-10-23)
    const string Capabilities = """
        <wcs:Capabilities xmlns:wcs="http://www.opengis.net/wcs/2.0"><wcs:Contents>
        <wcs:CoverageSummary><wcs:CoverageId>13787b9a-26a4-4775-8523-806d13af58fc__Lidar_Composite_Elevation_DTM_1m</wcs:CoverageId></wcs:CoverageSummary>
        <wcs:CoverageSummary><wcs:CoverageId>13787b9a-26a4-4775-8523-806d13af58fc__Lidar_Composite_Hillshade_DTM_1m</wcs:CoverageId></wcs:CoverageSummary>
        </wcs:Contents></wcs:Capabilities>
        """;
    const string Describe = """
        <gml:Envelope srsName="http://www.opengis.net/def/crs/EPSG/0/27700" axisLabels="E N" srsDimension="2">
        <gml:lowerCorner>80000.0 4000.0</gml:lowerCorner></gml:Envelope>
        <gml:RectifiedGrid dimension="2" gml:id="grid" axisLabels="i j"/>
        """;

    [Fact]
    public void Reads_coverage_names_and_axes()
    {
        var ids = LidarService.CoverageIds(Capabilities);
        Assert.Equal(2, ids.Count);
        Assert.EndsWith("Elevation_DTM_1m", ids[0]);
        Assert.Equal(("E", "N"), LidarService.AxisLabels(Describe));
        Assert.Equal(("E", "N"), LidarService.AxisLabels("<nothing/>"));
    }

    [Fact]
    public void Builds_the_download_url_for_a_square()
    {
        var url = LidarService.CoverageUrl(LidarService.Dtm, "a__Lidar_Composite_Elevation_DTM_1m", ("E", "N"), 528849, 181040, 529149, 181340, "image/tiff");
        Assert.StartsWith("https://environment.data.gov.uk/spatialdata/lidar-composite-digital-terrain-model-dtm-1m/wcs?", url);
        Assert.Contains("request=GetCoverage", url);
        Assert.Contains("format=image%2Ftiff", url);
        Assert.Contains("subset=E(528849,529149)", url);
        Assert.Contains("subset=N(181040,181340)", url);
    }

    [Fact]
    public void Shaded_relief_lights_slopes_facing_north_west()
    {
        // a ridge running north-south: west face lit, east face in shade, gaps white
        int w = 20, h = 10;
        var v = new float[w * h];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                v[r * w + c] = 10 - Math.Abs(c - 10);
        v[0] = float.NaN;
        var px = LidarService.ShadedRelief(new GeoRaster(0, 10, 1, w, h, v));
        Assert.True(px[5 * w + 5] > px[5 * w + 15] + 50);
        Assert.Equal(255, px[0]);
    }

    [Fact]
    public void A_tiff_without_map_position_is_placed_by_its_world_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // 4 x 3 float grid, no GeoTIFF tags (as the service may send it)
            var path = Path.Combine(dir, "EA_LIDAR_Composite_DTM_1m.tif");
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write((ushort)0x4949); bw.Write((ushort)42); bw.Write(8u);
                var tags = new (ushort Tag, ushort Type, uint Value)[] { (256, 3, 4), (257, 3, 3), (258, 3, 32), (259, 3, 1), (273, 4, 8 + 2 + 8 * 12 + 4), (277, 3, 1), (279, 4, 48), (339, 3, 3) };
                bw.Write((ushort)tags.Length);
                foreach (var t in tags) { bw.Write(t.Tag); bw.Write(t.Type); bw.Write(1u); if (t.Type == 3) { bw.Write((ushort)t.Value); bw.Write((ushort)0); } else bw.Write(t.Value); }
                bw.Write(0u);
                for (int i = 0; i < 12; i++) bw.Write((float)i);
            }
            Assert.False(GeoTiff.Probe(path).Placed);
            Assert.Equal((4, 3), (GeoTiff.Probe(path).Width, GeoTiff.Probe(path).Height));
            File.WriteAllText(Path.ChangeExtension(path, ".tfw"), LidarService.WorldFileText(528900, 181200, 1));
            var g = GeoRaster.Read(path, 528000, 181000, 530000, 182000)!;
            Assert.Equal(528900, g.OriginX, 6);
            Assert.Equal(181200, g.OriginY, 6);
            Assert.Equal(5, g.At(528901.5, 181198.5));   // row 1, column 1
        }
        finally { Directory.Delete(dir, true); }
    }
}
