using System.Text.Json;
using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.TestFit;

/// <summary>An existing wall on the floor: its centre line and thickness (mm, model plan coordinates).</summary>
public sealed record SiteWall(Vec2 A, Vec2 B, double ThicknessMm);

/// <summary>A room on the floor, by its name and outline (mm, model plan coordinates).</summary>
public sealed record SiteRoom(string Name, IReadOnlyList<Vec2> Outline);

/// <summary>
/// The floor picked in Revit, as the Office Test-Fit page reads it (window.TESTFIT_SITE in site.js).
/// The model works in millimetres with Y pointing north; the page works in metres with Y pointing down,
/// from the top-left corner of the floor outline. Origin records where that corner is in the model so a
/// layout can be put back in the same place.
/// </summary>
public sealed class TestFitSite
{
    public string Title { get; init; } = "";
    public string Level { get; init; } = "";
    /// <summary>Floor to floor height used for walls in the 3D view.</summary>
    public double StoreyHeightMm { get; init; } = 3000;
    /// <summary>Where true north points, in degrees clockwise from the top of the plan.</summary>
    public double NorthDegrees { get; init; }
    /// <summary>The floor's outline, one entry per slab: outer loop first, then its holes (stairs, voids).</summary>
    public List<List<IReadOnlyList<Vec2>>> Faces { get; } = new();
    public List<SiteWall> Walls { get; } = new();
    /// <summary>Columns, piers and other solid objects standing on the floor, as plan boxes.</summary>
    public List<(Vec2 Min, Vec2 Max)> Columns { get; } = new();
    public List<SiteRoom> Rooms { get; } = new();
    /// <summary>What the outline was taken from, for the report ("floor slabs", "rooms" or "walls").</summary>
    public string Source { get; init; } = "";

    // Base-build rooms the layout must leave alone: they become "Core" spaces on the page.
    static readonly Regex CoreName = new(
        @"\b(stairs?|staircase|lifts?|elevators?|wc|wcs|w\.c\.|toilets?|lavs?|showers?|riser|risers|shafts?|ducts?|core|plant|cleaners?|accessible|disabled|escape|firefighting)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsCore(string roomName) => CoreName.IsMatch(roomName);

    /// <summary>The page's coordinates (metres, Y down) and the model point at the page's origin.</summary>
    public (Func<Vec2, double[]> ToPage, Vec2 OriginMm, double WidthM, double DepthM) Frame()
    {
        var pts = Faces.SelectMany(f => f).SelectMany(l => l).ToList();
        if (pts.Count == 0) throw new InvalidOperationException("The floor has no outline.");
        double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        var origin = new Vec2(x0, y1);
        return (p => new[] { Round((p.X - origin.X) / 1000), Round((origin.Y - p.Y) / 1000) }, origin, Round((x1 - x0) / 1000), Round((y1 - y0) / 1000));
    }

    static double Round(double m) => Math.Round(m, 3);

    /// <summary>The content of site.js.</summary>
    public string ToScript()
    {
        var (page, origin, w, d) = Frame();
        double[][] Loop(IReadOnlyList<Vec2> l) => l.Select(page).ToArray();
        var cores = Rooms.Where(r => r.Outline.Count >= 3 && IsCore(r.Name)).Select(r =>
        {
            var a = r.Outline.Select(page).ToList();
            double rx0 = a.Min(p => p[0]), rx1 = a.Max(p => p[0]), ry0 = a.Min(p => p[1]), ry1 = a.Max(p => p[1]);
            return new { name = r.Name, x = rx0, y = ry0, w = Round(rx1 - rx0), d = Round(ry1 - ry0) };
        }).Where(c => c.w > 0.3 && c.d > 0.3).ToList();
        var site = new
        {
            title = Title,
            level = Level,
            w,
            d,
            h = Round(StoreyHeightMm / 1000),
            north = Math.Round(NorthDegrees, 2),
            origin = new { x = Round(origin.X / 1000), y = Round(origin.Y / 1000) },
            source = Source,
            faces = Faces.Select(f => f.Select(Loop).ToArray()).ToArray(),
            walls = Walls.Where(x => x.A.DistanceTo(x.B) > 10).Select(x =>
            {
                var (a, b) = (page(x.A), page(x.B));
                return new { x1 = a[0], y1 = a[1], x2 = b[0], y2 = b[1], t = Round(x.ThicknessMm / 1000) };
            }).ToArray(),
            cols = Columns.Select(c =>
            {
                var (a, b) = (page(new Vec2(c.Min.X, c.Max.Y)), page(new Vec2(c.Max.X, c.Min.Y)));
                return new { x = a[0], y = a[1], w = Round(b[0] - a[0]), h = Round(b[1] - a[1]) };
            }).ToArray(),
            rooms = Rooms.Where(r => r.Outline.Count >= 3).Select(r => new { name = r.Name, pts = Loop(r.Outline) }).ToArray(),
            cores,
        };
        var json = JsonSerializer.Serialize(site, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default });
        return "window.TESTFIT_SITE=" + json + ";\n";
    }
}
