using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SurveyToRevit.Core.Survey;

/// <summary>Sheet height fit of one section: z (mm above datum) = A * y (sheet mm) + B, from the level marks on it.</summary>
public sealed record HeightFit(double A, double B, double ResidualMm, IReadOnlyList<double[]> Used, IReadOnlyList<double[]> Rejected);

/// <summary>Where the section sits along its cut: plan distance along the cut = Shift + (Mirrored ? -1 : 1) * sheet x * A.</summary>
public sealed record SectionRegistration(bool Mirrored, double ShiftMm, int Matched, int PlanFaces, double Score, bool Sure, string Why);

/// <summary>A band, cornice or string course on one end's profile: projects at least 50 mm over at most 1 m of height.</summary>
public sealed record ProfileBand(double FromZ, double ToZ, double ProjectionMm);

/// <summary>One end of a section: the facade it cuts, the outer profile (out from the section's own wall face, z) and what it says.</summary>
public sealed class SectionEnd
{
    public string End { get; set; } = "";
    /// <summary>A 1 m piece of the facade the cut meets, model mm: x1, y1, x2, y2.</summary>
    public double[]? FacadeLine { get; set; }
    /// <summary>How far the section's wall face stands outside the plan's face here (mm, negative = inside). Profiles below are measured from the section's own face, so this offset is already taken out of them.</summary>
    public double? OffsetMm { get; set; }
    public string? Class { get; set; }
    public string Why { get; set; } = "";
    public double? SlopeDeg { get; set; }
    public double[]? Eaves { get; set; }
    public double[]? Kerb { get; set; }
    public List<ProfileBand> Bands { get; set; } = new();
    /// <summary>The outer profile as (out mm, z mm) pairs, points kept only where it changes by 5 mm or more.</summary>
    public List<double[]> Profile { get; set; } = new();
}

public sealed class SectionIndexEntry
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public double[]? Cut { get; set; }
    public HeightFit? Fit { get; set; }
    public SectionRegistration? Registration { get; set; }
    public List<SectionEnd> Ends { get; set; } = new();
    public string Note { get; set; } = "";
}

/// <summary>
/// Item H of MCP-AUDIT-AND-TOOLS.md: what a survey section says, published. Its height fit from the level marks (bad marks
/// rejected and named), where it sits along its cut (walls cut in the section matched to the plan's walls crossed by the cut,
/// trying both ways round), the offset of its wall face from the plan's, and the outer profile at each end in 10 mm steps,
/// measured from the section's own face so an offset never leaks into it. The profiles feed the facade-top classes (item C).
/// Deterministic; any survey with sections, level marks and a cut placed from the key plan.
/// </summary>
public static class SectionProfiles
{
    public const double FitTolMm = 50, StepMm = 10, MatchMm = 60, BandMinMm = 50, BandMaxHeightMm = 1000, ProbeAboveFloorMm = 500;

    /// <summary>z = A·y + B through the most marks within 50 mm (pairs tried, then least squares on the inliers).</summary>
    public static HeightFit? Fit(IReadOnlyList<(double Y, double Mm)> marks, double tolMm = FitTolMm)
    {
        if (marks.Count < 2) return null;
        (double a, double b, int n, double sse) best = (0, 0, 0, double.MaxValue);
        for (int i = 0; i < marks.Count; i++)
            for (int j = i + 1; j < marks.Count; j++)
            {
                double dy = marks[j].Y - marks[i].Y, dz = marks[j].Mm - marks[i].Mm;
                if (Math.Abs(dy) < 1e-6 || Math.Abs(dz) < 300) continue;
                double a = dz / dy; if (a <= 0) continue; // heights grow up the sheet
                double b = marks[i].Mm - a * marks[i].Y;
                var r = marks.Select(m => Math.Abs(a * m.Y + b - m.Mm)).Where(x => x <= tolMm).ToList();
                double sse = r.Sum(x => x * x);
                if (r.Count > best.n || (r.Count == best.n && sse < best.sse)) best = (a, b, r.Count, sse);
            }
        if (best.n < 2) return null;
        var inl = marks.Where(m => Math.Abs(best.a * m.Y + best.b - m.Mm) <= tolMm).ToList();
        double my = inl.Average(m => m.Y), mz = inl.Average(m => m.Mm);
        double sxx = inl.Sum(m => (m.Y - my) * (m.Y - my)), sxy = inl.Sum(m => (m.Y - my) * (m.Mm - mz));
        double A = sxx > 1e-9 ? sxy / sxx : best.a, B = mz - A * my;
        var used = marks.Where(m => Math.Abs(A * m.Y + B - m.Mm) <= tolMm).Select(m => new[] { Math.Round(m.Mm), Math.Round(A * m.Y + B - m.Mm, 1) }).ToList();
        var rejected = marks.Where(m => Math.Abs(A * m.Y + B - m.Mm) > tolMm).Select(m => new[] { Math.Round(m.Mm), Math.Round(A * m.Y + B - m.Mm, 1) }).ToList();
        if (used.Count < 2) return null;
        return new HeightFit(Math.Round(A, 6), Math.Round(B, 1), Math.Round(Math.Sqrt(used.Average(u => u[1] * u[1])), 1), used, rejected);
    }

