using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SurveyToRevit.Core.Survey;

/// <summary>A section's outer line across one facade stretch: (out, z) mm, out from the facade line (+ in front), z above
/// datum. From the survey sections (item H) or the MCP side's section-index.json; none is needed.</summary>
public sealed record StretchProfile(Segment2 Line, IReadOnlyList<(double Out, double Z)> Points, string Source);

/// <summary>What the top of one facade stretch is (item C of MCP-AUDIT-AND-TOOLS.md), from the evidence, with how sure.</summary>
public sealed class FacadeTopCall
{
    public string Id { get; set; } = "";
    public double[] Line { get; set; } = Array.Empty<double>();
    public string Level { get; set; } = "";
    /// <summary>FlatParapet, SetBackStorey, Mansard, Pitched or Unknown.</summary>
    public string Class { get; set; } = "Unknown";
    public string Confidence { get; set; } = "Low";
    public string Evidence { get; set; } = "";
    public string Rejected { get; set; } = "";
    public string Settle { get; set; } = "";
    /// <summary>How far the top storey's face stands back from the facade below (mm), when it has walls there.</summary>
    public double? SetbackMm { get; set; }
    public double? TopWallThicknessMm { get; set; }
    public int TopWindowLines { get; set; }
    /// <summary>Sloped classes: eaves and kerb/ridge as (out, z), and the slope in degrees.</summary>
    public double[]? Eaves { get; set; }
    public double[]? Kerb { get; set; }
    public double? SlopeDeg { get; set; }
    /// <summary>Mansard: the plan's cut through the slope left out of the walls (lines x1, y1, x2, y2).</summary>
    public List<double[]> SkinLinesLeftOut { get; set; } = new();
    public List<double[]> DormerFrontsKept { get; set; } = new();
}

public static class FacadeTops
{
    public const double MinStretchMm = 2000, MaxSetbackMm = 3500, FlushMm = 150, ThickWallMm = 200, MinRiseMm = 1500, MansardDeg = 45;

    static double[] L(Segment2 s) => new[] { Math.Round(s.A.X), Math.Round(s.A.Y), Math.Round(s.B.X), Math.Round(s.B.Y) };

    /// <summary>The storey a facade's top sits on: the highest floor with an outline and walls, and the one below it.</summary>
    public static (SurveyLevel? Top, SurveyLevel? Below) TopStoreys(IReadOnlyList<SurveyLevel> levels)
    {
        var withWalls = levels.Where(l => l.Outline != null && l.Walls.Walls.Count > 0 && !double.IsNaN(l.ElevationMm)).OrderBy(l => l.ElevationMm).ToList();
        return withWalls.Count < 2 ? (withWalls.LastOrDefault(), null) : (withWalls[^1], withWalls[^2]);
    }

    /// <summary>The outer face lines of the storey below the top (its outline's edges, long enough to be a facade).</summary>
    public static List<Segment2> Stretches(Polygon2 outline)
    {
        var v = outline.Vertices; var res = new List<Segment2>();
        for (int i = 0; i < v.Count; i++) { var s = new Segment2(v[i], v[(i + 1) % v.Count], "outline"); if (s.Length >= MinStretchMm) res.Add(s); }
        return res;
    }

