using System.IO.Compression;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Site;

/// <summary>
/// The surveyed building as the LiDAR sees it: cells standing 2.5 m or more above the ground (DSM minus DTM).
/// The LiDAR outline is only good to about a metre, too coarse for a balcony, but it catches gross errors:
/// an outside wall standing well clear of anything the LiDAR saw built (a neighbour's wall, a garden wall,
/// a roof outline on a wall layer). Upper-floor outside walls are checked; the ground floor is left out
/// (shopfronts, canopies and areas under overhangs read as open ground from above).
/// </summary>
public static class LidarEnvelope
{
    static readonly System.Text.RegularExpressions.Regex GroundOrBelow = new(@"ground|basement|cellar|vault", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static List<CheckItem> Check(IEnumerable<string> files, SurveyFrame frame, IReadOnlyList<SurveyLevel> levels, double clearMm = 1500)
    {
        var items = new List<CheckItem>();
        var outlines = levels.Where(l => l.Outline != null).Select(l => l.Outline!).ToList();
        if (outlines.Count == 0) return items;
        // survey grid box round the building, 20 m margin
        var corners = outlines.SelectMany(o => o.Vertices).Select(frame.ToSurvey).ToList();
        double minX = corners.Min(p => p.X) / 1000 - 20, maxX = corners.Max(p => p.X) / 1000 + 20;
        double minY = corners.Min(p => p.Y) / 1000 - 20, maxY = corners.Max(p => p.Y) / 1000 + 20;
        var dsm = new List<GeoRaster>(); var dtm = new List<GeoRaster>();
        var temp = new List<string>();
        try
        {
        foreach (var f in Expand(files, temp))
        {
            var kind = SiteContextBuilder.Kind(f);
            if (kind is not ("DSM" or "DTM")) continue;
            try { if (GeoRaster.Read(f, minX, minY, maxX, maxY) is GeoRaster r) (kind == "DSM" ? dsm : dtm).Add(r); }
            catch { /* unreadable tile: the surroundings report it */ }
        }
        }
        finally { foreach (var d in temp) try { Directory.Delete(d, true); } catch { } }
        return Check(dsm, dtm, frame, levels, clearMm);
    }

    /// <summary>The check against grids already read (metres, survey grid).</summary>
    public static List<CheckItem> Check(IReadOnlyList<GeoRaster> dsm, IReadOnlyList<GeoRaster> dtm, SurveyFrame frame, IReadOnlyList<SurveyLevel> levels, double clearMm = 1500)
    {
        var items = new List<CheckItem>();
        if (dsm.Count == 0 || dtm.Count == 0) return items;
        double Height(GeoRaster[] list, Vec2 surveyMm)
        {
            foreach (var r in list) { double v = r.At(surveyMm.X / 1000, surveyMm.Y / 1000); if (!double.IsNaN(v)) return v; }
            return double.NaN;
        }
        var top = dsm.ToArray(); var ground = dtm.ToArray();
        // standing (built) within a metre of a model point: the LiDAR cell is 1 m, and the façade line falls between cells
        bool? Built(Vec2 model)
        {
            bool any = false, seen = false;
            for (int dx = -1; dx <= 1 && !any; dx++)
                for (int dy = -1; dy <= 1 && !any; dy++)
                {
                    var s = frame.ToSurvey(model + new Vec2(dx * 700, dy * 700));
                    double t = Height(top, s), g = Height(ground, s);
                    if (double.IsNaN(t) || double.IsNaN(g)) continue;
                    seen = true;
                    if (t - g >= SiteContextBuilder.MinBuildingHeightM) any = true;
                }
            return seen ? any : null;
        }
        var floors = levels.OrderBy(l => l.ElevationMm).ToList();
        foreach (var l in floors.Skip(1))
        {
            if (GroundOrBelow.IsMatch(l.Name)) continue;
            if (l.Outline is not Polygon2 outline) continue;
            foreach (var w in l.Walls.Walls)
            {
                if (w.Arc != null || w.Length < 2000 || w.HeightMm != null) continue;
                if (WindowFinder.Outward(w, w.Length / 2, outline) is not Vec2 o) continue;
                // the wall's outer face and a little way in: nothing built anywhere along it
                var c = w.Centerline;
                var samples = new[] { 0.2, 0.5, 0.8 }.Select(t => c.A + (c.B - c.A) * t + o * (w.TypeThickness / 2) - o * (clearMm - 1000)).ToList();
                var built = samples.Select(Built).ToList();
                if (built.Any(b => b == null) || built.Any(b => b == true)) continue;
                items.Add(new CheckItem(l.Code, CheckKind.OutsideLidar, c.Mid,
                    $"{l.Name}: a {w.Length / 1000:0.0} m outside wall stands where the LiDAR sees nothing built: a neighbour's or garden wall, or an outline read as a wall?"));
            }
        }
        return items;
    }

    /// <summary>The grids, with the tiles inside any zip unpacked to a temporary folder named after the zip (its name may say DSM or DTM).</summary>
    static IEnumerable<string> Expand(IEnumerable<string> files, List<string> temp)
    {
        foreach (var f in files)
        {
            if (!f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { yield return f; continue; }
            var dir = Path.Combine(Path.GetTempPath(), "SurveyToRevit-check-" + Guid.NewGuid().ToString("N")[..8], Path.GetFileNameWithoutExtension(f));
            var got = new List<string>();
            try
            {
                Directory.CreateDirectory(dir);
                temp.Add(Path.GetDirectoryName(dir)!);
                using var zip = ZipFile.OpenRead(f);
                foreach (var e in zip.Entries)
                {
                    var ext = Path.GetExtension(e.Name).ToLowerInvariant();
                    if (ext is not (".tif" or ".tiff" or ".asc" or ".tfw")) continue;
                    var to = Path.Combine(dir, e.Name);
                    e.ExtractToFile(to, true);
                    if (ext != ".tfw") got.Add(to);
                }
            }
            catch { /* unreadable zip */ }
            foreach (var g in got) yield return g;
        }
    }
}
