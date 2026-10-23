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
    /// <summary>Lines of blocks named as doors ("door", "doorl", "FD30"…) placed on a layer that is not a door layer.</summary>
    public IReadOnlyList<Segment2> DoorBlockLines { get; init; } = Array.Empty<Segment2>();
    /// <summary>Lines on wall layers that are not cut walls: drawn dashed or hidden (overhead, below or beyond), or much thinner than the walls.</summary>
    public IReadOnlyList<Segment2> HiddenLines { get; init; } = Array.Empty<Segment2>();
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
        // per wall line: drawn dashed/hidden, and its line weight (1/100 mm, -1 unknown)
        var wallStyle = new List<(bool dashed, int weight)>();
        var openings = new List<(string layer, double x1, double y1, double x2, double y2)>();
        var features = new List<(string layer, double x1, double y1, double x2, double y2)>();
        var texts = new List<(double x, double y, string value, string layer)>();
        var arcs = new List<(LayerRole role, Arc2 arc)>(); // in drawing units until the units are known
        var counts = new Dictionary<string, int>();
        // door symbols drawn as blocks named as doors on a layer that is not a door layer (e.g. "doorl" on JOINERY)
        var doorBlocks = new List<(string layer, double x1, double y1, double x2, double y2)>();
        int inDoorBlock = 0;

        (bool dashed, int weight) style = (false, -1);
        void AddSeg(string layer, LayerRole role, double x1, double y1, double x2, double y2)
        {
            if (inDoorBlock > 0 && role is not LayerRole.Door) { doorBlocks.Add((layer, x1, y1, x2, y2)); return; }
            if (role is LayerRole.Wall or LayerRole.CurtainWall) { walls.Add((layer, x1, y1, x2, y2)); wallStyle.Add(style); }
            else if (role is LayerRole.Window or LayerRole.Door) openings.Add((layer, x1, y1, x2, y2));
            else if (LayerRules.IsFeature(role)) features.Add((layer, x1, y1, x2, y2));
        }

        void Visit(Entity e, string? inheritedLayer, int depth, (bool dashed, int weight)? inheritedStyle = null)
        {
            var layer = e.Layer?.Name ?? "";
            if (layer == "0" && inheritedLayer != null) layer = inheritedLayer;
            counts[layer] = counts.GetValueOrDefault(layer) + 1;
            var role = rules.Classify(layer);
            style = Style(e, inheritedStyle);
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
                            if ((inDoorBlock > 0 || role is LayerRole.Wall or LayerRole.CurtainWall or LayerRole.Window or LayerRole.Door || LayerRules.IsFeature(role)) && (a.X != b.X || a.Y != b.Y) && !(role is LayerRole.Wall && style.dashed))
                                arcs.Add((inDoorBlock > 0 ? LayerRole.Door : role, Arc2.FromBulge(new Vec2(a.X, a.Y), new Vec2(b.X, b.Y), v[i].Bulge, layer)));
                            continue;
                        }
                        AddSeg(layer, role, a.X, a.Y, b.X, b.Y);
                    }
                    break;
                case Arc arc when inDoorBlock > 0 || (role is LayerRole.Wall or LayerRole.CurtainWall or LayerRole.Window or LayerRole.Door || LayerRules.IsFeature(role)) && !(role is LayerRole.Wall && style.dashed):
                    arcs.Add((inDoorBlock > 0 ? LayerRole.Door : role, new Arc2(new Vec2(arc.Center.X, arc.Center.Y), arc.Radius, arc.StartAngle, arc.EndAngle, layer)));
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
                case Insert ins when depth < 4 && role is not LayerRole.Door && IsDoorBlock(ins.Block?.Name):
                    // a door block on another layer: its lines are door evidence, kept apart
                    inDoorBlock++;
                    var ownStyle = style;
                    try { foreach (var sub in ins.Explode()) Visit(sub, layer, depth + 1, ownStyle); } catch { }
                    inDoorBlock--;
                    break;
                case Insert ins when depth < 4 && (role is LayerRole.Wall or LayerRole.CurtainWall or LayerRole.Window or LayerRole.Door || LayerRules.IsFeature(role)):
                    // Window/door symbols (and stair, sanitary and furniture symbols) are usually blocks: explode them.
                    var own = style;
                    try { foreach (var sub in ins.Explode()) Visit(sub, layer, depth + 1, own); } catch { /* unusual block: ignore */ }
                    break;
                case Hatch hatch when role is LayerRole.Wall:
                    // solid fill (poché) on a wall layer: its edges are wall faces
                    foreach (var path in hatch.Paths)
                        foreach (var edge in path.Edges)
                            switch (edge)
                            {
                                case Hatch.BoundaryPath.Line hl: AddSeg(layer, role, hl.Start.X, hl.Start.Y, hl.End.X, hl.End.Y); break;
                                case Hatch.BoundaryPath.Polyline hp:
                                    var pv = hp.Vertices.ToList();
                                    for (int i = 0; i + 1 < pv.Count + (hp.IsClosed ? 1 : 0); i++)
                                        AddSeg(layer, role, pv[i].X, pv[i].Y, pv[(i + 1) % pv.Count].X, pv[(i + 1) % pv.Count].Y);
                                    break;
                            }
                    break;
            }
        }

        foreach (var e in doc.Entities) Visit(e, null, 0);

        // wall-layer lines that are not cut walls: drawn dashed or hidden (overhead, below, beyond), or much
        // thinner than the drawing's walls (a balustrade or cill below the cut, where the surveyor sets line weights)
        var weights = wallStyle.Where(w => w.weight >= 0).Select(w => w.weight).OrderBy(w => w).ToList();
        int typical = weights.Count >= 20 ? weights[weights.Count / 2] : -1;
        var hidden = new List<(string layer, double x1, double y1, double x2, double y2)>();
        var cut = new List<(string layer, double x1, double y1, double x2, double y2)>();
        for (int i = 0; i < walls.Count; i++)
        {
            var (dashed, weight) = wallStyle[i];
            bool thin = typical >= 25 && weight >= 0 && weight <= typical / 2;
            (dashed || thin ? hidden : cut).Add(walls[i]);
        }
        walls = cut;

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
            HiddenLines = hidden.Select(Seg).ToList(),
            DoorBlockLines = doorBlocks.Select(Seg).ToList(),
            OpeningLines = openingSegs,
            Texts = texts.Select(t => new SurveyText(new Vec2(t.x * mmPerUnit, t.y * mmPerUnit), t.value, t.layer)).ToList(),
            LayerCounts = counts,
        };
    }

    /// <summary>How an entity is drawn: dashed or hidden (any linetype with a pattern), and its line weight (1/100 mm, -1 when not set).</summary>
    static (bool dashed, int weight) Style(Entity e, (bool dashed, int weight)? block)
    {
        var lt = e.LineType;
        bool dashed;
        if (lt == null || lt.Name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) dashed = IsDashed(e.Layer?.LineType);
        else if (lt.Name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) dashed = block?.dashed ?? false;
        else dashed = IsDashed(lt);
        int w = (int)e.LineWeight;
        if (w == (int)LineWeightType.ByLayer) w = e.Layer != null ? (int)e.Layer.LineWeight : -1;
        else if (w == (int)LineWeightType.ByBlock) w = block?.weight ?? -1;
        return (dashed, w >= 0 ? w : -1);
    }

    /// <summary>A block named as a door: "door", "doorl", "Door-900", "DR01", "FD30", "fire door".</summary>
    public static bool IsDoorBlock(string? name) => name != null && !name.StartsWith("*") &&
        System.Text.RegularExpressions.Regex.IsMatch(name, @"door|^dr[-_ ]?\d|^fd[-_ ]?\d|^d[-_]?\d{3,4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static bool IsDashed(ACadSharp.Tables.LineType? lt)
    {
        if (lt == null || lt.Name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)
            || lt.Name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || lt.Name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) return false;
        return lt.Segments.Any() || System.Text.RegularExpressions.Regex.IsMatch(lt.Name, "dash|hid|dot|cent|phant|border|divide", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