    /// <summary>
    /// Classes each stretch of the storey below the top. Evidence, strongest first: a decision (survey-decisions.json
    /// facadeTop with a line), a section profile across it, a flat roof along it, then the plan alone (set-back, wall
    /// thickness, windows). Mansard and Pitched only from a profile or a decision: the plan alone raises a question, never
    /// removes a wall.
    /// </summary>
    public static List<FacadeTopCall> Classify(IReadOnlyList<SurveyLevel> levels, IReadOnlyList<Segment2> topWindowLines,
        IReadOnlyList<RoofCandidate>? roofs = null, IReadOnlyList<StretchProfile>? profiles = null, SurveyDecisions? decisions = null)
    {
        var calls = new List<FacadeTopCall>();
        var (top, below) = TopStoreys(levels);
        if (top == null || below?.Outline == null) return calls;
        var centre = new Vec2(below.Outline.Vertices.Average(p => p.X), below.Outline.Vertices.Average(p => p.Y));
        int k = 0;
        foreach (var e in Stretches(below.Outline))
        {
            var d = e.Direction; var n = new Vec2(-d.Y, d.X);
            var mid = new Vec2((e.A.X + e.B.X) / 2, (e.A.Y + e.B.Y) / 2);
            if ((centre.X - mid.X) * n.X + (centre.Y - mid.Y) * n.Y < 0) n = new Vec2(-n.X, -n.Y); // n points inside
            var call = new FacadeTopCall { Id = $"T{++k}", Line = L(e), Level = top.Name };
            // the top storey's walls along this stretch, and how far in
            var along = top.Walls.Walls.Select(w => (w, inset: Inset(w.Centerline, e, d, n))).Where(x => x.inset is double).Select(x => (x.w, inset: x.inset!.Value)).ToList();
            var nearest = along.OrderBy(x => x.inset).FirstOrDefault();
            if (along.Count > 0)
            {
                call.SetbackMm = Math.Round(Math.Max(0, nearest.inset - nearest.w.MeasuredThickness / 2));
                call.TopWallThicknessMm = Math.Round(nearest.w.MeasuredThickness);
                call.TopWindowLines = topWindowLines.Count(s => along.Any(a => NearLine(a.w.Centerline, s, a.w.MeasuredThickness / 2 + 300)));
            }
            var dec = decisions?.FacadeTop.FirstOrDefault(x => x.Line is { Length: 4 } dl && Overlaps(e, new Segment2(new Vec2(dl[0], dl[1]), new Vec2(dl[2], dl[3]))));
            var prof = profiles?.FirstOrDefault(p => Overlaps(e, p.Line));
            if (dec != null && IsClass(dec.Class))
            {
                call.Class = dec.Class; call.Confidence = "High"; call.Evidence = $"{SurveyDecisions.FileName}: {dec.Evidence}".TrimEnd(' ', ':');
                call.Settle = "decided; remove the entry to let the drawings decide";
            }
            else if (prof != null && FromProfile(prof, top.ElevationMm) is var pf && pf.Class != null)
            {
                call.Class = pf.Class; call.Confidence = "High"; call.Evidence = $"section {prof.Source}: {pf.Why}";
                call.SlopeDeg = pf.Slope; call.Eaves = pf.Eaves; call.Kerb = pf.Kerb;
                call.Rejected = pf.Class == "Mansard" ? "SetBackStorey (the section's face slopes)" : pf.Class == "SetBackStorey" ? "Mansard (the section's face is vertical)" : "";
            }
            else if (along.Count == 0 || call.SetbackMm <= FlushMm)
            {
                // the facade runs up flush (or nothing stands on this edge): a flat roof along it is a parapet top
                var flat = roofs?.FirstOrDefault(r => r.Outline.Vertices.Any(p => DistToLine(p, e) <= 1500) && r.ElevationMm >= top.ElevationMm - 500);
                if (flat != null) { call.Class = "FlatParapet"; call.Confidence = "Medium"; call.Evidence = $"a flat roof at {flat.ElevationMm / 1000:0.00} m along it ({flat.Note})"; }
                else { call.Class = "Unknown"; call.Confidence = "Low"; call.Evidence = along.Count == 0 ? "no top-storey wall stands on this edge and no roof plan shows it" : "the top storey's face is flush with the facade below; no roof plan or section shows its top"; }
                call.Settle = "a section crossing it, the roof plan, or a photo of the roofline";
                call.Rejected = "Mansard, Pitched (nothing shows a slope)";
            }
            else if (call.TopWallThicknessMm >= ThickWallMm || call.TopWindowLines > 0)
            {
                call.Class = "SetBackStorey"; call.Confidence = "Low";
                call.Evidence = $"the plan: the top storey stands {call.SetbackMm:0} mm back, walls {call.TopWallThicknessMm:0} mm{(call.TopWindowLines > 0 ? $", window lines in them ({call.TopWindowLines})" : "")}";
                call.Rejected = "Mansard (a mansard's cut is thin and windowless, except at dormers)";
                call.Settle = "a section crossing it (vertical face = set-back storey) or a photo";
            }
            else
            {
                call.Class = "Unknown"; call.Confidence = "Low";
                call.Evidence = $"the plan: thin ({call.TopWallThicknessMm:0} mm) windowless lines {call.SetbackMm:0} mm back from the facade";
                call.Rejected = "Mansard (only a section, the roof plan or the elevation may say so); built as walls, as before";
                call.Settle = "a section crossing it: a face sloping 45° or more is a mansard (then its plan lines are the roof's skin, not walls); a photo";
            }
            calls.Add(call);
        }
        return calls;
    }

