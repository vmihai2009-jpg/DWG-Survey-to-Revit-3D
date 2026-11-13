using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SurveyToRevit.Core.Survey;

/// <summary>
/// survey-decisions.json (item T of MCP-AUDIT-AND-TOOLS.md): calls made outside the engine (the MCP side, from the
/// drawings and site photos, or a person) that a rebuild must keep. Read from the survey folder next to layers.txt and
/// obeyed like it; without the file the build is exactly what the engine does on its own. Millimetres in the model's
/// internal frame; levels as "aod" mm above survey datum.
/// </summary>
public sealed class SurveyDecisions
{
    public const string FileName = "survey-decisions.json";

    public int Version { get; set; } = 1;
    public List<FacadeTopDecision> FacadeTop { get; set; } = new();
    public List<LineDecision> NotWalls { get; set; } = new();
    public List<LineDecision> Walls { get; set; } = new();
    public List<ElevationDecision> Elevations { get; set; } = new();
    public List<LevelDecision> Levels { get; set; } = new();
    public List<WindowDecision> Windows { get; set; } = new();
    public List<long> Keep { get; set; } = new();

    /// <summary>Where it was read from.</summary>
    [JsonIgnore] public string? Path { get; set; }

    static readonly JsonSerializerOptions Json = Freeze(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    });
    static JsonSerializerOptions Freeze(JsonSerializerOptions o) { o.MakeReadOnly(); return o; }

    public static SurveyDecisions Parse(string json) => JsonSerializer.Deserialize<SurveyDecisions>(json, Json) ?? new SurveyDecisions();

    /// <summary>The file in the first of these folders that has one, or null.</summary>
    public static SurveyDecisions? Find(IEnumerable<string> folders, List<string> notes)
    {
        foreach (var f in folders.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var p = System.IO.Path.Combine(f, FileName);
            if (!File.Exists(p)) continue;
            try { var d = Parse(File.ReadAllText(p)); d.Path = p; return d; }
            catch (Exception ex) { notes.Add($"CHECK {FileName} at {p} could not be read ({ex.Message}); built without it."); return null; }
        }
        return null;
    }
}

