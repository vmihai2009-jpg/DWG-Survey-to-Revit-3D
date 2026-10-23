using System.Text.Json;

namespace SurveyToRevit.Core.TestFit;

/// <summary>A piece of furniture: its kind on the page (desk, chair, sofa...), centre, size and turn (mm, radians anticlockwise).</summary>
/// <remarks>Rotation is how far to turn a family whose front faces -Y (the usual Autodesk convention) so it faces the way the page drew it.</remarks>
public sealed record FitItem(string Kind, string Category, string Zone, Vec2 Center, double WidthMm, double DepthMm, double Rotation);

/// <summary>A new partition: solid (wall) or glazed (curtain wall).</summary>
public sealed record FitWall(Vec2 A, Vec2 B, bool Glazed, string Zone);

/// <summary>A door in a new partition: hinge, the direction along the wall, and the side it opens to (into the room).</summary>
public sealed record FitDoor(Vec2 Hinge, Vec2 Along, Vec2 Into, double WidthMm, bool Glazed, string Zone)
{
    public Vec2 Center => Hinge + Along * (WidthMm / 2);
}

/// <summary>A space from the page: meeting and focus rooms become Revit rooms.</summary>
public sealed record FitZone(string Id, string Name, string Type, string Part, Vec2 Min, Vec2 Max)
{
    public Vec2 Center => (Min + Max) * 0.5;
    public bool Enclosed => Type != "core" && Part != "open";
}

/// <summary>
/// The layout the test-fit page sends back with "Send to Revit", in model millimetres. The page works in
/// metres with Y down from the floor's top-left corner (origin, from <see cref="TestFitSite.Frame"/>).
/// </summary>
public sealed class TestFitLayout
{
    public List<FitItem> Items { get; } = new();
    public List<FitWall> Walls { get; } = new();
    public List<FitDoor> Doors { get; } = new();
    public List<FitZone> Zones { get; } = new();
    public int Desks { get; private set; }
    public double NiaM2 { get; private set; }

    public static TestFitLayout Parse(string json, Vec2 originMm)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("kind", out var kind) && kind.GetString() != "testfit-layout")
            throw new InvalidDataException("That is not a test-fit layout.");
        Vec2 P(double x, double y) => new(originMm.X + x * 1000, originMm.Y - y * 1000);
        static double D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        IEnumerable<JsonElement> A(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

        var layout = new TestFitLayout { Desks = (int)D(root, "desks"), NiaM2 = D(root, "nia") };
        foreach (var it in A("items"))
        {
            // page front (fx, fy) with Y down → model front (fx, -fy); turn -Y onto it
            double fx = D(it, "fx"), fy = D(it, "fy");
            var rotation = fx == 0 && fy == 0 ? 0 : Math.Atan2(fx, fy);
            layout.Items.Add(new FitItem(S(it, "k"), S(it, "cat"), S(it, "zone"), P(D(it, "x"), D(it, "y")), D(it, "w") * 1000, D(it, "d") * 1000, rotation));
        }
        foreach (var w in A("walls"))
            layout.Walls.Add(new FitWall(P(D(w, "x1"), D(w, "y1")), P(D(w, "x2"), D(w, "y2")), S(w, "part") == "glazed", S(w, "zone")));
        foreach (var d in A("doors"))
            layout.Doors.Add(new FitDoor(P(D(d, "x"), D(d, "y")), new Vec2(D(d, "dx"), -D(d, "dy")), new Vec2(D(d, "nx"), -D(d, "ny")),
                D(d, "w") * 1000, S(d, "part") == "glazed", S(d, "zone")));
        foreach (var z in A("zones"))
        {
            Vec2 a = P(D(z, "x"), D(z, "y") + D(z, "d")), b = P(D(z, "x") + D(z, "w"), D(z, "y"));
            layout.Zones.Add(new FitZone(S(z, "id"), S(z, "name"), S(z, "type"), S(z, "part"), a, b));
        }
        return layout;
    }
}
