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
}