    /// <summary>Sheet x (mm) where non-horizontal lines cross sheet height y.</summary>
    public static List<double> Crossings(IEnumerable<Segment2> lines, double y, double minAngleDeg = 2)
    {
        var xs = new List<double>();
        foreach (var s in lines)
        {
            double dy = s.B.Y - s.A.Y, dx = s.B.X - s.A.X;
            if (Math.Abs(Math.Atan2(Math.Abs(dy), Math.Abs(dx)) * 180 / Math.PI) < minAngleDeg) continue;
            if ((s.A.Y - y) * (s.B.Y - y) > 0) continue;
            xs.Add(s.A.X + dx * (y - s.A.Y) / dy);
        }
        return xs;
    }

    /// <summary>
    /// Section x (mm along the sheet, already scaled to the building) against plan face positions along the cut: the
    /// shift and way round that put most section lines within 60 mm of a plan face. Sure when at least 4 match, at least
    /// 40% of the plan faces are found and the other way round does clearly worse.
    /// </summary>
    public static SectionRegistration? Register(IReadOnlyList<(IReadOnlyList<double> Plan, IReadOnlyList<double> Section)> probes)
    {
        int planFaces = probes.Sum(p => p.Plan.Count);
        if (planFaces < 2 || probes.Sum(p => p.Section.Count) < 2) return null;
        (bool m, double shift, int n) Best(bool mirrored)
        {
            int sign = mirrored ? -1 : 1; (bool, double, int) best = (mirrored, 0, 0);
            var shifts = probes.SelectMany(p => p.Plan.SelectMany(t => p.Section.Select(x => t - sign * x))).ToList();
            foreach (var sh in shifts)
            {
                int n = probes.Sum(p => p.Plan.Count(t => p.Section.Any(x => Math.Abs(sh + sign * x - t) <= MatchMm)));
                if (n > best.Item3) best = (mirrored, sh, n);
            }
            return best;
        }
        var a = Best(false); var b = Best(true);
        var win = a.n >= b.n ? a : b; var other = a.n >= b.n ? b : a;
        // refine the shift: mean of the matched differences
        int sg = win.m ? -1 : 1;
        var diffs = probes.SelectMany(p => p.Plan.Select(t => p.Section.Select(x => t - (win.shift + sg * x)).OrderBy(Math.Abs).FirstOrDefault(double.NaN)))
            .Where(d => !double.IsNaN(d) && Math.Abs(d) <= MatchMm).ToList();
        double shift = win.shift + (diffs.Count > 0 ? diffs.Average() : 0);
        double score = (double)win.n / planFaces;
        bool sure = win.n >= 4 && score >= 0.4 && win.n >= other.n + 2;
        string why = $"{win.n} of {planFaces} plan wall faces found in the section ({score:P0}); the other way round finds {other.n}";
        return new SectionRegistration(win.m, Math.Round(shift), win.n, planFaces, Math.Round(score, 2), sure, why);
    }

    /// <summary>Plan wall faces crossed by the cut, as distances (mm) from the cut's start. Walls at under 45° to the cut are skipped.</summary>
    public static List<double> PlanFaces(Segment2 cut, IEnumerable<WallCandidate> walls)
    {
        var u = cut.Direction; var res = new List<double>();
        foreach (var w in walls)
        {
            var c = w.Centerline; var d = c.Direction;
            double sin = Math.Abs(u.X * d.Y - u.Y * d.X);
            if (sin < Math.Sin(Math.PI / 4)) continue;
            // intersection of the cut's line with the wall's centreline (within the wall's length)
            double den = u.X * d.Y - u.Y * d.X;
            var w0 = c.A - cut.A;
            double t = (w0.X * d.Y - w0.Y * d.X) / den, s = (w0.X * u.Y - w0.Y * u.X) / den;
            if (s < -50 || s > c.Length + 50 || t < -500 || t > cut.Length + 500) continue;
            double half = w.MeasuredThickness / 2 / sin;
            res.Add(t - half); res.Add(t + half);
        }
        return res.OrderBy(x => x).ToList();
    }

