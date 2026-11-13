using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SurveyToRevit.Core.Survey;

/// <summary>
/// One call the engine made that wasn't certain, or one check it failed (item V of MCP-AUDIT-AND-TOOLS.md): what,
/// where, how sure and why, what it used, what it rejected, what would settle it, the survey-decisions.json entry that
/// would override it, and how much of the model it touches. Sorted worst first, the list says where a person or the
/// MCP side should act first.
/// </summary>
public sealed class BuildLogEntry
{
    public string Kind { get; set; } = "";
    public string Level { get; set; } = "";
    public string LevelCode { get; set; } = "";
    /// <summary>Model mm: x, y (and z when known).</summary>
    public double[] At { get; set; } = Array.Empty<double>();
    /// <summary>A line in model mm (x1, y1, x2, y2), for walls.</summary>
    public double[]? Line { get; set; }
    public List<long> Ids { get; set; } = new();
    public string Drawing { get; set; } = "";
    public string Layer { get; set; } = "";
    /// <summary>High, Medium or Low.</summary>
    public string Confidence { get; set; } = "Medium";
    public string Why { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Alternative { get; set; } = "";
    public string Settle { get; set; } = "";
    /// <summary>The survey-decisions.json entry kind that would override this call (walls, notWalls, levels, facadeTop...).</summary>
    public string Override { get; set; } = "";
    public double Severity { get; set; }
    /// <summary>What Severity counts: m of wall, m² of slab, windows, items.</summary>
    public string SeverityUnit { get; set; } = "";
    /// <summary>Severity x (1 - confidence): the sort key, largest first.</summary>
    public double Score { get; set; }
}

public sealed class BuildLog
{
    public int Version { get; set; } = 1;
    public string Project { get; set; } = "";
    public DateTime Written { get; set; }
    public int Entries => Items.Count;
    public Dictionary<string, int> ByKind => Items.GroupBy(e => e.Kind).OrderByDescending(g => g.Sum(e => e.Score)).ToDictionary(g => g.Key, g => g.Count());
    public List<BuildLogEntry> Items { get; set; } = new();

    public static double Sureness(string confidence) => confidence switch { "High" => 0.9, "Medium" => 0.6, _ => 0.3 };

    static readonly JsonSerializerOptions Json = Freeze(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(), // inside Revit, options without one fail when frozen (item W)
    });

