using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;

namespace SurveyToRevit.Core.Survey;

public sealed record SurveyText(Vec2 Position, string Value, string Layer);

/// <summary>
/// One DWG survey read directly from the file (not through Revit, which cannot
/// see text such as spot heights). All coordinates are in millimetres in the
/// drawing's own coordinate system (for surveys: Ordnance Survey grid).
/// </summary>
public sealed class SurveyDrawing
{
    public required string Path { get; init; }
    public required DrawingName Name { get; init; }
    public required double MmPerUnit { get; init; }
    public required string UnitsNote { get; init; }
    public required IReadOnlyList<Segment2> WallLines { get; init; }
    public required IReadOnlyList<Segment2> OpeningLines { get; init; }
    /// <summary>Curved lines (arcs, bulged polyline pieces) on wall layers, radius 1 m or more.</summary>
    public IReadOnlyList<Arc2> WallArcs { get; init; } = Array.Empty<Arc2>();
    /// <summary>Small arcs and circles on wall layers (round columns, half-round pilasters): shapes, not walls.</summary>
    public IReadOnlyList<Arc2> SmallWallArcs { get; init; } = Array.Empty<Arc2>();
    /// <summary>Door swing arcs (centre at the hinge, radius = leaf width).</summary>
    public IReadOnlyList<Arc2> DoorArcs { get; init; } = Array.Empty<Arc2>();
    public required IReadOnlyList<SurveyText> Texts { get; init; }
    /// <summary>Lines on layers for other parts of the building (stairs, columns, roofs, fittings…; see <see cref="LayerRules.IsFeature"/>), curves in short pieces.</summary>
    public IReadOnlyList<Segment2> FeatureLines { get; init; } = Array.Empty<Segment2>();
    public required IReadOnlyDictionary<string, int> LayerCounts { get; init; }

    /// <summary>Spot heights on level layers, in mm.</summary>
    public IEnumerable<double> SpotHeightsMm(LayerRules rules) =>
        Texts.Where(t => rules.Classify(t.Layer) == LayerRole.LevelText)
             .Select(t => LevelEstimator.ParseMm(t.Value)).OfType<double>();

