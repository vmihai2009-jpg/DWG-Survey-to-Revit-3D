using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Core.Learning;

/// <summary>
/// Office-wide learning from what people do to the models Survey to Revit builds (docs/LEARNING.md).
///
///   1. Capture: every build leaves a snapshot of the walls it made; the next build of that project compares the model
///      with it, and each wall becomes an example (kept, deleted, moved, retyped) in the shared learning folder, with the
///      reading's evidence (layer, faces, thickness, confidence) and the surveyor.
///   2. Surveyor profiles: layers whose walls people delete on project after project of one surveyor become a proposed
///      layer rule for that surveyor (layers.txt format).
///   3. Tuning: the reading's tolerances can be set office-wide (tuning.json); SurveyLab --sweep tries values.
///   4. Ranking: low-confidence walls of a kind people nearly always delete (same surveyor, layer, thickness) are left out.
///   5. Gate: profiles, model and tuning are written as *.proposed; only a person adopts them (SurveyLab --adopt, after
///      --bench shows no survey getting worse). The engine only ever reads the adopted files.
///
/// Nothing here may stop a build: every read and write is guarded.
/// </summary>
public static class LearningFolder
{
    /// <summary>The shared folder (office-wide); null to switch learning off. Set by the add-in from its settings.</summary>
    public static string? Path { get; set; }