    static JsonSerializerOptions Freeze(JsonSerializerOptions o) { o.MakeReadOnly(); return o; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public void Add(BuildLogEntry e)
    {
        e.Score = Math.Round(e.Severity * (1 - Sureness(e.Confidence)), 2);
        Items.Add(e);
    }

    /// <summary>Worst first: score, then severity.</summary>
    public void Sort() => Items = Items.OrderByDescending(e => e.Score).ThenByDescending(e => e.Severity).ThenBy(e => e.LevelCode, StringComparer.Ordinal).ToList();

    static double[] Line(Segment2 s) => new[] { Math.Round(s.A.X), Math.Round(s.A.Y), Math.Round(s.B.X), Math.Round(s.B.Y) };
    static double[] Mid(Segment2 s) => new[] { Math.Round((s.A.X + s.B.X) / 2), Math.Round((s.A.Y + s.B.Y) / 2) };

    /// <summary>The calls a reading made: walls it wasn't sure of, floor levels few spot heights agree on, unclear areas,
    /// the self-checks it failed, CHECK notes and skipped drawings.</summary>
    public static BuildLog From(SurveyModel model, string project = "")
    {
        var log = new BuildLog { Project = project, Written = DateTime.Now };
        foreach (var lv in model.Levels)
        {
            string file = Path.GetFileName(lv.Plan?.Path ?? "");
            foreach (var w in lv.Walls.Walls.Where(w => w.Confidence != Confidence.High))
            {
                var (kind, why, alt, settle) =
                    w.FaceA == "bridge" ? ("bridge", "a wall made across a gap no line was drawn in, to close the outline", "leave the gap open", "the plan or elevation at this gap: is there a wall, an opening or nothing?")
                  : w.FaceA == "join" ? ("facade-join", "a wall made to join two facade runs that stop short of each other", "leave the facade open there", "the elevation of this facade at the join")
                  : string.IsNullOrEmpty(w.FaceB) ? ("wall-from-single-line", $"one drawn line, the other face assumed {w.TypeThickness:0} mm away", "not a wall (a line of a fitting, a skin, an edge)", "a section or elevation through it; or the drawing's other layers here")
                  : ("wall-pairing", $"two faces that only partly run alongside each other, {w.MeasuredThickness:0} mm apart", "two thinner walls, or not a wall", "the plan at this wall, zoomed in");
                log.Add(new BuildLogEntry
                {
                    Kind = kind, Level = lv.Name, LevelCode = lv.Code, At = Mid(w.Centerline), Line = Line(w.Centerline), Drawing = file,
                    Layer = string.Join(" + ", new[] { w.Centerline.Layer, w.FaceA, w.FaceB }.Where(s => !string.IsNullOrEmpty(s) && s != "bridge" && s != "join").Distinct()),
                    Confidence = w.Confidence.ToString(), Why = why, Evidence = $"{w.TypeName}, {w.Length / 1000:0.0} m", Alternative = alt, Settle = settle,
                    Override = "walls / notWalls", Severity = Math.Round(w.Length / 1000, 1), SeverityUnit = "m of wall",
                });
            }
            if (lv.Estimate is { Total: > 0 } est && est.Supporting < est.Total * 0.7)
            {
                double share = (double)est.Supporting / est.Total;
                log.Add(new BuildLogEntry
                {
                    Kind = "level", Level = lv.Name, LevelCode = lv.Code, Drawing = file, At = Array.Empty<double>(),
                    Confidence = share >= 0.5 ? "Medium" : "Low",
                    Why = $"only {est.Supporting} of {est.Total} spot heights agree on {est.FloorLevelMm / 1000:0.000} m",
                    Evidence = "the plan's spot heights", Alternative = string.Join(", ", est.OtherLevels.Take(3).Select(o => $"{o.LevelMm / 1000:0.000} m ({o.Count})")),
                    Settle = "the elevations' and sections' level marks for this floor", Override = "levels",
                    Severity = Math.Round((lv.Outline?.Area ?? 0) / 1e6), SeverityUnit = "m² of floor",
                });
            }
            foreach (var u in lv.Unclear)
                log.Add(new BuildLogEntry
                {
                    Kind = "unclear-area", Level = lv.Name, LevelCode = lv.Code, Drawing = file,
                    At = new[] { Math.Round(u.Outline.Vertices.Average(v => v.X)), Math.Round(u.Outline.Vertices.Average(v => v.Y)) },
                    Confidence = "Low", Why = u.Reason, Evidence = "the plan", Alternative = "", Settle = "the plan here, and photos",
                    Override = "walls", Severity = Math.Round(u.Outline.Area / 1e6, 1), SeverityUnit = "m²",
                });
        }
        foreach (var c in model.Checks)
        {
            var lv = model.Levels.FirstOrDefault(l => l.Code == c.LevelCode);
            log.Add(new BuildLogEntry
            {
                Kind = "check: " + c.Kind, Level = lv?.Name ?? c.LevelCode, LevelCode = c.LevelCode, At = new[] { Math.Round(c.At.X), Math.Round(c.At.Y) },
                Drawing = Path.GetFileName(lv?.Plan?.Path ?? ""), Confidence = "Medium", Why = CheckItem.Describe(c.Kind) + ": " + c.Message,
                Evidence = "the engine's self-check of its reading", Settle = "look at the place in the plan and the model",
                Severity = c.Priority == 1 ? 10 : c.Priority == 2 ? 5 : 2, SeverityUnit = "check",
            });
        }
        foreach (var n in model.Notes.Where(n => n.StartsWith("CHECK", StringComparison.Ordinal)))
        {
            var lv = model.Levels.FirstOrDefault(l => n.StartsWith($"CHECK {l.Name}:", StringComparison.Ordinal));
            log.Add(new BuildLogEntry
            {
                Kind = "note", Level = lv?.Name ?? "", LevelCode = lv?.Code ?? "", Confidence = "Low", Why = n.Substring(n.IndexOf(':') + 1).Trim(),
                Evidence = "the reading's report", Severity = 3, SeverityUnit = "note",
            });
        }
        foreach (var t in model.FacadeTops.Where(t => t.Confidence != "High"))
        {
            double len = Math.Sqrt(Math.Pow(t.Line[2] - t.Line[0], 2) + Math.Pow(t.Line[3] - t.Line[1], 2)) / 1000;
            log.Add(new BuildLogEntry
            {
                Kind = "facade-top", Level = t.Level, At = new[] { Math.Round((t.Line[0] + t.Line[2]) / 2), Math.Round((t.Line[1] + t.Line[3]) / 2) }, Line = t.Line,
                Confidence = t.Confidence, Why = $"{t.Id}: {t.Class}", Evidence = t.Evidence, Alternative = t.Rejected, Settle = t.Settle,
                Override = "facadeTop (with a line)", Severity = Math.Round(len, 1), SeverityUnit = "m of facade",
            });
        }
        foreach (var x in model.SectionIndex)
        {
            if (x.Fit is { Rejected.Count: > 0 } f)
                log.Add(new BuildLogEntry
                {
                    Kind = "section marks", Confidence = "Medium", Why = $"section {x.Name}: {f.Rejected.Count} level marks off the sheet's height fit by more than {SectionProfiles.FitTolMm:0} mm ({string.Join(", ", f.Rejected.Take(4).Select(r => $"{r[0]:0} by {r[1]:0}"))})",
                    Evidence = x.File, Drawing = x.File, Settle = "check the marks on the sheet: a typo, or a part of the sheet drawn at another height", Severity = f.Rejected.Count, SeverityUnit = "marks",
                });
            if (x.Registration is { Sure: false } r)
                log.Add(new BuildLogEntry
                {
                    Kind = "section placement", Confidence = "Low", Why = $"section {x.Name}: not sure where it sits along its cut ({r.Why}); its profiles were not used for the facade tops",
                    Evidence = x.File, Drawing = x.File, Settle = "a facadeTop decision with a line for those facades, or a clearer key plan", Severity = 3, SeverityUnit = "section",
                });
            foreach (var e in x.Ends.Where(e => e.OffsetMm is double o && Math.Abs(o) > 100))
                log.Add(new BuildLogEntry
                {
                    Kind = "section offset", Line = e.FacadeLine, Confidence = "High", Why = $"section {x.Name} end {e.End}: its wall face stands {e.OffsetMm:0} mm off the plan's",
                    Evidence = x.File, Drawing = x.File, Settle = "the profile is measured from the section's own face; anything placed from the section alone needs this offset", Severity = 1, SeverityUnit = "end",
                });
        }
        foreach (var o in model.Decisions.Where(o => o.State is "stale" or "contradicted"))
            log.Add(new BuildLogEntry
            {
                Kind = "decision " + o.State, Confidence = "Medium", Why = $"{o.Kind} {o.What}: {o.Detail}", Evidence = SurveyDecisions.FileName,
                Settle = o.State == "stale" ? "update or remove the entry (the drawing or the floor it names changed)" : "look at the drawing: the decision or the reading is wrong",
                Override = o.Kind, Severity = 5, SeverityUnit = "decision",
            });
        foreach (var s in model.Skipped)
            log.Add(new BuildLogEntry { Kind = "skipped-drawing", Drawing = s.FileName, Confidence = "Medium", Why = s.Reason, Settle = "is anything in it needed? (an elevation or section is read separately)", Severity = 1, SeverityUnit = "drawing" });
        log.Sort();
        return log;
    }

    /// <summary>Entries from what Revit did in the build: elements it refused ("REVIT REFUSED ...") and fallbacks the
    /// builder logged ("... built as ..."), added after the reading's entries.</summary>
    public void AddBuildLines(IEnumerable<string> lines)
    {
        foreach (var l in lines)
        {
            if (l.StartsWith("REVIT REFUSED", StringComparison.Ordinal))
                Add(new BuildLogEntry { Kind = "revit-refused", Confidence = "Low", Why = l, Evidence = "Revit's answer to the build", Settle = "the element's outline in the plan (often a self-crossing or tiny edge)", Severity = 5, SeverityUnit = "element" });
            else if (l.Contains("fallback", StringComparison.OrdinalIgnoreCase) || l.Contains("instead", StringComparison.OrdinalIgnoreCase) && l.Contains("roof", StringComparison.OrdinalIgnoreCase))
                Add(new BuildLogEntry { Kind = "fallback", Confidence = "Low", Why = l, Evidence = "the build", Settle = "rebuild that element by hand or with the MCP side", Severity = 5, SeverityUnit = "element" });
        }
        Sort();
    }

    /// <summary>Writes build-log.json to the reports folder (in the project's own folder there) and to the survey folder's
    /// _prep folder, where the MCP side reads it. Returns the paths written; a folder that can't be written is skipped.</summary>
    public List<string> Save(string reportsRoot, string projectTitle, string? surveyFolder)
    {
        var text = ToJson(); var written = new List<string>();
        string safe = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(projectTitle ?? ""), @"[^\w\- ]", "_");
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(reportsRoot)) dirs.Add(string.IsNullOrEmpty(safe) ? reportsRoot : Path.Combine(reportsRoot, safe));
        if (!string.IsNullOrEmpty(surveyFolder)) dirs.Add(Path.Combine(surveyFolder, "_prep"));
        foreach (var d in dirs)
            try { Directory.CreateDirectory(d); var p = Path.Combine(d, "build-log.json"); File.WriteAllText(p, text); written.Add(p); }
            catch { /* the log is a help, never a reason to lose the build */ }
        return written;
    }

    /// <summary>The survey folder: where the plans were read from.</summary>
    public static string? SurveyFolder(SurveyModel model) =>
        model.Levels.Select(l => l.Plan?.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p)) is string p ? Path.GetDirectoryName(p) : null;
}
