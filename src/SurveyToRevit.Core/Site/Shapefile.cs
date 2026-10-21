namespace SurveyToRevit.Core.Site;

/// <summary>
/// Reads polygon outlines from an ESRI shapefile (.shp), e.g. building footprints exported
/// from OS MasterMap Topography in British National Grid metres. Only shapes whose bounding
/// box touches the requested box are kept. Rings drawn clockwise are outlines; anticlockwise
/// rings are holes (courtyards) of the outline before them.
/// </summary>
public static class Shapefile
{
    public sealed record Shape(IReadOnlyList<Vec2> Outer, IReadOnlyList<IReadOnlyList<Vec2>> Holes);

    public static List<Shape> ReadPolygons(string path, double minX, double minY, double maxX, double maxY)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        int BigInt() { var b = br.ReadBytes(4); Array.Reverse(b); return BitConverter.ToInt32(b); }
        if (BigInt() != 9994) throw new InvalidDataException($"{Path.GetFileName(path)} is not a shapefile.");
        fs.Position = 32;
        int fileType = br.ReadInt32();
        if (fileType is not (5 or 15 or 25)) throw new InvalidDataException($"{Path.GetFileName(path)} holds points or lines, not outlines (shape type {fileType}).");
        fs.Position = 100;
        var shapes = new List<Shape>();
        while (fs.Position + 8 <= fs.Length)
        {
            BigInt();
            int words = BigInt();
            long next = fs.Position + words * 2L;
            int type = br.ReadInt32();
            if (type is 5 or 15 or 25)
            {
                double x0 = br.ReadDouble(), y0 = br.ReadDouble(), x1 = br.ReadDouble(), y1 = br.ReadDouble();
                if (x1 >= minX && x0 <= maxX && y1 >= minY && y0 <= maxY)
                {
                    int parts = br.ReadInt32(), points = br.ReadInt32();
                    var starts = Enumerable.Range(0, parts).Select(_ => br.ReadInt32()).ToList();
                    var pts = new Vec2[points];
                    for (int i = 0; i < points; i++) pts[i] = new Vec2(br.ReadDouble(), br.ReadDouble());
                    List<Vec2>? outer = null;
                    var holes = new List<IReadOnlyList<Vec2>>();
                    for (int p = 0; p < parts; p++)
                    {
                        int s = starts[p], e = p + 1 < parts ? starts[p + 1] : points;
                        var ring = pts[s..e].ToList();
                        if (ring.Count > 1 && ring[0].DistanceTo(ring[^1]) < 1e-9) ring.RemoveAt(ring.Count - 1);
                        if (ring.Count < 3) continue;
                        if (new Polygon2(ring).SignedArea < 0) // clockwise: a new outline
                        {
                            if (outer != null) shapes.Add(new Shape(outer, holes));
                            outer = ring; holes = new List<IReadOnlyList<Vec2>>();
                        }
                        else if (outer != null) holes.Add(ring);
                    }
                    if (outer != null) shapes.Add(new Shape(outer, holes));
                }
            }
            fs.Position = next;
        }
        return shapes;
    }
}