    /// <summary>The sheet's own words (title block, usually in paper space): "Ground Floor Plan", "Sections 1 and 3".</summary>
    static List<string> SheetTitles(ACadSharp.CadDocument doc)
    {
        var titles = new List<string>();
        foreach (var br in doc.BlockRecords.Where(b => b.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)))
            foreach (var e in br.Entities)
                if ((e is TextEntity t ? t.Value : e is MText m ? m.Value : null) is string v && v.Length <= 80) titles.Add(v);
        return titles;
    }

    public static SurveyDrawing Read(string path, LayerRules? rules = null)
    {
        rules ??= LayerRules.Default;
        var doc = DwgReader.Read(path, (_, _) => { });

        var walls = new List<(string layer, double x1, double y1, double x2, double y2)>();
        var openings = new List<(string layer, double x1, double y1, double x2, double y2)>();
        var features = new List<(string layer, double x1, double y1, double x2, double y2)>();
        var texts = new List<(double x, double y, string value, string layer)>();
        var arcs = new List<(LayerRole role, Arc2 arc)>(); // in drawing units until the units are known
        var counts = new Dictionary<string, int>();

        void AddSeg(string layer, LayerRole role, double x1, double y1, double x2, double y2)
        {
            if (role is LayerRole.Wall or LayerRole.CurtainWall) walls.Add((layer, x1, y1, x2, y2));
            else if (role is LayerRole.Window or LayerRole.Door) openings.Add((layer, x1, y1, x2, y2));
            else if (LayerRules.IsFeature(role)) features.Add((layer, x1, y1, x2, y2));
        }

        void Visit(Entity e, string? inheritedLayer, int depth)
        {
            var layer = e.Layer?.Name ?? "";
            if (layer == "0" && inheritedLayer != null) layer = inheritedLayer;
            counts[layer] = counts.GetValueOrDefault(layer) + 1;
            var role = rules.Classify(layer);
            switch (e)
            {
                case Line l:
                    AddSeg(layer, role, l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y);
                    break;
                case LwPolyline pl:
                    var v = pl.Vertices;
                    int n = pl.IsClosed ? v.Count : v.Count - 1;
                    for (int i = 0; i < n; i++)
                    {
                        var a = v[i].Location; var b = v[(i + 1) % v.Count].Location;
                        if (Math.Abs(v[i].Bulge) > 1e-6)
                        {
                            if ((role is LayerRole.Wall or LayerRole.CurtainWall or LayerRole.Window or LayerRole.Door || LayerRules.IsFeature(role)) && (a.X != b.X || a.Y != b.Y))
                                arcs.Add((role, Arc2.FromBulge(new Vec2(a.X, a.Y), new Vec2(b.X, b.Y), v[i].Bulge, layer)));
                            continue;
                        }
                        AddSeg(layer, role, a.X, a.Y, b.X, b.Y);
                    }
                    break;
                case Arc arc when role is LayerRole.Wall or LayerRole.CurtainWall or LayerRole.Window or LayerRole.Door || LayerRules.IsFeature(role):
                    arcs.Add((role, new Arc2(new Vec2(arc.Center.X, arc.Center.Y), arc.Radius, arc.StartAngle, arc.EndAngle, layer)));
                    break;
                case Circle circle when LayerRules.IsFeature(role) || role is LayerRole.Wall:
                    // round columns, rooflights: a full turn
                    arcs.Add((role, new Arc2(new Vec2(circle.Center.X, circle.Center.Y), circle.Radius, 0, 2 * Math.PI, layer)));
                    break;
                case TextEntity t:
                    texts.Add((t.InsertPoint.X, t.InsertPoint.Y, t.Value ?? "", layer));
                    break;
                case MText mt:
                    texts.Add((mt.InsertPoint.X, mt.InsertPoint.Y, mt.PlainText ?? "", layer));
                    break;
                case Insert ins when depth < 4 && (role is LayerRole.Wall or LayerRole.CurtainWall or LayerRole.Window or LayerRole.Door || LayerRules.IsFeature(role)):
                    // Window/door symbols (and stair, sanitary and furniture symbols) are usually blocks: explode them.
                    try { foreach (var sub in ins.Explode()) Visit(sub, layer, depth + 1); } catch { /* unusual block: ignore */ }
                    break;
            }
        }

        foreach (var e in doc.Entities) Visit(e, null, 0);

        var (mmPerUnit, note) = Units(doc.Header.InsUnits, walls.Select(w => (w.x1, w.y1, w.x2, w.y2)).ToList());
        Segment2 Seg((string layer, double x1, double y1, double x2, double y2) s) =>
            new(new Vec2(s.x1 * mmPerUnit, s.y1 * mmPerUnit), new Vec2(s.x2 * mmPerUnit, s.y2 * mmPerUnit), s.layer, System.IO.Path.GetFileName(path));

        Arc2 Scale(Arc2 a) => a with { Center = a.Center * mmPerUnit, Radius = a.Radius * mmPerUnit };
        var scaled = arcs.Select(a => (a.role, arc: Scale(a.arc))).ToList();
        // window arcs (curved glazing) count as window linework, in short straight pieces
        var openingSegs = openings.Select(Seg).Concat(scaled.Where(a => a.role == LayerRole.Window)
            .SelectMany(a => a.arc.Tessellate(250)).Select(s => s with { Source = System.IO.Path.GetFileName(path) })).ToList();
        return new SurveyDrawing
        {
            FeatureLines = features.Select(Seg).Concat(scaled.Where(a => LayerRules.IsFeature(a.role))
                .SelectMany(a => a.arc.Tessellate(Math.Max(20, Math.Min(250, a.arc.Length / 16)))).Select(x => x with { Source = System.IO.Path.GetFileName(path) })).ToList(),
            DoorArcs = scaled.Where(a => a.role == LayerRole.Door).Select(a => a.arc).ToList(),
            SmallWallArcs = scaled.Where(a => a.role is LayerRole.Wall && !(a.arc.Radius >= 1000 && a.arc.Length >= 200)).Select(a => a.arc).ToList(),
            WallArcs = scaled.Where(a => a.role is LayerRole.Wall or LayerRole.CurtainWall && a.arc.Radius >= 1000 && a.arc.Length >= 200).Select(a => a.arc with { Layer = a.arc.Layer }).ToList(),
            Path = path,
            Name = DrawingName.Parse(path, SheetTitles(doc)),
            MmPerUnit = mmPerUnit,
            UnitsNote = note,
            WallLines = walls.Select(Seg).ToList(),
            OpeningLines = openingSegs,
            Texts = texts.Select(t => new SurveyText(new Vec2(t.x * mmPerUnit, t.y * mmPerUnit), t.value, t.layer)).ToList(),
            LayerCounts = counts,
        };
    }

    /// <summary>
    /// Drawing units from the file header; when the file says "unitless" (common in
    /// surveys), they are inferred from the building size: a plan spanning under
    /// 2000 units is in metres, otherwise millimetres.
    /// </summary>
    public static (double mmPerUnit, string note) Units(UnitsType header, IReadOnlyList<(double x1, double y1, double x2, double y2)> lines)
    {
        switch (header)
        {
            case UnitsType.Millimeters: return (1, "millimetres (from file)");
            case UnitsType.Centimeters: return (10, "centimetres (from file)");
            case UnitsType.Meters: return (1000, "metres (from file)");
            case UnitsType.Inches: return (25.4, "inches (from file)");
            case UnitsType.Feet: return (304.8, "feet (from file)");
        }
        if (lines.Count == 0) return (1000, "unknown: no wall lines, assumed metres");
        double span = Math.Max(lines.Max(l => Math.Max(l.x1, l.x2)) - lines.Min(l => Math.Min(l.x1, l.x2)),
                               lines.Max(l => Math.Max(l.y1, l.y2)) - lines.Min(l => Math.Min(l.y1, l.y2)));
        return span < 2000
            ? (1000, $"metres (file says unitless; building spans {span:0.#} units)")
            : (1, $"millimetres (file says unitless; building spans {span:0} units)");
    }
}