public abstract class DecisionBase
{
    public string By { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Date { get; set; } = "";
}

public sealed class FacadeTopDecision : DecisionBase
{
    public string Facade { get; set; } = "";
    public double? From { get; set; }
    public double? To { get; set; }
    public string Class { get; set; } = "";
}

public sealed class LineDecision : DecisionBase
{
    /// <summary>Level code ("06") or name ("Sixth Floor").</summary>
    public string Level { get; set; } = "";
    /// <summary>x1, y1, x2, y2 (mm).</summary>
    public double[] Line { get; set; } = Array.Empty<double>();
    public double? Thickness { get; set; }
}

public sealed class ElevationDecision : DecisionBase
{
    public string Sheet { get; set; } = "";
    public double[] XRange { get; set; } = Array.Empty<double>();
    public string Facade { get; set; } = "";
    public bool Mirrored { get; set; }
}

public sealed class LevelDecision : DecisionBase
{
    public string Level { get; set; } = "";
    public double Aod { get; set; }
    public string Source { get; set; } = "";
}

public sealed class WindowDecision : DecisionBase
{
    public string Facade { get; set; } = "";
    public double U { get; set; }
    public double Sill { get; set; }
    public double Head { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>What happened to one decision on this reading.</summary>
public sealed record DecisionOutcome(string Kind, string What, string State, string Detail);

public static class DecisionRules
{
    public const double MatchMm = 50, MatchDeg = 2;

    static bool IsLevel(SurveyLevel l, string want) =>
        string.Equals(l.Code, want, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, want, StringComparison.OrdinalIgnoreCase);

    static Segment2? Seg(double[] a) => a is { Length: 4 } ? new Segment2(new Vec2(a[0], a[1]), new Vec2(a[2], a[3]), "decision", "decision") : null;

    /// <summary>Does this wall lie on the decision's line: within 2 degrees, the line's middle within 50 mm (plus half the
    /// wall: the line may be a face), and the wall covering at least half of the line?</summary>
    public static bool OnLine(WallCandidate w, Segment2 line)
    {
        var c = w.Centerline; if (c.Length < 1 || line.Length < 1) return false;
        double cos = Math.Abs(c.Direction.X * line.Direction.X + c.Direction.Y * line.Direction.Y);
        if (cos < Math.Cos(MatchDeg * Math.PI / 180)) return false;
        var d = c.Direction; var n = new Vec2(-d.Y, d.X);
        var mid = new Vec2((line.A.X + line.B.X) / 2, (line.A.Y + line.B.Y) / 2);
        double off = Math.Abs((mid.X - c.A.X) * n.X + (mid.Y - c.A.Y) * n.Y);
        if (off > MatchMm + w.MeasuredThickness / 2) return false;
        double t0 = (line.A.X - c.A.X) * d.X + (line.A.Y - c.A.Y) * d.Y, t1 = (line.B.X - c.A.X) * d.X + (line.B.Y - c.A.Y) * d.Y;
        double lo = Math.Max(Math.Min(t0, t1), 0), hi = Math.Min(Math.Max(t0, t1), c.Length);
        return hi - lo >= 0.5 * line.Length;
    }

    /// <summary>levels: each floor named in the file takes its height.</summary>
    public static List<DecisionOutcome> ApplyLevels(SurveyDecisions d, IList<SurveyLevel> levels)
    {
        var res = new List<DecisionOutcome>();
        foreach (var x in d.Levels)
        {
            var l = levels.FirstOrDefault(l => IsLevel(l, x.Level));
            if (l == null) { res.Add(new("levels", x.Level, "stale", "no floor of that code or name")); continue; }
            double was = l.ElevationMm; l.ElevationMm = x.Aod;
            res.Add(new("levels", l.Name, "applied", $"{was / 1000:0.000} m -> {x.Aod / 1000:0.000} m ({x.Source}{(string.IsNullOrEmpty(x.Reason) ? "" : ": " + x.Reason)})"));
        }
        return res;
    }

    /// <summary>notWalls and walls on one floor: walls on a notWalls line are removed, unless window lines lie on them
    /// (a wall that holds a window is kept and the decision reported as contradicted: a dormer front); walls in the file
    /// are added (tagged as decisions) unless one is already there.</summary>
    public static (List<WallCandidate> Walls, List<DecisionOutcome> Outcomes) ApplyWalls(SurveyDecisions d, SurveyLevel level, IReadOnlyList<WallCandidate> walls, IReadOnlyList<Segment2> windowLines)
    {
        var res = new List<DecisionOutcome>(); var list = walls.ToList();
        foreach (var x in d.NotWalls.Where(x => IsLevel(level, x.Level)))
        {
            if (Seg(x.Line) is not Segment2 line) { res.Add(new("notWalls", level.Name, "stale", "line needs 4 numbers")); continue; }
            var hit = list.Where(w => OnLine(w, line)).ToList();
            if (hit.Count == 0) { res.Add(new("notWalls", Describe(line), "stale", $"no wall on {level.Name} lies on it")); continue; }
            foreach (var w in hit)
            {
                int wins = windowLines.Count(s => Near(w.Centerline, s, w.MeasuredThickness / 2 + 300));
                if (wins > 0) { res.Add(new("notWalls", Describe(line), "contradicted", $"the wall there holds window lines ({wins}): kept (a dormer front?)")); continue; }
                list.Remove(w);
                res.Add(new("notWalls", Describe(line), "applied", $"wall {w.TypeName} {w.Length / 1000:0.0} m removed on {level.Name}{(string.IsNullOrEmpty(x.Reason) ? "" : ": " + x.Reason)}"));
            }
        }
        foreach (var x in d.Walls.Where(x => IsLevel(level, x.Level)))
        {
            if (Seg(x.Line) is not Segment2 line) { res.Add(new("walls", level.Name, "stale", "line needs 4 numbers")); continue; }
            if (list.Any(w => OnLine(w, line))) { res.Add(new("walls", Describe(line), "stale", $"a wall is already there on {level.Name}")); continue; }
            double t = x.Thickness ?? 215;
            list.Add(new WallCandidate(line, t, t, Confidence.High, "decision", "decision") { FromDecision = true });
            res.Add(new("walls", Describe(line), "applied", $"{t:0} mm wall added on {level.Name}{(string.IsNullOrEmpty(x.Reason) ? "" : ": " + x.Reason)}"));
        }
        return (list, res);
    }

    /// <summary>Kinds this version reads but doesn't act on yet: listed as waiting, never dropped.</summary>
    public static List<DecisionOutcome> Waiting(SurveyDecisions d)
    {
        var res = new List<DecisionOutcome>();
        foreach (var x in d.FacadeTop) res.Add(new("facadeTop", $"{x.Facade} {x.Class}", "waiting", "read; facade tops are built from it once item C lands"));
        foreach (var x in d.Elevations) res.Add(new("elevations", $"{x.Sheet} -> {x.Facade}", "waiting", "read; used once elevations are read (item E)"));
        foreach (var x in d.Windows) res.Add(new("windows", $"{x.Facade} u {x.U:0}", "waiting", "read; used once elevation windows are read (item F)"));
        if (d.Keep.Count > 0) res.Add(new("keep", $"{d.Keep.Count} elements", "waiting", "read; rebuilds keep them once item U lands"));
        return res;
    }

    static bool Near(Segment2 c, Segment2 s, double tol)
    {
        var mid = new Vec2((s.A.X + s.B.X) / 2, (s.A.Y + s.B.Y) / 2);
        var d = c.Direction; double t = (mid.X - c.A.X) * d.X + (mid.Y - c.A.Y) * d.Y;
        if (t < 0 || t > c.Length) return false;
        var p = new Vec2(c.A.X + d.X * t, c.A.Y + d.Y * t);
        return p.DistanceTo(mid) <= tol;
    }

    static string Describe(Segment2 s) => $"({s.A.X:0}, {s.A.Y:0}) - ({s.B.X:0}, {s.B.Y:0})";
}
