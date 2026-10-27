using System.Text.Json;

namespace SurveyToRevit.Core.TestFit;

/// <summary>A piece of furniture: its kind on the page (desk, chair, sofa...), centre, size and turn (mm, radians anticlockwise).</summary>
/// <remarks>Rotation is how far to turn a family whose front faces -Y (the usual Autodesk convention) so it faces the way the page drew it.</remarks>
/// <remarks>Group: the pieces that go together in one Revit group (a workstation "ws12", else the space's id).</remarks>
public sealed record FitItem(string Kind, string Category, string Zone, Vec2 Center, double WidthMm, double DepthMm, double Rotation)
{
    public string Group { get; init; } = Zone;

    /// <summary>Which way the piece's front points in the model (a family faces -Y before it is turned).</summary>
    public Vec2 Front => new(Math.Sin(Rotation), -Math.Cos(Rotation));
}

/// <summary>A new partition: solid (wall) or glazed (curtain wall); a divider is the operable wall between the halves of a split room.</summary>
public sealed record FitWall(Vec2 A, Vec2 B, bool Glazed, string Zone)
{
    public bool Divider { get; init; }
}

/// <summary>A door in a new partition: hinge, the direction along the wall, and the side it opens to (into the room).</summary>
public sealed record FitDoor(Vec2 Hinge, Vec2 Along, Vec2 Into, double WidthMm, bool Glazed, string Zone)
{
    public Vec2 Center => Hinge + Along * (WidthMm / 2);
}

/// <summary>A space from the page: meeting and focus rooms become Revit rooms. Outline is set for a drawn polygon.</summary>
public sealed record FitZone(string Id, string Name, string Type, string Part, Vec2 Min, Vec2 Max)
{
    public Vec2 Center => (Min + Max) * 0.5;
    public IReadOnlyList<Vec2>? Outline { get; init; }
    /// <summary>A point inside the space (for a polygon, the page's pick; the box centre may be outside an L).</summary>
    public Vec2? Inside { get; init; }
    public Vec2 RoomPoint => Inside ?? Center;
    /// <summary>Its net area as the page measured it (on the floor, less walls and higher-priority spaces).</summary>
    public double AreaM2 { get; init; }
    public int Seats { get; init; }
    /// <summary>Left out of the NIA (a core, plant, riser, landlord area).</summary>
    public bool NotNia { get; init; }
    public bool Enclosed => Type != "core" && Part != "open";
}

/// <summary>
/// The layout the test-fit page sends back with "Send to Revit", in model millimetres. The page works in
/// metres with Y down from the floor's top-left corner (origin, from <see cref="TestFitSite.Frame"/>).
/// </summary>
public sealed class TestFitLayout
{
    /// <summary>
    /// A monitor on each desk, at the back of the desk facing its user, in the desk's group: the page draws one
    /// on every desk, so the model gets one too. Hot desks are left bare, as the page draws them.
    /// </summary>
    public IEnumerable<FitItem> WithMonitors() => Items.SelectMany(it => it.Kind != "desk" || it.Category == "hotdesk" ? new[] { it } : new[]
    {
        it,
        it with { Kind = "monitor", Center = it.Center - it.Front * (it.DepthMm / 2 - 170), WidthMm = 560, DepthMm = 200 },
    });

    public List<FitItem> Items { get; } = new();
    public List<FitWall> Walls { get; } = new();
    public List<FitDoor> Doors { get; } = new();
    public List<FitZone> Zones { get; } = new();
    /// <summary>
    /// The family picked on the page's Assets tab per space use and kind ("work:desk"): its id there, the type, and
    /// the extra turn (degrees anticlockwise) for a family whose front is not towards -Y.
    /// </summary>
    public Dictionary<string, (string Id, string Type, double Turn)> Assets { get; } = new(StringComparer.Ordinal);
    /// <summary>The page's dashboard figures, for the sheet; null from an older page.</summary>
    public FitSummary? Summary { get; private set; }
    public int Desks { get; private set; }
    /// <summary>Also make the study sheet (the page's "Also make a study sheet" box; on by default).</summary>
    public bool Sheet { get; private set; } = true;
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