    /// <summary>Outer profile of one end: out (mm, positive = proud of the face) at each z step, from a list of t (along the cut) per z.</summary>
    public static List<(double Out, double Z)> EndProfile(IReadOnlyList<(double Z, double T)> outer, double faceT, bool atStart) =>
        outer.Select(p => (Out: atStart ? faceT - p.T : p.T - faceT, p.Z)).ToList();

    public static List<ProfileBand> Bands(IReadOnlyList<(double Out, double Z)> prof)
    {
        var res = new List<ProfileBand>(); int i = 0;
        while (i < prof.Count)
        {
            if (prof[i].Out < BandMinMm) { i++; continue; }
            int j = i; while (j + 1 < prof.Count && prof[j + 1].Out >= BandMinMm) j++;
            double h = prof[j].Z - prof[i].Z;
            if (h <= BandMaxHeightMm) res.Add(new ProfileBand(Math.Round(prof[i].Z), Math.Round(prof[j].Z), Math.Round(prof.Skip(i).Take(j - i + 1).Max(p => p.Out))));
            i = j + 1;
        }
        return res;
    }

    static List<double[]> Thin(IReadOnlyList<(double Out, double Z)> prof)
    {
        var res = new List<double[]>();
        for (int i = 0; i < prof.Count; i++)
            if (i == 0 || i == prof.Count - 1 || Math.Abs(prof[i].Out - res[^1][0]) >= 5) res.Add(new[] { Math.Round(prof[i].Out), Math.Round(prof[i].Z) });
        return res;
    }

