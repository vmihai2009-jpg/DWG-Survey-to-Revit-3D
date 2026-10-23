using System.Globalization;
using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Site;

/// <summary>
/// Downloads LiDAR for the site straight from the Environment Agency's free web service (WCS, Open
/// Government Licence): the LIDAR Composite DSM and DTM at 1 m, which cover about 99% of England, cut to
/// a square around the survey in British National Grid (EPSG:27700). The service's coverage names and
/// axis labels are read from the service itself each time, so a renamed coverage does not break it.
/// </summary>
public static class LidarService
{
    public const string Root = "https://environment.data.gov.uk/spatialdata/";
    public const string Dsm = "lidar-composite-digital-surface-model-last-return-dsm-1m";
    public const string Dtm = "lidar-composite-digital-terrain-model-dtm-1m";

    public static string CapabilitiesUrl(string service) => $"{Root}{service}/wcs?service=WCS&version=2.0.1&request=GetCapabilities";
    public static string DescribeUrl(string service, string id) => $"{Root}{service}/wcs?service=WCS&version=2.0.1&request=DescribeCoverage&CoverageId={Uri.EscapeDataString(id)}";

    public static string CoverageUrl(string service, string id, (string X, string Y) axes, double minE, double minN, double maxE, double maxN, string format)
    {
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return $"{Root}{service}/wcs?service=WCS&version=2.0.1&request=GetCoverage&CoverageId={Uri.EscapeDataString(id)}" +
               $"&format={Uri.EscapeDataString(format)}&subset={axes.X}({F(minE)},{F(maxE)})&subset={axes.Y}({F(minN)},{F(maxN)})";
    }

    /// <summary>Coverage names in a WCS GetCapabilities answer.</summary>
    public static List<string> CoverageIds(string capabilities) =>
        Regex.Matches(capabilities, @"<(?:\w+:)?CoverageId>\s*([^<]+?)\s*</").Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>The coverage's two axis names (e.g. "E N" or "x y") from DescribeCoverage; easting first.</summary>
    public static (string X, string Y) AxisLabels(string describe)
    {
        var m = Regex.Match(describe, @"axisLabels=""([^""]+)""");
        var parts = m.Success ? m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        return parts.Length >= 2 ? (parts[0], parts[1]) : ("E", "N");
    }

    static async Task<(string Id, (string, string) Axes)> Find(HttpClient http, string service, string want)
    {
        var ids = CoverageIds(await http.GetStringAsync(CapabilitiesUrl(service)));
        var id = ids.FirstOrDefault(i => i.Contains(want, StringComparison.OrdinalIgnoreCase)) ?? ids.FirstOrDefault()
                 ?? throw new InvalidDataException($"The Environment Agency service '{service}' lists no data.");
        return (id, AxisLabels(await http.GetStringAsync(DescribeUrl(service, id))));
    }

    static async Task<byte[]> Get(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!response.IsSuccessStatusCode || type.Contains("xml"))
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var reason = Regex.Match(text, @"<(?:\w+:)?ExceptionText>([^<]+)<").Groups[1].Value;
            throw new InvalidDataException($"The Environment Agency service refused the request ({(int)response.StatusCode}): {(reason.Length > 0 ? reason : text[..Math.Min(200, text.Length)])}");
        }
        return bytes;
    }

    /// <summary>Downloads the DSM or DTM (<paramref name="service"/>) for an area (grid metres) into a GeoTIFF in <paramref name="folder"/>.</summary>
    public static async Task<string> Download(HttpClient http, string service, double minE, double minN, double maxE, double maxN, string folder)
    {
        var (id, axes) = await Find(http, service, "Elevation");
        var bytes = await Get(http, CoverageUrl(service, id, axes, minE, minN, maxE, maxN, "image/tiff"));
        Directory.CreateDirectory(folder);
        var kind = service == Dtm ? "DTM" : "DSM";
        var path = Path.Combine(folder, $"EA_LIDAR_Composite_{kind}_1m_E{minE:0}_N{minN:0}_{maxE - minE:0}m.tif");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    /// <summary>
    /// A shaded-relief picture of a height grid (light from the north-west), as grey levels row by row from
    /// the north; cells with no data are white.
    /// </summary>
    public static byte[] ShadedRelief(GeoRaster g)
    {
        var px = new byte[g.Width * g.Height];
        double az = 315 * Math.PI / 180, alt = 45 * Math.PI / 180, c = g.CellSize;
        double H(int col, int row) => g.At(g.OriginX + (Math.Clamp(col, 0, g.Width - 1) + 0.5) * c, g.OriginY - (Math.Clamp(row, 0, g.Height - 1) + 0.5) * c);
        for (int r = 0; r < g.Height; r++)
            for (int k = 0; k < g.Width; k++)
            {
                double dx = (H(k + 1, r) - H(k - 1, r)) / (2 * c), dy = (H(k, r - 1) - H(k, r + 1)) / (2 * c);
                if (double.IsNaN(dx) || double.IsNaN(dy)) { px[r * g.Width + k] = 255; continue; }
                double slope = Math.Atan(Math.Sqrt(dx * dx + dy * dy)), aspect = Math.Atan2(-dx, -dy);
                double shade = Math.Sin(alt) * Math.Cos(slope) + Math.Cos(alt) * Math.Sin(slope) * Math.Cos(az - aspect);
                px[r * g.Width + k] = (byte)Math.Clamp(60 + 195 * shade, 0, 255);
            }
        return px;
    }
}
