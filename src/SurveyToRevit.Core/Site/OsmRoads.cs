using System.Globalization;
using System.Text.Json;

namespace SurveyToRevit.Core.Site;

/// <summary>A road centreline in British National Grid metres, with its carriageway width.</summary>
public sealed record RoadLine(string Kind, string? Name, double WidthM, IReadOnlyList<Vec2> Points);

/// <summary>
/// Road centrelines from OpenStreetMap (free, © OpenStreetMap contributors, ODbL) through the Overpass
/// service, converted to British National Grid. Each road gets a carriageway width from its own "width"
/// or "lanes" tag, or a usual width for its kind. Footpaths, steps and tunnels are left out: pavements
/// stay ground. OSM is a few metres from the Ordnance Survey grid at worst; the site builder clips the
/// roads to open ground, so they never run into a building.
/// </summary>
public static class OsmRoads
{
    /// <summary>Overpass servers, tried in turn (the main one first).</summary>
    public static readonly string[] Overpass =
    {
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://overpass.private.coffee/api/interpreter",
    };
    public const string FileSuffix = ".osm.json";

    public static string Query(double minE, double minN, double maxE, double maxN)
    {
        var (s, w) = Bng.ToWgs84(minE, minN);
        var (n, e) = Bng.ToWgs84(maxE, maxN);
        // the corners of a grid square are not the corners in latitude/longitude: a little extra all round
        string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
        return $"[out:json][timeout:40];way[\"highway\"]({F(Math.Min(s, n) - 0.0005)},{F(Math.Min(w, e) - 0.0008)},{F(Math.Max(s, n) + 0.0005)},{F(Math.Max(w, e) + 0.0008)});out geom;";
    }

    /// <summary>Downloads the roads for an area (grid metres) into <paramref name="folder"/>; returns the file.</summary>
    public static async Task<string> Download(HttpClient http, double minE, double minN, double maxE, double maxN, string folder,
        IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        // Overpass refuses requests that do not say who is asking (406/403): name the add-in, ask for JSON
        string? text = null;
        var errors = new List<string>();
        foreach (var server in Overpass)
        {
            // a busy server can keep a request waiting for minutes: give each one 45 s, then try the next
            using var each = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            each.CancelAfter(TimeSpan.FromSeconds(45));
            progress?.Report(new Uri(server).Host);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, server)
                {
                    Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", Query(minE, minN, maxE, maxN)) }),
                };
                request.Headers.UserAgent.ParseAdd($"SurveyToRevit/{typeof(OsmRoads).Assembly.GetName().Version} (Revit add-in; site context)");
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("*/*;q=0.5");
                using var response = await http.SendAsync(request, each.Token);
                var answer = await response.Content.ReadAsStringAsync(each.Token);
                if (response.IsSuccessStatusCode && answer.TrimStart().StartsWith("{")) { text = answer; break; }
                errors.Add($"{new Uri(server).Host}: {(int)response.StatusCode}");
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { errors.Add($"{new Uri(server).Host}: no answer in 45 s"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{new Uri(server).Host}: {ex.Message}"); }
        }
        if (text == null) throw new InvalidDataException("OpenStreetMap (Overpass) refused the request (" + string.Join("; ", errors) + ").");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Roads_E{minE:0}_N{minN:0}_{maxE - minE:0}m{FileSuffix}");
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    static readonly Dictionary<string, double> UsualWidth = new()
    {
        ["motorway"] = 14, ["trunk"] = 12, ["primary"] = 11, ["secondary"] = 9, ["tertiary"] = 8,
        ["motorway_link"] = 7, ["trunk_link"] = 7, ["primary_link"] = 7, ["secondary_link"] = 6, ["tertiary_link"] = 6,
        ["unclassified"] = 6, ["residential"] = 6, ["living_street"] = 5, ["service"] = 4, ["pedestrian"] = 6, ["road"] = 6, ["busway"] = 7,
    };

    /// <summary>Roads in an Overpass answer (JSON with geometry), in grid metres.</summary>
    public static List<RoadLine> Parse(string json)
    {
        var roads = new List<RoadLine>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("elements", out var elements)) return roads;
        foreach (var el in elements.EnumerateArray())
        {
            if (!el.TryGetProperty("tags", out var tags) || !el.TryGetProperty("geometry", out var geom)) continue;
            string? Tag(string k) => tags.TryGetProperty(k, out var v) ? v.GetString() : null;
            var kind = Tag("highway");
            if (kind == null || !UsualWidth.TryGetValue(kind, out var width)) continue;
            if (Tag("tunnel") is "yes" or "building_passage" || Tag("covered") == "yes" || Tag("area") == "yes") continue;
            if (int.TryParse(Tag("layer"), out var layer) && layer < 0) continue;
            if (Metres(Tag("width")) is double w && w is > 2 and < 40) width = w;
            else if (int.TryParse(Tag("lanes"), out var lanes) && lanes is > 0 and < 10) width = Math.Max(width, lanes * 3.25);
            var pts = geom.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object)
                .Select(p => Bng.FromWgs84(p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble()))
                .Select(g => new Vec2(g.E, g.N)).ToList();
            if (pts.Count >= 2) roads.Add(new RoadLine(kind, Tag("name"), width, pts));
        }
        return roads;
    }

