using System.IO.Compression;
using System.Text.RegularExpressions;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Site;

/// <summary>A neighbouring building as a block: footprint (and courtyards) in model mm, base and top in mm above datum.</summary>
public sealed record ContextBlock(Polygon2 Outline, IReadOnlyList<Polygon2> Holes, double BaseMm, double TopMm)
{
    /// <summary>The block's shape with its roof as the LiDAR sees it (pitches, steps), when traced from the LiDAR.</summary>
    public BlockMesh? Mesh { get; init; }
}

/// <summary>A closed surface in model mm: corner points and triangles (anticlockwise seen from outside).</summary>
public sealed record BlockMesh(IReadOnlyList<(double X, double Y, double Z)> Points, IReadOnlyList<(int A, int B, int C)> Triangles);

/// <summary>A connected area of carriageway (model mm), with islands, from road centrelines kept to open ground.</summary>
public sealed record RoadArea(Polygon2 Outline, IReadOnlyList<Polygon2> Holes);

/// <summary>The surroundings read from LiDAR: ground points and neighbour blocks, in model coordinates (mm).</summary>
public sealed class SiteContext
{
    public required IReadOnlyList<(Vec2 Position, double ZMm)> Ground { get; init; }
    public required IReadOnlyList<ContextBlock> Blocks { get; init; }
    public IReadOnlyList<RoadArea> Roads { get; init; } = Array.Empty<RoadArea>();
    /// <summary>LiDAR roof top over the surveyed building (mm above datum), for comparison only.</summary>
    public double? SurveyedRoofTopMm { get; init; }
    /// <summary>The surveyed building's outline: the ground surface is left open here for the survey's own floors.</summary>
    public Polygon2? Footprint { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Surroundings from Environment Agency LiDAR. The DTM (bare ground) gives the ground;
/// DSM minus DTM gives what stands on it. Cells standing 2.5 m or more on a smooth
/// surface (roofs, flat or pitched; trees are rough and drop out) are buildings, split
/// where the roof height jumps, each part becoming a block from the ground to its median
/// roof height. The survey always comes first: nothing is made on or near the surveyed
/// building (its floor outlines plus a margin); its LiDAR roof height is only reported.
/// With a footprint shapefile (e.g. OS MasterMap Topography buildings), its exact outlines
/// are used instead, each raised to its LiDAR roof height; a footprint whose roof steps
/// is split by the LiDAR. Footprints with no LiDAR heights are left out rather than guessed.
/// </summary>
public static class SiteContextBuilder
{
    public const double MinBuildingHeightM = 2.5, RoofStepM = 1.5, SurveyMarginM = 3, MinBlockM2 = 20, MinCourtyardM2 = 10;
    static readonly Regex Dsm = new(@"dsm|surface|first.?return", RegexOptions.IgnoreCase);
    static readonly Regex Dtm = new(@"dtm|terrain", RegexOptions.IgnoreCase);

    /// <summary>LiDAR tiles (.tif, .asc), footprint shapefiles (.shp), or a zip of them as downloaded, for the surroundings.</summary>
    public static bool IsSiteFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff" or ".asc" or ".shp" or ".zip"
        || path.EndsWith(OsmRoads.FileSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"DSM", "DTM", "Footprints", or null when a grid's file name does not say which it is.</summary>
    public static string? Kind(string path)
    {
        if (Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase)) return "Footprints";
        if (path.EndsWith(OsmRoads.FileSuffix, StringComparison.OrdinalIgnoreCase)) return "Roads";
        var name = Path.GetFileNameWithoutExtension(path);
        bool s = Dsm.IsMatch(name), t = Dtm.IsMatch(name);
        // a tile from a download zip whose own name does not say: the zip's name may ("lidar_tiles_dsm-2015...zip")
        if (s == t && Path.GetFileName(Path.GetDirectoryName(path)) is string folder) { s = Dsm.IsMatch(folder); t = Dtm.IsMatch(folder); }
        return s == t ? null : s ? "DSM" : "DTM";
    }

    public static SiteContext Build(IEnumerable<string> files, SurveyFrame frame, IEnumerable<Polygon2> surveyedOutlines,
        double radiusM = 150, double groundStepM = 5, (double E, double N)? centreM = null)
    {
        var notes = new List<string>();
        // the area round the base point, or round a centre chosen on the map
        double cx = centreM?.E ?? frame.BasePoint.X / 1000, cy = centreM?.N ?? frame.BasePoint.Y / 1000;
        // files are read over the grid square that holds the whole area (turned with the model)
        double reach = radiusM * Math.Sqrt(2);
        double bMinX = cx - reach, bMaxX = cx + reach, bMinY = cy - reach, bMaxY = cy + reach;
        // the work grid runs square to the model (the survey's main walls, so usually the streets):
        // 1 m cells in model metres, so outlines come out straight rather than stepped
        var local = frame.ToModel(new Vec2(cx * 1000, cy * 1000));
        double lx = local.X / 1000, ly = local.Y / 1000;
        double minX = lx - radiusM, maxY = ly + radiusM;
        Vec2 Local(Vec2 grid) { var m = frame.ToModel(new Vec2(grid.X * 1000, grid.Y * 1000)); return new Vec2(m.X / 1000, m.Y / 1000); }
        var dsm = new List<GeoRaster>();
        var dtm = new List<GeoRaster>();
        var footprints = new List<Shapefile.Shape>();
        var roadLines = new List<RoadLine>();
        var unzipped = new List<string>();
        try
        {
        foreach (var f in Unzip(files, unzipped, notes))
        {
            var kind = Kind(f);
            if (kind == null) { notes.Add($"{Path.GetFileName(f)}: name does not say DSM or DTM; not used."); continue; }
            try
            {
                if (kind == "Footprints") { footprints.AddRange(Shapefile.ReadPolygons(f, bMinX, bMinY, bMaxX, bMaxY)); continue; }
                if (kind == "Roads") { roadLines.AddRange(OsmRoads.Parse(File.ReadAllText(f))); continue; }
                var r = GeoRaster.Read(f, bMinX, bMinY, bMaxX, bMaxY);
                if (r == null) { notes.Add($"{Path.GetFileName(f)}: does not cover the site; not used."); continue; }
                (kind == "DSM" ? dsm : dtm).Add(r);
            }
            catch (Exception ex) { notes.Add($"{Path.GetFileName(f)}: could not be read ({ex.Message})."); }
        }
        }
        finally { foreach (var d in unzipped) try { Directory.Delete(d, true); } catch { /* temp folder */ } }
        if (dsm.Count + dtm.Count == 0 && files.Any(f => Kind(f) != "Roads"))
            notes.Add($"None of the LiDAR tiles reach the site (E {cx:0} N {cy:0}). Download the tiles for this grid square that cover it, e.g. the Environment Agency's LIDAR Composite DSM and DTM 1m.");

        int n = (int)Math.Round(2 * radiusM);
        double At(List<GeoRaster> list, int c, int r)
        {
            var at = frame.ToSurvey(new Vec2((minX + c + 0.5) * 1000, (maxY - r - 0.5) * 1000));
            foreach (var g in list) { var v = g.At(at.X / 1000, at.Y / 1000); if (!double.IsNaN(v)) return v; }
            return double.NaN;
        }
        var top = new double[n * n];
        var ground = new double[n * n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) { top[r * n + c] = At(dsm, c, r); ground[r * n + c] = At(dtm, c, r); }
        Vec2 Model(double x, double y) => new(x * 1000, y * 1000);
        footprints = footprints.Select(fp => new Shapefile.Shape(fp.Outer.Select(Local).ToList(),
            fp.Holes.Select(h => (IReadOnlyList<Vec2>)h.Select(Local).ToList()).ToList())).ToList();
        roadLines = roadLines.Select(rl => rl with { Points = rl.Points.Select(Local).ToList() }).ToList();

        // ground points on a coarser grid, inside the circle
        var groundPts = new List<(Vec2, double)>();
        int step = Math.Max(1, (int)Math.Round(groundStepM));
        for (int r = step / 2; r < n; r += step)
            for (int c = step / 2; c < n; c += step)
            {
                double z = ground[r * n + c], x = minX + c + 0.5, y = maxY - r - 0.5;
                if (double.IsNaN(z) || Math.Pow(x - lx, 2) + Math.Pow(y - ly, 2) > radiusM * radiusM) continue;
                groundPts.Add((Model(x, y), Math.Round(z * 1000 / 10) * 10));
            }
        int missing = top.Count(double.IsNaN) * 100 / top.Length;
        if (dtm.Count == 0) notes.Add("No DTM (bare ground) tile: no ground surface, and buildings cannot be separated from the ground.");
        if (dsm.Count == 0) notes.Add("No DSM (surface) tile: no neighbouring buildings.");
        if (missing > 5 && dsm.Count > 0) notes.Add($"{missing}% of the area has no LiDAR data (outside the tiles or not flown).");

        // the surveyed building and a margin around it are left alone
        var outlines = surveyedOutlines.Where(o => o.Vertices.Count >= 3).ToList();
        var survey = new bool[n * n];
        var roofs = new List<double>();
        foreach (var o in outlines)
        {
            double oMinX = o.Vertices.Min(v => v.X) - SurveyMarginM * 1000, oMaxX = o.Vertices.Max(v => v.X) + SurveyMarginM * 1000;
            double oMinY = o.Vertices.Min(v => v.Y) - SurveyMarginM * 1000, oMaxY = o.Vertices.Max(v => v.Y) + SurveyMarginM * 1000;
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    var p = Model(minX + c + 0.5, maxY - r - 0.5);
                    if (p.X < oMinX || p.X > oMaxX || p.Y < oMinY || p.Y > oMaxY) continue;
                    bool inside = FacadeWalls.Contains(o, p);
                    if (inside || DistanceToEdges(o, p) < SurveyMarginM * 1000) survey[r * n + c] = true;
                }
        }
        if (outlines.Count > 0)
        {
            var main = outlines.OrderByDescending(o => o.Area).First();
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    if (!double.IsNaN(top[r * n + c]) && FacadeWalls.Contains(main, Model(minX + c + 0.5, maxY - r - 0.5))) roofs.Add(top[r * n + c]);
        }
        double? roofTop = roofs.Count > 20 ? Math.Round(Percentile(roofs, 0.98) * 100) * 10 : null;

        // buildings: tall enough and smooth
        var mask = new bool[n * n];
        if (dsm.Count > 0 && dtm.Count > 0)
            for (int r = 1; r < n - 1; r++)
                for (int c = 1; c < n - 1; c++)
                {
                    int i = r * n + c;
                    if (survey[i] || double.IsNaN(top[i]) || double.IsNaN(ground[i])) continue;
                    if (top[i] - ground[i] < MinBuildingHeightM) continue;
                    double lap = 4 * top[i] - top[i - 1] - top[i + 1] - top[i - n] - top[i + n];
                    mask[i] = !double.IsNaN(lap) && Math.Abs(lap) < 1.0;
                }
        mask = Grow(Shrink(Grow(mask, n), n), n);     // close ridges and gutters
        mask = Grow(Shrink(mask, n), n);              // drop strands and lone trees
        for (int i = 0; i < mask.Length; i++) if (survey[i]) mask[i] = false;

        var blocks = new List<ContextBlock>();
        int skipped = 0;
        if (footprints.Count > 0)
        {
            int noHeight = 0, split = 0;
            foreach (var fp in footprints)
            {
                var cells = Cells(fp.Outer, n, minX, maxY);
                if (cells.Count == 0 || cells.Count(i => survey[i]) * 2 >= cells.Count) continue; // the surveyed building: survey wins
                var tops = cells.Select(i => top[i]).Where(v => !double.IsNaN(v)).ToList();
                var bases = cells.Select(i => ground[i]).Where(v => !double.IsNaN(v)).ToList();
                if (tops.Count < cells.Count / 2 || bases.Count == 0) { noHeight++; continue; }
                double topM = Percentile(tops, 0.5), baseM = bases.Min();
                if (topM - baseM < MinBuildingHeightM) continue;
                if (Percentile(tops, 0.85) - Percentile(tops, 0.15) > 3 && cells.Count >= 2 * MinBlockM2)
                {
                    // roof steps: let the LiDAR split this footprint
                    var inside = new bool[n * n];
                    foreach (var i in cells) inside[i] = !double.IsNaN(top[i]) && top[i] - ground[i] >= MinBuildingHeightM;
                    int before = blocks.Count;
                    skipped += Roofs(inside, top, ground, n, minX, maxY, Model, blocks);
                    if (blocks.Count > before) { split++; continue; }
                }
                var outline = new Polygon2(fp.Outer.Select(v => Model(v.X, v.Y)));
                if (!outline.IsSimple() || outline.Area < 5e6) continue;
                var holes = fp.Holes.Select(h => new Polygon2(h.Select(v => Model(v.X, v.Y)))).Where(h => h.IsSimple() && h.Area >= MinCourtyardM2 * 1e6).ToList();
                blocks.Add(new ContextBlock(outline, holes, Math.Floor(baseM * 10) * 100, Math.Round(topM * 10) * 100));
            }
            notes.Add($"Footprints: {footprints.Count} outlines in the area; {split} with stepped roofs split by the LiDAR; {noHeight} without LiDAR heights left out.");
        }
        else skipped = Roofs(mask, top, ground, n, minX, maxY, Model, blocks);
        if (skipped > 0) notes.Add($"{skipped} small roof shapes too irregular to outline were left out.");
        notes.Insert(0, $"LiDAR: {dsm.Count} DSM and {dtm.Count} DTM tiles over a {2 * radiusM:0} m square around the base point; " +
                        $"{groundPts.Count} ground points, {blocks.Count} neighbouring blocks{(footprints.Count > 0 ? " from the footprints" : " traced from the LiDAR")} (the surveyed building and {SurveyMarginM:0} m around it are left to the survey).");
        if (roofTop is double rt) notes.Add($"LiDAR roof top over the surveyed building: {rt / 1000:0.00} m above datum (for comparison only; the survey levels are used).");
        var footprint = outlines.OrderByDescending(o => o.Area).FirstOrDefault();

        // the ground just outside the surveyed building (3–15 m), to compare with its ground floor
        if (footprint != null && dtm.Count > 0)
        {
            var near = new List<double>();
            for (int r = 0; r < n; r += 2)
                for (int c = 0; c < n; c += 2)
                {
                    int i = r * n + c;
                    if (double.IsNaN(ground[i]) || mask[i]) continue;
                    var p = Model(minX + c + 0.5, maxY - r - 0.5);
                    if (FacadeWalls.Contains(footprint, p)) continue;
                    double d = DistanceToEdges(footprint, p);
                    if (d >= 3000 && d <= 15000) near.Add(ground[i]);
                }
            if (near.Count > 5) notes.Add($"Ground beside the surveyed building (LiDAR, 3–15 m out): {Percentile(near, 0.5):0.00} m above datum (lowest {Percentile(near, 0.05):0.00}, highest {Percentile(near, 0.95):0.00}).");
        }

        // roads: centrelines widened to their carriageway, kept to open ground (not buildings, not the survey)
        var roads = new List<RoadArea>();
        if (roadLines.Count > 0)
        {
            var road = new bool[n * n];
            foreach (var rl in roadLines)
                for (int k = 0; k + 1 < rl.Points.Count; k++)
                {
                    var a = rl.Points[k]; var b = rl.Points[k + 1]; double half = rl.WidthM / 2;
                    int c0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - half - minX)), c1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + half - minX));
                    int r0 = Math.Max(0, (int)Math.Floor(maxY - Math.Max(a.Y, b.Y) - half)), r1 = Math.Min(n - 1, (int)Math.Ceiling(maxY - Math.Min(a.Y, b.Y) + half));
                    for (int r = r0; r <= r1; r++)
                        for (int c = c0; c <= c1; c++)
                            if (SegmentDistance(new Vec2(minX + c + 0.5, maxY - r - 0.5), a, b) <= half) road[r * n + c] = true;
                }
            for (int i = 0; i < road.Length; i++)
                if (survey[i] || mask[i] || (dtm.Count > 0 && double.IsNaN(ground[i]))) road[i] = false;
            road = Shrink(Grow(road, n), n);
            for (int i = 0; i < road.Length; i++) if (survey[i] || mask[i]) road[i] = false;
            road = Grow(Shrink(road, n), n);     // drop slivers left between buildings
            for (int i = 0; i < road.Length; i++) if (survey[i] || mask[i]) road[i] = false;
            roads = RoadAreas(road, n, minX, maxY, Model);
            var named = roadLines.Select(r => r.Name).Where(x => x != null).Distinct().Take(8).ToList();
            notes.Add($"Roads: {roadLines.Count} OpenStreetMap road lines ({string.Join(", ", named)}{(named.Count == 8 ? ", …" : "")}) → {roads.Count} carriageway areas, {road.Count(x => x)} m² (© OpenStreetMap contributors, ODbL).");
        }

        return new SiteContext { Roads = roads, Ground = groundPts.Where(g => footprint == null || !FacadeWalls.Contains(footprint, g.Item1)).ToList(),
            Blocks = blocks, SurveyedRoofTopMm = roofTop, Notes = notes, Footprint = footprint };
    }

    /// <summary>
    /// The files to read: zips (as downloaded from the Environment Agency) are opened into a temporary folder
    /// named after the zip, keeping only grids and shapefiles; the folders are listed in <paramref name="temp"/>.
    /// </summary>
    static IEnumerable<string> Unzip(IEnumerable<string> files, List<string> temp, List<string> notes)
    {
        foreach (var f in files)
        {
            if (!Path.GetExtension(f).Equals(".zip", StringComparison.OrdinalIgnoreCase)) { yield return f; continue; }
            var dir = Path.Combine(Path.GetTempPath(), "SurveyToRevit-lidar-" + Guid.NewGuid().ToString("N")[..8], Path.GetFileNameWithoutExtension(f));
            var got = new List<string>();
            try
            {
                Directory.CreateDirectory(dir);
                temp.Add(Path.GetDirectoryName(dir)!);
                using var zip = ZipFile.OpenRead(f);
                foreach (var e in zip.Entries)
                {
                    var ext = Path.GetExtension(e.Name).ToLowerInvariant();
                    if (ext is not (".tif" or ".tiff" or ".asc" or ".shp" or ".shx" or ".dbf" or ".prj")) continue;
                    var to = Path.Combine(dir, e.Name);
                    e.ExtractToFile(to, true);
                    if (ext is not (".shx" or ".dbf" or ".prj")) got.Add(to);
                }
                notes.Add($"{Path.GetFileName(f)}: {got.Count} files inside.");
            }
            catch (Exception ex) { notes.Add($"{Path.GetFileName(f)}: could not be opened ({ex.Message})."); }
            foreach (var g in got) yield return g;
        }
    }

    static double SegmentDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var d = b - a; double len2 = d.Dot(d);
        double t = len2 == 0 ? 0 : Math.Clamp((p - a).Dot(d) / len2, 0, 1);
        return p.DistanceTo(a + d * t);
    }

    /// <summary>Connected road cells → outlines with islands.</summary>
    static List<RoadArea> RoadAreas(bool[] mask, int n, double minX, double maxY, Func<double, double, Vec2> model)
    {
        var region = new int[n * n];
        Array.Fill(region, -1);
        var areas = new List<RoadArea>();
        var queue = new Queue<int>();
        int next = 0;
        for (int s = 0; s < mask.Length; s++)
        {
            if (!mask[s] || region[s] >= 0) continue;
            var cells = new List<int>();
            region[s] = next; queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(); cells.Add(i);
                int r = i / n, c = i % n;
                foreach (var j in new[] { c > 0 ? i - 1 : -1, c < n - 1 ? i + 1 : -1, r > 0 ? i - n : -1, r < n - 1 ? i + n : -1 })
                    if (j >= 0 && mask[j] && region[j] < 0) { region[j] = next; queue.Enqueue(j); }
            }
            int id = next++;
            if (cells.Count < MinBlockM2) continue;
            var loops = Boundary(cells, region, id, n);
            var outer = loops.OrderByDescending(l => SignedArea(l)).First();
            // simplified where that stays a clean outline, else cell by cell (a road network is one big, intricate area)
            Polygon2 Shape(List<(int X, int Y)> l) => ToPolygon(l, minX, maxY, model) is Polygon2 p && p.IsSimple() ? p : Corners(l, minX, maxY, model);
            var outline = Shape(outer);
            var holes = loops.Where(l => l != outer && -SignedArea(l) >= MinCourtyardM2).Select(Shape).ToList();
            areas.Add(new RoadArea(outline, holes));
        }
        return areas;
    }

    /// <summary>A corner loop as it is, only the corners where it turns (always a clean outline).</summary>
    static Polygon2 Corners(List<(int X, int Y)> loop, double minX, double maxY, Func<double, double, Vec2> model)
    {
        var kept = loop.Where((p, i) =>
        {
            var a = loop[(i + loop.Count - 1) % loop.Count]; var b = loop[(i + 1) % loop.Count];
            return (p.X - a.X) * (b.Y - p.Y) - (p.Y - a.Y) * (b.X - p.X) != 0;
        });
        return new Polygon2(kept.Select(p => model(minX + p.X, maxY + p.Y)));
    }

    /// <summary>Splits the masked cells into roofs of one height and adds a block for each; returns how many could not be outlined.</summary>
    static int Roofs(bool[] mask, double[] top, double[] ground, int n, double minX, double maxY, Func<double, double, Vec2> model, List<ContextBlock> blocks)
    {
        var region = new int[n * n];
        Array.Fill(region, -1);
        int skipped = 0, next = 0;
        var queue = new Queue<int>();
        for (int s = 0; s < mask.Length; s++)
        {
            if (!mask[s] || region[s] >= 0) continue;
            var cells = new List<int>();
            region[s] = next; queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(); cells.Add(i);
                int r = i / n, c = i % n;
                foreach (var j in new[] { c > 0 ? i - 1 : -1, c < n - 1 ? i + 1 : -1, r > 0 ? i - n : -1, r < n - 1 ? i + n : -1 })
                    if (j >= 0 && mask[j] && region[j] < 0 && Math.Abs(top[j] - top[i]) < RoofStepM) { region[j] = next; queue.Enqueue(j); }
            }
            int id = next++;
            if (cells.Count < MinBlockM2) continue;
            var loops = Boundary(cells, region, id, n);
            var outer = loops.OrderByDescending(l => SignedArea(l)).First();
            var outline = ToPolygon(outer, minX, maxY, model);
            if (outline == null || !outline.IsSimple()) { outline = Corners(outer, minX, maxY, model); skipped++; }
            var holes = loops.Where(l => l != outer && -SignedArea(l) >= MinCourtyardM2)
                .Select(l => ToPolygon(l, minX, maxY, model)).Where(h => h != null && h.IsSimple()).Cast<Polygon2>().ToList();
            var heights = cells.Select(i => top[i]).ToList();
            var bases = cells.Select(i => ground[i]).Where(z => !double.IsNaN(z)).ToList();
            if (bases.Count == 0) continue;
            double topMm = Math.Round(Percentile(heights, 0.5) * 10) * 100;
            double baseMm = Math.Floor(bases.Min() * 10) * 100;
            if (topMm - baseMm < MinBuildingHeightM * 1000) continue;
            blocks.Add(new ContextBlock(outline, holes, baseMm, topMm) { Mesh = Surface(cells, top, n, minX, maxY, baseMm / 1000) });
        }
        return skipped;
    }

    /// <summary>
    /// A block as the LiDAR sees it, on 2 m cells: walls up from the base and a roof following the surface
    /// heights (each roof corner the mean of the cells around it, so pitches and steps show).
    /// </summary>
    internal static BlockMesh Surface(List<int> cells, double[] top, int n, double minX, double maxY, double baseM, int step = 2)
    {
        // coarse cells: in the block when at least half their fine cells are; height = their median
        var fine = cells.ToHashSet();
        var height = new Dictionary<(int C, int R), double>();
        foreach (var g in cells.GroupBy(i => (C: i % n / step, R: i / n / step)))
            if (g.Count() * 2 >= step * step)
            {
                var z = g.Select(i => top[i]).Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
                if (z.Count > 0) height[g.Key] = z[z.Count / 2];
            }
        if (height.Count == 0) { foreach (var i in cells.Take(1)) height[(i % n / step, i / n / step)] = top[i]; }
        var pts = new List<(double, double, double)>();
        var tris = new List<(int, int, int)>();
        var topIndex = new Dictionary<(int, int), int>();
        var baseIndex = new Dictionary<(int, int), int>();
        double X(int i) => (minX + i * step) * 1000;
        double Y(int j) => (maxY - j * step) * 1000;
        int Top((int I, int J) v)
        {
            if (topIndex.TryGetValue(v, out var k)) return k;
            var around = new[] { (v.I - 1, v.J - 1), (v.I, v.J - 1), (v.I - 1, v.J), (v.I, v.J) }
                .Where(height.ContainsKey).Select(c => height[c]).ToList();
            pts.Add((X(v.I), Y(v.J), Math.Round(around.Average() * 100) * 10));
            return topIndex[v] = pts.Count - 1;
        }
        int Base((int I, int J) v)
        {
            if (baseIndex.TryGetValue(v, out var k)) return k;
            pts.Add((X(v.I), Y(v.J), baseM * 1000));
            return baseIndex[v] = pts.Count - 1;
        }
        void Quad(int a, int b, int c, int d) { tris.Add((a, b, c)); tris.Add((a, c, d)); }
        foreach (var (c, r) in height.Keys)
        {
            // corners: south-west, south-east, north-east, north-west (rows run south)
            (int, int) sw = (c, r + 1), se = (c + 1, r + 1), ne = (c + 1, r), nw = (c, r);
            Quad(Top(sw), Top(se), Top(ne), Top(nw));
            Quad(Base(sw), Base(nw), Base(ne), Base(se));
            if (!height.ContainsKey((c, r + 1))) Quad(Base(sw), Base(se), Top(se), Top(sw));
            if (!height.ContainsKey((c, r - 1))) Quad(Base(ne), Base(nw), Top(nw), Top(ne));
            if (!height.ContainsKey((c + 1, r))) Quad(Base(se), Base(ne), Top(ne), Top(se));
            if (!height.ContainsKey((c - 1, r))) Quad(Base(nw), Base(sw), Top(sw), Top(nw));
        }
        return new BlockMesh(pts, tris);
    }

    /// <summary>Grid cells whose centres fall inside a ring given in grid metres.</summary>
    static List<int> Cells(IReadOnlyList<Vec2> ring, int n, double minX, double maxY)
    {
        var poly = new Polygon2(ring);
        int c0 = Math.Max(0, (int)Math.Floor(ring.Min(v => v.X) - minX)), c1 = Math.Min(n - 1, (int)Math.Ceiling(ring.Max(v => v.X) - minX));
        int r0 = Math.Max(0, (int)Math.Floor(maxY - ring.Max(v => v.Y))), r1 = Math.Min(n - 1, (int)Math.Ceiling(maxY - ring.Min(v => v.Y)));
        var cells = new List<int>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (FacadeWalls.Contains(poly, new Vec2(minX + c + 0.5, maxY - r - 0.5))) cells.Add(r * n + c);
        return cells;
    }

    static bool[] Grow(bool[] m, int n) => Morph(m, n, true);
    static bool[] Shrink(bool[] m, int n) => Morph(m, n, false);

    static bool[] Morph(bool[] m, int n, bool grow)
    {
        var o = new bool[m.Length];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                bool any = false, all = true;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int rr = r + dr, cc = c + dc;
                        bool v = rr >= 0 && cc >= 0 && rr < n && cc < n && m[rr * n + cc];
                        any |= v; all &= v;
                    }
                o[r * n + c] = grow ? any : all;
            }
        return o;
    }

    /// <summary>
    /// The region's boundary as closed loops of cell corners (column, -row), interior on the left:
    /// the outer loop runs anticlockwise, courtyards clockwise. Where two loops touch at a corner
    /// the walk turns left, so they stay separate.
    /// </summary>
    static List<List<(int X, int Y)>> Boundary(List<int> cells, int[] region, int id, int n)
    {
        bool In(int c, int r) => c >= 0 && r >= 0 && c < n && r < n && region[r * n + c] == id;
        var edges = new Dictionary<(int, int), List<(int, int)>>();
        void Add((int, int) a, (int, int) b) { if (!edges.TryGetValue(a, out var l)) edges[a] = l = new(); l.Add(b); }
        foreach (var i in cells)
        {
            int r = i / n, c = i % n;
            if (!In(c, r + 1)) Add((c, -r - 1), (c + 1, -r - 1));
            if (!In(c + 1, r)) Add((c + 1, -r - 1), (c + 1, -r));
            if (!In(c, r - 1)) Add((c + 1, -r), (c, -r));
            if (!In(c - 1, r)) Add((c, -r), (c, -r - 1));
        }
        var loops = new List<List<(int X, int Y)>>();
        while (edges.Count > 0)
        {
            var start = edges.Keys.First();
            var loop = new List<(int X, int Y)>();
            var at = start;
            (int X, int Y) dir = (0, 0);
            for (int guard = 0; guard < 4 * cells.Count + 8; guard++)
            {
                if (!edges.TryGetValue(at, out var outs)) break;
                var to = outs.Count == 1 ? outs[0] : outs.OrderByDescending(o => Cross(dir, (o.Item1 - at.Item1, o.Item2 - at.Item2))).First();
                outs.Remove(to);
                if (outs.Count == 0) edges.Remove(at);
                loop.Add(at);
                dir = (to.Item1 - at.Item1, to.Item2 - at.Item2);
                at = to;
                if (at == start) break;
            }
            if (loop.Count >= 4) loops.Add(loop);
        }
        return loops;
    }

    static int Cross((int X, int Y) a, (int X, int Y) b) => a.X * b.Y - a.Y * b.X;

    static double SignedArea(List<(int X, int Y)> l)
    {
        double s = 0;
        for (int i = 0; i < l.Count; i++) { var a = l[i]; var b = l[(i + 1) % l.Count]; s += (double)a.X * b.Y - (double)b.X * a.Y; }
        return s / 2;
    }

    /// <summary>Corner loop → simplified polygon in model mm (corners within 0.5 m of a straight run are dropped).</summary>
    static Polygon2? ToPolygon(List<(int X, int Y)> loop, double minX, double maxY, Func<double, double, Vec2> model)
    {
        var pts = loop.Select(p => new Vec2(minX + p.X, maxY + p.Y)).ToList();
        var kept = SimplifyClosed(pts, 0.5);
        if (kept.Count < 3) return null;
        var poly = new Polygon2(kept.Select(p => model(p.X, p.Y)));
        return poly.Area < MinBlockM2 * 1e6 / 2 ? null : poly;
    }

    static List<Vec2> SimplifyClosed(List<Vec2> pts, double tol)
    {
        // split at the two points farthest apart, simplify each half (Douglas–Peucker)
        int a = 0, b = 0; double best = -1;
        for (int i = 0; i < pts.Count; i++) { double d = pts[i].DistanceTo(pts[0]); if (d > best) { best = d; b = i; } }
        var keep = new bool[pts.Count];
        keep[a] = keep[b] = true;
        void Rec(int i, int j)
        {
            int k = -1; double far = tol;
            for (int m = i + 1; m < j; m++)
            {
                double d = DistanceToLine(pts[m % pts.Count], pts[i % pts.Count], pts[j % pts.Count]);
                if (d > far) { far = d; k = m; }
            }
            if (k < 0) return;
            keep[k % pts.Count] = true;
            Rec(i, k); Rec(k, j);
        }
        Rec(a, b); Rec(b, pts.Count);
        return pts.Where((_, i) => keep[i]).ToList();
    }

    static double DistanceToLine(Vec2 p, Vec2 a, Vec2 b)
    {
        var d = b - a; double len = Math.Sqrt(d.Dot(d));
        if (len < 1e-9) return p.DistanceTo(a);
        return Math.Abs(d.X * (p.Y - a.Y) - d.Y * (p.X - a.X)) / len;
    }

    static double DistanceToEdges(Polygon2 poly, Vec2 p)
    {
        double best = double.MaxValue;
        var v = poly.Vertices;
        for (int i = 0; i < v.Count; i++)
        {
            var a = v[i]; var b = v[(i + 1) % v.Count];
            var d = b - a; double len2 = d.Dot(d);
            double t = len2 == 0 ? 0 : Math.Clamp((p - a).Dot(d) / len2, 0, 1);
            best = Math.Min(best, p.DistanceTo(a + d * t));
        }
        return best;
    }

    static double Percentile(List<double> values, double q)
    {
        var s = values.OrderBy(v => v).ToList();
        return s[(int)Math.Clamp(Math.Round(q * (s.Count - 1)), 0, s.Count - 1)];
    }
}
