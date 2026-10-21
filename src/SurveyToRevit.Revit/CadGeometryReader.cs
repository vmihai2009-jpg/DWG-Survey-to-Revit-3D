using Autodesk.Revit.DB;
using SurveyToRevit.Core;

namespace SurveyToRevit.Revit;

/// <summary>
/// Extracts plan linework from a linked or imported DWG (an ImportInstance).
/// Output is in millimetres, in Revit's internal (model) coordinates projected to XY.
/// </summary>
public sealed class CadGeometryReader
{
    public List<Segment2> Segments { get; } = new();
    public int Arcs { get; private set; }
    public int Other { get; private set; }
    public double MinZ { get; private set; } = double.MaxValue;
    public double MaxZ { get; private set; } = double.MinValue;

    readonly Document _doc;
    readonly string _source;
    readonly Dictionary<ElementId, string> _layerNames = new();

    CadGeometryReader(Document doc, string source) { _doc = doc; _source = source; }

    public static CadGeometryReader Read(Document doc, ImportInstance import)
    {
        var reader = new CadGeometryReader(doc, import.UniqueId);
        var geometry = import.get_Geometry(new Options { ComputeReferences = false, IncludeNonVisibleObjects = false });
        if (geometry != null) reader.Walk(geometry, Transform.Identity);
        return reader;
    }

    // Symbol geometry plus an explicit accumulated transform, so nested blocks land in model coordinates.
    void Walk(IEnumerable<GeometryObject> objects, Transform t)
    {
        foreach (var obj in objects)
        {
            switch (obj)
            {
                case GeometryInstance gi:
                    Walk(gi.GetSymbolGeometry(), t.Multiply(gi.Transform));
                    break;
                case Line line:
                    Add(t.OfPoint(line.GetEndPoint(0)), t.OfPoint(line.GetEndPoint(1)), obj);
                    break;
                case PolyLine pl:
                    var pts = pl.GetCoordinates();
                    for (int i = 0; i + 1 < pts.Count; i++) Add(t.OfPoint(pts[i]), t.OfPoint(pts[i + 1]), obj);
                    break;
                case Arc:
                    Arcs++;
                    break;
                default:
                    Other++;
                    break;
            }
        }
    }

    void Add(XYZ p, XYZ q, GeometryObject obj)
    {
        MinZ = Math.Min(MinZ, Math.Min(p.Z, q.Z));
        MaxZ = Math.Max(MaxZ, Math.Max(p.Z, q.Z));
        Segments.Add(new Segment2(
            new Vec2(UnitConversion.FeetToMm(p.X), UnitConversion.FeetToMm(p.Y)),
            new Vec2(UnitConversion.FeetToMm(q.X), UnitConversion.FeetToMm(q.Y)),
            LayerOf(obj), _source));
    }

    string LayerOf(GeometryObject obj)
    {
        var id = obj.GraphicsStyleId;
        if (id == ElementId.InvalidElementId) return "";
        if (!_layerNames.TryGetValue(id, out var name))
        {
            name = (_doc.GetElement(id) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
            _layerNames[id] = name;
        }
        return name;
    }
}