    static double? Metres(string? s)
    {
        if (s == null) return null;
        s = s.Trim().TrimEnd('m').Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

/// <summary>
/// WGS84 latitude/longitude ↔ British National Grid (OSGB36) by the Ordnance Survey's Helmert transformation
/// and Transverse Mercator formulae: good to a few metres, enough to place map data (not survey data).
/// </summary>
public static class Bng
{
    const double A = 6377563.396, B = 6356256.909, F0 = 0.9996012717, E0 = 400000, N0 = -100000;
    static readonly double Lat0 = 49 * Math.PI / 180, Lon0 = -2 * Math.PI / 180;

    public static (double E, double N) FromWgs84(double latDeg, double lonDeg)
    {
        // WGS84 → cartesian
        double a = 6378137, b = 6356752.3142;
        double phi = latDeg * Math.PI / 180, lam = lonDeg * Math.PI / 180;
        double e2 = 1 - b * b / (a * a);
        double nu = a / Math.Sqrt(1 - e2 * Math.Sin(phi) * Math.Sin(phi));
        double x = nu * Math.Cos(phi) * Math.Cos(lam), y = nu * Math.Cos(phi) * Math.Sin(lam), z = (1 - e2) * nu * Math.Sin(phi);
        // Helmert WGS84 → OSGB36
        double tx = -446.448, ty = 125.157, tz = -542.060, s = 20.4894e-6;
        double rx = -0.1502 / 3600 * Math.PI / 180, ry = -0.2470 / 3600 * Math.PI / 180, rz = -0.8421 / 3600 * Math.PI / 180;
        double x2 = tx + (1 + s) * x - rz * y + ry * z;
        double y2 = ty + rz * x + (1 + s) * y - rx * z;
        double z2 = tz - ry * x + rx * y + (1 + s) * z;
        // cartesian → Airy 1830 latitude/longitude
        double ea2 = 1 - B * B / (A * A);
        double p = Math.Sqrt(x2 * x2 + y2 * y2);
        double lat = Math.Atan2(z2, p * (1 - ea2));
        for (int i = 0; i < 10; i++)
        {
            double v = A / Math.Sqrt(1 - ea2 * Math.Sin(lat) * Math.Sin(lat));
            lat = Math.Atan2(z2 + ea2 * v * Math.Sin(lat), p);
        }
        double lon = Math.Atan2(y2, x2);
        return Project(lat, lon);
    }

    /// <summary>Grid → WGS84 (by refining <see cref="FromWgs84"/>), for asking map services about a grid area.</summary>
    public static (double LatDeg, double LonDeg) ToWgs84(double e, double n)
    {
        double lat = 49 + (n + 100000) / 111200, lon = -2 + (e - 400000) / (111320 * Math.Cos(lat * Math.PI / 180));
        for (int i = 0; i < 20; i++)
        {
            var (ge, gn) = FromWgs84(lat, lon);
            double dE = e - ge, dN = n - gn;
            if (Math.Abs(dE) < 1e-4 && Math.Abs(dN) < 1e-4) break;
            lat += dN / 111200;
            lon += dE / (111320 * Math.Cos(lat * Math.PI / 180));
        }
        return (lat, lon);
    }

    static (double E, double N) Project(double phi, double lam)
    {
        double e2 = 1 - B * B / (A * A), n = (A - B) / (A + B);
        double sin = Math.Sin(phi), cos = Math.Cos(phi), tan = Math.Tan(phi);
        double nu = A * F0 / Math.Sqrt(1 - e2 * sin * sin);
        double rho = A * F0 * (1 - e2) / Math.Pow(1 - e2 * sin * sin, 1.5);
        double eta2 = nu / rho - 1;
        double dp = phi - Lat0, sp = phi + Lat0;
        double m = B * F0 * ((1 + n + 1.25 * n * n + 1.25 * n * n * n) * dp
                   - (3 * n + 3 * n * n + 21.0 / 8 * n * n * n) * Math.Sin(dp) * Math.Cos(sp)
                   + (15.0 / 8 * n * n + 15.0 / 8 * n * n * n) * Math.Sin(2 * dp) * Math.Cos(2 * sp)
                   - 35.0 / 24 * n * n * n * Math.Sin(3 * dp) * Math.Cos(3 * sp));
        double I = m + N0;
        double II = nu / 2 * sin * cos;
        double III = nu / 24 * sin * Math.Pow(cos, 3) * (5 - tan * tan + 9 * eta2);
        double IIIA = nu / 720 * sin * Math.Pow(cos, 5) * (61 - 58 * tan * tan + Math.Pow(tan, 4));
        double IV = nu * cos;
        double V = nu / 6 * Math.Pow(cos, 3) * (nu / rho - tan * tan);
        double VI = nu / 120 * Math.Pow(cos, 5) * (5 - 18 * tan * tan + Math.Pow(tan, 4) + 14 * eta2 - 58 * tan * tan * eta2);
        double dl = lam - Lon0;
        double N = I + II * dl * dl + III * Math.Pow(dl, 4) + IIIA * Math.Pow(dl, 6);
        double E = E0 + IV * dl + V * Math.Pow(dl, 3) + VI * Math.Pow(dl, 5);
        return (E, N);
    }
}