    public static string? Sub(string name)
    {
        if (string.IsNullOrWhiteSpace(Path)) return null;
        try { var d = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(d); return d; } catch { return null; }
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Who drew the survey: the firm named on the sheets, else the originator field of ISO 19650 file names, else unknown.</summary>
public static class SurveyorId
{
    static readonly Regex Firm = new(@"\b([A-Z][A-Za-z&]+(?:\s+[A-Z][A-Za-z&]+){0,2})\s+(Surveys?|Surveying|Surveyors|Measured\s+Surveys?|Ltd|Limited|LLP)\b");
    static readonly Regex Iso = new(@"^[A-Za-z0-9]{2,8}-([A-Za-z]{2,6})-[A-Za-z0-9]{1,4}-");

    public static string From(IEnumerable<string> paths, IEnumerable<string> texts)
    {
        var firms = texts.SelectMany(t => Firm.Matches(t ?? "").Select(m => Clean(m.Value))).Where(s => s.Length >= 6)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (firms != null && firms.Count() >= 2) return firms.Key;
        var iso = paths.Select(p => Iso.Match(System.IO.Path.GetFileName(p))).Where(m => m.Success).Select(m => m.Groups[1].Value.ToUpperInvariant())
            .GroupBy(s => s).OrderByDescending(g => g.Count()).FirstOrDefault();
        return iso?.Key ?? "unknown";
    }

    static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>A file-name-safe form.</summary>
    public static string Safe(string id) => Regex.Replace(id, @"[^\w\-]+", "_").Trim('_') is { Length: > 0 } s ? s : "unknown";
}

public enum WallOutcome { Kept, Deleted, Moved, Retyped }

/// <summary>One built wall and what people did with it: the label the learning uses.</summary>
public sealed record WallExample
{
    public DateTime Time { get; init; }
    public string Project { get; init; } = "";
    public string Surveyor { get; init; } = "unknown";
    public string Level { get; init; } = "";
    public string Layer { get; init; } = "";
    public string? FaceA { get; init; }
    public string? FaceB { get; init; }
    public string Confidence { get; init; } = "";
    public double ThicknessMm { get; init; }
    public double LengthMm { get; init; }
    public WallOutcome Outcome { get; init; }
    public double? MovedMm { get; init; }
    public double? NewThicknessMm { get; init; }

    /// <summary>The kind the ranking groups by: surveyor, layer, confidence and thickness to 50 mm.</summary>
    public string Key => WallModel.KeyOf(Surveyor, Layer, Confidence, ThicknessMm);
}

/// <summary>The shared examples: one JSON line per wall, a file per surveyor.</summary>
public static class LearningStore
{
    public static int Append(IEnumerable<WallExample> examples)
    {
        var dir = LearningFolder.Sub("examples");
        if (dir == null) return 0;
        int n = 0;
        try
        {
            foreach (var g in examples.GroupBy(e => SurveyorId.Safe(e.Surveyor)))
            {
                var lines = g.Select(e => JsonSerializer.Serialize(e, LearningFolder.Json)).ToList();
                File.AppendAllLines(System.IO.Path.Combine(dir, g.Key + ".jsonl"), lines);
                n += lines.Count;
            }
        }
        catch { /* learning never stops a build */ }
        return n;
    }

    public static List<WallExample> ReadAll()
    {
        var list = new List<WallExample>();
        var dir = LearningFolder.Sub("examples");
        if (dir == null) return list;
        foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
            foreach (var line in File.ReadLines(f))
                try { if (JsonSerializer.Deserialize<WallExample>(line, LearningFolder.Json) is { } e) list.Add(e); } catch { }
        return list;
    }
}

/// <summary>
/// Point 4: for each kind of low-confidence wall (surveyor, layer, confidence, thickness), how often people deleted it.
/// A kind deleted at least <see cref="DropRate"/> of the time over <see cref="MinExamples"/> walls from at least
/// <see cref="MinProjects"/> projects is left out of later readings.
/// </summary>
public sealed class WallModel
{
    public const double DropRate = 0.8;
    public const int MinExamples = 8, MinProjects = 2;

    public int Version { get; set; } = 1;
    public DateTime Built { get; set; }
    public Dictionary<string, KindStats> Kinds { get; set; } = new();

    public sealed class KindStats
    {
        public int Seen { get; set; }
        public int Deleted { get; set; }
        public int Projects { get; set; }
        public bool Drop { get; set; }
    }

    public static string KeyOf(string surveyor, string layer, string confidence, double thicknessMm) =>
        $"{SurveyorId.Safe(surveyor)}|{layer.ToUpperInvariant()}|{confidence}|{Math.Round(thicknessMm / 50) * 50:0}";

    public static WallModel Train(IEnumerable<WallExample> examples)
    {
        var m = new WallModel { Built = DateTime.Now };
        foreach (var g in examples.Where(e => e.Confidence == "Low").GroupBy(e => e.Key))
        {
            int seen = g.Count(), del = g.Count(e => e.Outcome == WallOutcome.Deleted), proj = g.Select(e => e.Project).Distinct().Count();
            m.Kinds[g.Key] = new KindStats { Seen = seen, Deleted = del, Projects = proj, Drop = seen >= MinExamples && proj >= MinProjects && del >= DropRate * seen };
        }
        return m;
    }

    // (an unknown surveyor's examples mix many offices' drawing habits: never used to leave walls out)
    public bool ShouldDrop(string surveyor, WallCandidate w) =>
        surveyor != "unknown" && w.Confidence == Confidence.Low && Kinds.TryGetValue(KeyOf(surveyor, w.Centerline.Layer, "Low", w.TypeThickness), out var k) && k.Drop;
}

/// <summary>Point 2: a surveyor's layers whose walls are nearly always deleted, as proposed layers.txt lines.</summary>
public static class SurveyorProfiles
{
    public static Dictionary<string, List<string>> Propose(IEnumerable<WallExample> examples)
    {
        var res = new Dictionary<string, List<string>>();
        foreach (var bySurveyor in examples.Where(e => e.Surveyor != "unknown").GroupBy(e => SurveyorId.Safe(e.Surveyor)))
        {
            var lines = new List<string>();
            foreach (var layer in bySurveyor.GroupBy(e => e.Layer, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(layer.Key) || layer.Key.Contains(' ') && layer.Key.Contains('+')) continue;
                int seen = layer.Count(), del = layer.Count(e => e.Outcome == WallOutcome.Deleted), proj = layer.Select(e => e.Project).Distinct().Count();
                if (seen >= 20 && proj >= WallModel.MinProjects && del >= 0.9 * seen)
                    lines.Add($"{layer.Key} = Other    # {del} of {seen} walls deleted on {proj} projects");
            }
            if (lines.Count > 0) res[bySurveyor.Key] = lines;
        }
        return res;
    }
}

/// <summary>Point 5: what is proposed and what is adopted. The engine reads only adopted files.</summary>
public static class Adopted
{
    public const string ModelFile = "walls-model.json", TuningFile = "tuning.json";

    static string? Path(string name, bool proposed) => LearningFolder.Sub(proposed ? "proposed" : "adopted") is string d ? System.IO.Path.Combine(d, name) : null;

    public static string? ProfilePath(string surveyor, bool proposed = false) => Path($"profile-{SurveyorId.Safe(surveyor)}.txt", proposed);

    public static WallModel? Model()
    {
        try { return Path(ModelFile, false) is string p && File.Exists(p) ? JsonSerializer.Deserialize<WallModel>(File.ReadAllText(p), LearningFolder.Json) : null; }
        catch { return null; }
    }

    /// <summary>The office's tolerances: the defaults with the adopted tuning.json on top.</summary>
    public static Tolerances Tolerances()
    {
        try
        {
            if (Path(TuningFile, false) is string p && File.Exists(p)) return Tuning.Apply(Core.Tolerances.Default, File.ReadAllText(p));
        }
        catch { }
        return Core.Tolerances.Default;
    }

    /// <summary>Writes what the examples support now as proposals (never adopted here). Returns a short report.</summary>
    public static List<string> Propose()
    {
        var report = new List<string>();
        var all = LearningStore.ReadAll();
        report.Add($"{all.Count} examples from {all.Select(e => e.Project).Distinct().Count()} projects, {all.Select(e => e.Surveyor).Distinct().Count()} surveyors.");
        var model = WallModel.Train(all);
        if (Path(ModelFile, true) is string mp) File.WriteAllText(mp, JsonSerializer.Serialize(model, new JsonSerializerOptions(LearningFolder.Json) { WriteIndented = true }));
        report.Add($"Wall ranking: {model.Kinds.Count(k => k.Value.Drop)} kinds of low-confidence wall to leave out (of {model.Kinds.Count} seen).");
        foreach (var (surveyor, lines) in SurveyorProfiles.Propose(all))
        {
            if (ProfilePath(surveyor, true) is string pp)
                File.WriteAllLines(pp, new[] { $"# proposed for surveyor {surveyor} from the office's corrections, {DateTime.Now:yyyy-MM-dd}" }.Concat(lines));
            report.Add($"Profile {surveyor}: {lines.Count} layer rules proposed.");
        }
        return report;
    }

    /// <summary>Copies the proposals into adopted (a person runs this after the benchmark shows nothing got worse).</summary>
    public static List<string> Adopt()
    {
        var done = new List<string>();
        if (LearningFolder.Sub("proposed") is not string from || LearningFolder.Sub("adopted") is not string to) return done;
        foreach (var f in Directory.GetFiles(from))
        {
            var dest = System.IO.Path.Combine(to, System.IO.Path.GetFileName(f));
            if (File.Exists(dest)) File.Copy(dest, System.IO.Path.Combine(LearningFolder.Sub("history")!, $"{DateTime.Now:yyyyMMdd_HHmmss}-{System.IO.Path.GetFileName(f)}"), true);
            File.Copy(f, dest, true);
            done.Add(System.IO.Path.GetFileName(f));
        }
        return done;
    }
}

/// <summary>Point 3: the reading's tolerances by name, for an office tuning.json ({"MaxPierMm": 5000}) and SurveyLab --sweep.</summary>
public static class Tuning
{
    public static Tolerances Apply(Tolerances baseTol, string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(baseTol))!.AsObject();
        var over = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        foreach (var (k, v) in over)
        {
            var key = node.Select(p => p.Key).FirstOrDefault(n => string.Equals(n, k, StringComparison.OrdinalIgnoreCase));
            if (key != null && v != null) node[key] = v.DeepClone();
        }
        return JsonSerializer.Deserialize<Tolerances>(node.ToJsonString()) ?? baseTol;
    }

    public static Tolerances With(Tolerances baseTol, string name, double value) => Apply(baseTol, $"{{\"{name}\": {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");

    public static IEnumerable<string> Names() => typeof(Tolerances).GetProperties().Where(p => p.PropertyType == typeof(double) && p.CanWrite).Select(p => p.Name);
}