    static bool IsClass(string c) => c is "FlatParapet" or "SetBackStorey" or "Mansard" or "Pitched" or "Unknown";

    /// <summary>The wall's distance in from the stretch, when it runs along it (within 5°, overlapping, 0 to 3.5 m in).</summary>
    static double? Inset(Segment2 w, Segment2 e, Vec2 d, Vec2 n)
    {
        if (w.Length < 500) return null;
        double cos = Math.Abs(w.Direction.X * d.X + w.Direction.Y * d.Y);
        if (cos < Math.Cos(5 * Math.PI / 180)) return null;
        double ta = (w.A.X - e.A.X) * d.X + (w.A.Y - e.A.Y) * d.Y, tb = (w.B.X - e.A.X) * d.X + (w.B.Y - e.A.Y) * d.Y;
        double lo = Math.Max(Math.Min(ta, tb), 0), hi = Math.Min(Math.Max(ta, tb), e.Length);
        if (hi - lo < 0.3 * Math.Min(w.Length, e.Length)) return null;
        var m = new Vec2((w.A.X + w.B.X) / 2, (w.A.Y + w.B.Y) / 2);
        double inset = (m.X - e.A.X) * n.X + (m.Y - e.A.Y) * n.Y;
        return inset >= -300 && inset <= MaxSetbackMm ? Math.Max(0, inset) : null;
    }

    static bool Overlaps(Segment2 e, Segment2 s)
    {
        var d = e.Direction;
        double cos = Math.Abs(s.Direction.X * d.X + s.Direction.Y * d.Y);
        if (cos < Math.Cos(10 * Math.PI / 180)) return false;
        var m = new Vec2((s.A.X + s.B.X) / 2, (s.A.Y + s.B.Y) / 2);
        if (DistToLine(m, e) > MaxSetbackMm) return false;
        double ta = (s.A.X - e.A.X) * d.X + (s.A.Y - e.A.Y) * d.Y, tb = (s.B.X - e.A.X) * d.X + (s.B.Y - e.A.Y) * d.Y;
        return Math.Min(Math.Max(ta, tb), e.Length) - Math.Max(Math.Min(ta, tb), 0) >= 0.3 * Math.Min(s.Length, e.Length);
    }

    static double DistToLine(Vec2 p, Segment2 e)
    {
        var d = e.Direction; double t = Math.Clamp((p.X - e.A.X) * d.X + (p.Y - e.A.Y) * d.Y, 0, e.Length);
        return p.DistanceTo(new Vec2(e.A.X + d.X * t, e.A.Y + d.Y * t));
    }

    static bool NearLine(Segment2 c, Segment2 s, double tol)
    {
        var mid = new Vec2((s.A.X + s.B.X) / 2, (s.A.Y + s.B.Y) / 2);
        var d = c.Direction; double t = (mid.X - c.A.X) * d.X + (mid.Y - c.A.Y) * d.Y;
        return t >= 0 && t <= c.Length && new Vec2(c.A.X + d.X * t, c.A.Y + d.Y * t).DistanceTo(mid) <= tol;
    }