    /// <summary>
    /// Everything for one section. <paramref name="lines"/>: the section drawing's lines (sheet mm, annotation left out);
    /// <paramref name="marks"/>: its level marks (sheet y, mm). Levels give the plan walls to register against and the
    /// storey whose face the profiles are measured from (the storey below the top).
    /// </summary>
    public static (SectionIndexEntry Entry, List<StretchProfile> Profiles) Build(SurveySection s, IReadOnlyList<Segment2> lines,
        IReadOnlyList<(double Y, double Mm)> marks, IReadOnlyList<SurveyLevel> levels)
    {
        var e = new SectionIndexEntry { Name = s.Name, File = s.File, Cut = s.Cut is { } c0 ? new[] { Math.Round(c0.A.X), Math.Round(c0.A.Y), Math.Round(c0.B.X), Math.Round(c0.B.Y) } : null };
        var profiles = new List<StretchProfile>();
        e.Fit = Fit(marks);
        if (e.Fit == null) { e.Note = $"no height fit: {marks.Count} level marks, fewer than 2 agree within {FitTolMm:0} mm"; return (e, profiles); }
        if (s.Cut == null) { e.Note = "cut line not placed from the key plan: the profile can't be tied to a facade"; return (e, profiles); }
        double A = e.Fit.A, B = e.Fit.B;
        double Y(double z) => (z - B) / A;
        var used = levels.Where(l => l.Walls.Walls.Count > 0 && !double.IsNaN(l.ElevationMm)).OrderBy(l => l.ElevationMm).ToList();
        double zLo = lines.Min(l => Math.Min(l.A.Y, l.B.Y)) * A + B, zHi = lines.Max(l => Math.Max(l.A.Y, l.B.Y)) * A + B;
        var probes = used.Where(l => l.ElevationMm + ProbeAboveFloorMm > zLo && l.ElevationMm + ProbeAboveFloorMm < zHi)
            .Select(l => ((IReadOnlyList<double>)PlanFaces(s.Cut, l.Walls.Walls), (IReadOnlyList<double>)Crossings(lines, Y(l.ElevationMm + ProbeAboveFloorMm)).Select(x => x * A).ToList()))
            .ToList();
        e.Registration = Register(probes);
        if (e.Registration == null) { e.Note = "no floor of the plan lies within the section's height to register it against"; return (e, profiles); }
        int sg = e.Registration.Mirrored ? -1 : 1;
        double T(double x) => e.Registration.ShiftMm + sg * x * A;
        // the outer line along the cut at each height
        var outerA = new List<(double Z, double T)>(); var outerB = new List<(double Z, double T)>();
        for (double z = Math.Ceiling(zLo / StepMm) * StepMm; z <= zHi; z += StepMm)
        {
            var ts = Crossings(lines, Y(z)).Select(T).ToList();
            if (ts.Count == 0) continue;
            outerA.Add((z, ts.Min())); outerB.Add((z, ts.Max()));
        }
        var (top, below) = FacadeTops.TopStoreys(levels);
        var faceStorey = below ?? top;
        if (faceStorey == null || outerA.Count == 0) { e.Note = "no storey to measure the face from"; return (e, profiles); }
        double f0 = faceStorey.ElevationMm + 500, f1 = faceStorey.ElevationMm + 2000;
        var planFaces = PlanFaces(s.Cut, faceStorey.Walls.Walls);
        var u = s.Cut.Direction; var n = new Vec2(-u.Y, u.X);
        foreach (var (name, outer, atStart) in new[] { ("A", outerA, true), ("B", outerB, false) })
        {
            var face = outer.Where(p => p.Z >= f0 && p.Z <= f1).Select(p => p.T).OrderBy(t => t).ToList();
            if (face.Count == 0) continue;
            double faceT = face[face.Count / 2];
            var end = new SectionEnd { End = name };
            var prof = EndProfile(outer, faceT, atStart);
            end.Profile = Thin(prof); end.Bands = Bands(prof);
            if (planFaces.Count > 0)
            {
                double planT = atStart ? planFaces.First() : planFaces.Last();
                end.OffsetMm = Math.Round(atStart ? planT - faceT : faceT - planT);
                var at = s.Cut.A + u * planT;
                end.FacadeLine = new[] { Math.Round(at.X - n.X * 500), Math.Round(at.Y - n.Y * 500), Math.Round(at.X + n.X * 500), Math.Round(at.Y + n.Y * 500) };
            }
            if (top != null)
            {
                var sp = new StretchProfile(end.FacadeLine is { } fl ? new Segment2(new Vec2(fl[0], fl[1]), new Vec2(fl[2], fl[3])) : s.Cut, prof, $"{s.Name} end {name}");
                var (cls, why, slope, eaves, kerb) = FacadeTops.FromProfile(sp, top.ElevationMm);
                end.Class = cls; end.Why = why; end.SlopeDeg = slope; end.Eaves = eaves; end.Kerb = kerb;
                // only a section that is surely the right way round speaks for a facade
                if (e.Registration.Sure && end.FacadeLine != null) profiles.Add(sp);
            }
            e.Ends.Add(end);
        }
        e.Note = e.Registration.Sure ? $"registered: {e.Registration.Why}" : $"not sure which way round ({e.Registration.Why}): profiles published, not used for the facade tops";
        return (e, profiles);
    }

    static readonly JsonSerializerOptions Json = Freeze(new JsonSerializerOptions
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    });
    static JsonSerializerOptions Freeze(JsonSerializerOptions o) { o.MakeReadOnly(); return o; }

    public static string ToJson(IReadOnlyList<SectionIndexEntry> entries) => JsonSerializer.Serialize(new { version = 1, sections = entries }, Json);

    /// <summary>Writes section-index.json to the reports folder (project subfolder) and the survey folder's _prep.</summary>
    public static List<string> Save(IReadOnlyList<SectionIndexEntry> entries, string reportsRoot, string projectTitle, string? surveyFolder) =>
        SharedFiles.Write("section-index.json", ToJson(entries), reportsRoot, projectTitle, surveyFolder);
}

/// <summary>The files the engine shares with the hub (section 4 of MCP-AUDIT-AND-TOOLS.md): reports/&lt;project&gt;/ and &lt;survey&gt;/_prep/.</summary>
public static class SharedFiles
{
    public static List<string> Write(string fileName, string text, string reportsRoot, string projectTitle, string? surveyFolder)
    {
        var written = new List<string>();
        string safe = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(projectTitle ?? ""), @"[^\w\- ]", "_");
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(reportsRoot)) dirs.Add(string.IsNullOrEmpty(safe) ? reportsRoot : Path.Combine(reportsRoot, safe));
        if (!string.IsNullOrEmpty(surveyFolder)) dirs.Add(Path.Combine(surveyFolder, "_prep"));
        foreach (var dir in dirs)
            try { Directory.CreateDirectory(dir); var p = Path.Combine(dir, fileName); File.WriteAllText(p, text); written.Add(p); } catch { }
        return written;
    }
}