        var layout = new TestFitLayout { Desks = (int)D(root, "desks"), NiaM2 = D(root, "nia"),
            Sheet = !(root.TryGetProperty("sheet", out var sh) && sh.ValueKind == JsonValueKind.False) };
        foreach (var it in A("items"))
        {
            // page front (fx, fy) with Y down → model front (fx, -fy); turn -Y onto it
            double fx = D(it, "fx"), fy = D(it, "fy");
            var rotation = fx == 0 && fy == 0 ? 0 : Math.Atan2(fx, fy);
            var zone = S(it, "zone");
            layout.Items.Add(new FitItem(S(it, "k"), S(it, "cat"), zone, P(D(it, "x"), D(it, "y")), D(it, "w") * 1000, D(it, "d") * 1000, rotation)
                { Group = S(it, "g") is { Length: > 0 } g ? g : zone });
        }
        foreach (var w in A("walls"))
            layout.Walls.Add(new FitWall(P(D(w, "x1"), D(w, "y1")), P(D(w, "x2"), D(w, "y2")), S(w, "part") == "glazed", S(w, "zone")) { Divider = S(w, "part") == "divider" });
        foreach (var d in A("doors"))
            layout.Doors.Add(new FitDoor(P(D(d, "x"), D(d, "y")), new Vec2(D(d, "dx"), -D(d, "dy")), new Vec2(D(d, "nx"), -D(d, "ny")),
                D(d, "w") * 1000, S(d, "part") == "glazed", S(d, "zone")));
        foreach (var z in A("zones"))
        {
            var areaM2 = D(z, "a");
            Vec2 a = P(D(z, "x"), D(z, "y") + D(z, "d")), b = P(D(z, "x") + D(z, "w"), D(z, "y"));
            List<Vec2>? outline = null;
            if (z.TryGetProperty("pts", out var pts) && pts.ValueKind == JsonValueKind.Array)
                outline = pts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2)
                    .Select(p => P(p[0].GetDouble(), p[1].GetDouble())).ToList();
            Vec2? inside = z.TryGetProperty("ix", out _) ? P(D(z, "ix"), D(z, "iy")) : null;
            layout.Zones.Add(new FitZone(S(z, "id"), S(z, "name"), S(z, "type"), S(z, "part"), a, b)
                { Outline = outline is { Count: >= 3 } ? outline : null, Inside = inside, AreaM2 = areaM2, Seats = (int)D(z, "seats"),
                  NotNia = z.TryGetProperty("nonia", out var n) && n.ValueKind == JsonValueKind.True });
        }
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object)
            foreach (var a in assets.EnumerateObject())
                if (a.Value.ValueKind == JsonValueKind.Object && S(a.Value, "id") is { Length: > 0 } id) layout.Assets[a.Name] = (id, S(a.Value, "type"), D(a.Value, "rot"));
        if (root.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.Object) layout.Summary = FitSummary.Parse(sum);
        return layout;
    }
}

/// <summary>The figures on the page's dashboard when the layout was sent: what the sheet lists.</summary>
public sealed class FitSummary
{
    public double Nia, Gross, NotNia, Density, Achieved;
    public int Desks, Target, TotalSeats, Doors;
    public double GlazedM, SolidM;
    public string Band = "";
    public List<(string Name, int Count)> Seats { get; } = new();
    /// <summary>Space mix: use, label, m² and share of the NIA.</summary>
    public List<(string Type, string Label, double M2, double Pct)> Mix { get; } = new();
    public List<(string Level, string Text)> Checks { get; } = new();

    public static FitSummary Parse(JsonElement e)
    {
        static double D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var s = new FitSummary
        {
            Nia = D(e, "nia"), Gross = D(e, "gross"), NotNia = D(e, "notNia"), Density = D(e, "density"), Achieved = D(e, "achieved"),
            Desks = (int)D(e, "desks"), Target = (int)D(e, "target"), TotalSeats = (int)D(e, "totalSeats"), Band = S(e, "band"),
        };
        if (e.TryGetProperty("seats", out var seats) && seats.ValueKind == JsonValueKind.Object)
            foreach (var p in seats.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) s.Seats.Add((p.Name, p.Value.GetInt32()));
        if (e.TryGetProperty("mix", out var mix) && mix.ValueKind == JsonValueKind.Array)
            foreach (var m in mix.EnumerateArray()) s.Mix.Add((S(m, "type"), S(m, "label"), D(m, "m2"), D(m, "pct")));
        if (e.TryGetProperty("partitions", out var parts) && parts.ValueKind == JsonValueKind.Object)
        { s.GlazedM = D(parts, "glazed"); s.SolidM = D(parts, "solid"); s.Doors = (int)D(parts, "doors"); }
        if (e.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
            foreach (var c in checks.EnumerateArray()) if (S(c, "text") is { Length: > 0 } t) s.Checks.Add((S(c, "level"), t));
        return s;
    }
}