    /// <summary>A profile's face above the top storey's floor: steep and set back = Mansard; vertical and set back =
    /// SetBackStorey; shallow = Pitched; vertical at the facade = the facade carries on (no class from it).</summary>
    public static (string? Class, string Why, double? Slope, double[]? Eaves, double[]? Kerb) FromProfile(StretchProfile p, double topFloorMm)
    {
        var pts = p.Points.Where(q => q.Z >= topFloorMm - 1000).OrderBy(q => q.Z).ToList();
        if (pts.Count < 2) return (null, "", null, null, null);
        // the face: from the highest point still at the facade (out >= -FlushMm) up to the top
        int i0 = pts.FindLastIndex(q => q.Out >= -FlushMm);
        var eaves = i0 >= 0 ? pts[i0] : pts[0];
        var rest = pts.Skip(Math.Max(0, i0)).ToList();
        var kerb = rest.OrderByDescending(q => q.Z).First();
        double rise = kerb.Z - eaves.Z, run = eaves.Out - kerb.Out;
        double[] E = { Math.Round(eaves.Out), Math.Round(eaves.Z) }, K = { Math.Round(kerb.Out), Math.Round(kerb.Z) };
        if (rise < MinRiseMm) return (null, $"rises only {rise:0} mm above the facade", null, null, null);
        double slope = Math.Atan2(rise, Math.Max(1e-6, run)) * 180 / Math.PI;
        if (run <= FlushMm) return (null, "the face runs straight up at the facade", Math.Round(slope, 1), E, K);
        // vertical part set back: most of the rise with little run
        var setBack = rest.Where(q => q.Out < -FlushMm).ToList();
        if (setBack.Count >= 2 && setBack.Max(q => q.Out) - setBack.Min(q => q.Out) <= FlushMm && setBack.Max(q => q.Z) - setBack.Min(q => q.Z) >= MinRiseMm)
            return ("SetBackStorey", $"its face stands {-setBack.Average(q => q.Out):0} mm back, vertical over {setBack.Max(q => q.Z) - setBack.Min(q => q.Z):0} mm", 90, E, K);
        if (slope >= MansardDeg && slope < 88) return ("Mansard", $"slopes {slope:0.0}° from {eaves.Z / 1000:0.00} m to {kerb.Z / 1000:0.00} m, {run:0} mm back", Math.Round(slope, 1), E, K);
        if (slope < MansardDeg) return ("Pitched", $"slopes {slope:0.0}° from the wall head", Math.Round(slope, 1), E, K);
        return (null, "", null, null, null);
    }

    /// <summary>Along a Mansard stretch, the top storey's thin windowless walls are the plan's cut through the roof's skin:
    /// left out. Walls there that hold window lines are dormer fronts: kept.</summary>
    public static List<WallCandidate> LeaveOutSkins(SurveyLevel top, IReadOnlyList<FacadeTopCall> calls, IReadOnlyList<Segment2> topWindowLines)
    {
        var walls = top.Walls.Walls.ToList();
        foreach (var c in calls.Where(c => c.Class == "Mansard" && c.Confidence != "Low"))
        {
            var e = new Segment2(new Vec2(c.Line[0], c.Line[1]), new Vec2(c.Line[2], c.Line[3]));
            var d = e.Direction; var n = new Vec2(-d.Y, d.X);
            foreach (var w in walls.ToList())
            {
                if (Inset(w.Centerline, e, d, n) is not double inset && Inset(w.Centerline, e, d, new Vec2(-n.X, -n.Y)) is not double) continue;
                if (w.MeasuredThickness >= ThickWallMm || w.FromDecision) continue;
                bool window = topWindowLines.Any(s => NearLine(w.Centerline, s, w.MeasuredThickness / 2 + 300));
                if (window) { c.DormerFrontsKept.Add(L(w.Centerline)); continue; }
                walls.Remove(w); c.SkinLinesLeftOut.Add(L(w.Centerline));
            }
        }
        return walls;
    }

    static readonly JsonSerializerOptions Json = Freeze(new JsonSerializerOptions
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    });
    static JsonSerializerOptions Freeze(JsonSerializerOptions o) { o.MakeReadOnly(); return o; }

    /// <summary>roof-edges.json (section 4 of MCP-AUDIT-AND-TOOLS.md).</summary>
    public static string ToJson(IReadOnlyList<FacadeTopCall> calls) => JsonSerializer.Serialize(new { version = 1, stretches = calls }, Json);

    /// <summary>Writes roof-edges.json to the reports folder (project subfolder) and the survey folder's _prep.</summary>
    public static List<string> Save(IReadOnlyList<FacadeTopCall> calls, string reportsRoot, string projectTitle, string? surveyFolder)
    {
        var text = ToJson(calls); var written = new List<string>();
        string safe = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(projectTitle ?? ""), @"[^\w\- ]", "_");
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(reportsRoot)) dirs.Add(string.IsNullOrEmpty(safe) ? reportsRoot : Path.Combine(reportsRoot, safe));
        if (!string.IsNullOrEmpty(surveyFolder)) dirs.Add(Path.Combine(surveyFolder, "_prep"));
        foreach (var dir in dirs)
            try { Directory.CreateDirectory(dir); var p = Path.Combine(dir, "roof-edges.json"); File.WriteAllText(p, text); written.Add(p); } catch { }
        return written;
    }
}
