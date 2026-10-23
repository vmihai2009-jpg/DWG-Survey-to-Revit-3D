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

    static async Task<(string Id, (string, string) Axes)> Find(HttpClient http, string service, string want, CancellationToken cancel)
    {
        var ids = CoverageIds(await http.GetStringAsync(CapabilitiesUrl(service), cancel));
        var id = ids.FirstOrDefault(i => i.Contains(want, StringComparison.OrdinalIgnoreCase)) ?? ids.FirstOrDefault()
                 ?? throw new InvalidDataException($"The Environment Agency service '{service}' lists no data.");
        return (id, AxisLabels(await http.GetStringAsync(DescribeUrl(service, id), cancel)));
    }

    /// <summary>Reads an answer, reporting the bytes received so far (so a slow download shows it is moving).</summary>
    internal static async Task<byte[]> Read(HttpResponseMessage response, IProgress<long>? progress, CancellationToken cancel)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            buffer.Write(chunk, 0, n);
            progress?.Report(buffer.Length);
        }
        return buffer.ToArray();
    }

    static async Task<byte[]> Get(HttpClient http, string url, IProgress<long>? progress, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        var bytes = await Read(response, progress, cancel);
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
    public static async Task<string> Download(HttpClient http, string service, double minE, double minN, double maxE, double maxN, string folder,
        IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        var (id, axes) = await Find(http, service, "Elevation", cancel);
        var bytes = await Get(http, CoverageUrl(service, id, axes, minE, minN, maxE, maxN, "image/tiff"), progress, cancel);
        Directory.CreateDirectory(folder);
        var kind = service == Dtm ? "DTM" : "DSM";
        var path = Path.Combine(folder, $"EA_LIDAR_Composite_{kind}_1m_E{minE:0}_N{minN:0}_{maxE - minE:0}m.tif");
        await File.WriteAllBytesAsync(path, bytes);
        // the service's TIFF may carry no map position: then a world file (.tfw) from the area asked for
        var (w, h, placed) = GeoTiff.Probe(path);
        if (!placed && w > 0 && h > 0) await File.WriteAllTextAsync(Path.ChangeExtension(path, ".tfw"), WorldFileText(minE, maxN, (maxE - minE) / w));
        return path;
    }

    /// <summary>World file lines (A D B E C F) for a north-up grid whose top-left corner is (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public static string WorldFileText(double left, double top, double cell)
    {
        string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        return string.Join("\r\n", F(cell), "0", "0", F(-cell), F(left + cell / 2), F(top - cell / 2)) + "\r\n";
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
