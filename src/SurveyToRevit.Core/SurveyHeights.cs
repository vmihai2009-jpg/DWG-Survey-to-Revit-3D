using System.Globalization;
using System.Text.RegularExpressions;

namespace SurveyToRevit.Core;

public enum HeightKind { Sill, Head, OpeningHeight, DoorHead, Ceiling }

/// <summary>A height written on a plan, above the floor it is drawn on, in mm.</summary>
public sealed record HeightNote(Vec2 Position, HeightKind Kind, double Mm);

/// <summary>
/// Heights surveyors write on plans next to openings and in rooms, read the same way for any
/// survey. Each is a short abbreviation then a height above the floor, in metres ("S 0.94",
/// "u/s 2.52") or, without a decimal point, millimetres ("S 940"):
///   sill: S, SL, SILL, CILL, C, FS (floor to sill)    head: H, HH, HD, HEAD, AH (arch head)
///   sill to head: OH, and SH when the same plan also has FS (a floor-to-sill and window-height
///   pair, as on older London surveys); otherwise SH is a sill height. Likewise H when the plan
///   writes cills as C ("c800 h2400")
///   door head: DH, D, DOOR, DOOR HEAD                  ceiling: U/S, US, CH, CLG, CEILING
/// Other surveyors' ways of writing the same are read too: the word spelt out with HT or HEIGHT
/// ("SILL HT 0.940", "Head height: 2.10"), the number first ("0.94 SILL", "2.52 CEILING"),
/// decimal commas ("S 0,94"), an explicit unit ("S 940mm", "H 2.1m") and two heights in one
/// text ("S 0.94 H 2.10", "S0.94/H2.10", "SH 0.90, HH 2.10").
/// An office's own abbreviations come from layers.txt ("label WH = OpeningHeight"; see
/// <see cref="Survey.LayerRules.LabelWords"/>): they are tried before the built-in ones.
/// Absolute levels (CL cover levels, FFL spot heights) are not relative heights and are left out.
/// </summary>
public static class SurveyHeights
{
    /// <summary>The built-in abbreviations, longest first so "sill" wins over "s".</summary>
    static readonly string[] BuiltIn = { "u/s", "us", "ch", "clg", "ceiling", "sill", "cill", "sl", "fs", "sh", "s", "c", "hh", "hd", "head", "ah", "h", "dh", "door", "door head", "d", "oh" };
    /// <summary>Spelt-out words whose number may come first ("0.94 SILL"); single letters may not, they would catch too much.</summary>
    static readonly HashSet<string> MayFollow = new(StringComparer.OrdinalIgnoreCase) { "sill", "cill", "head", "ceiling", "clg", "door head", "u/s" };
    const string Num = @"\+?(\d+(?:[.,]\d+)?)\s*(mm|m)?";
    const string Ht = @"(?:\s*(?:ht|hgt|height))?";

    static readonly Dictionary<string, (Regex Pair, Regex After, Regex Whole)> Cache = new();

    static string Words(IReadOnlyDictionary<string, HeightKind>? custom) =>
        string.Join("|", BuiltIn.Concat(custom?.Keys ?? Enumerable.Empty<string>())
            .Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0).Distinct()
            .OrderByDescending(w => w.Length).Select(w => Regex.Escape(w).Replace("\\ ", @"\s+")));

    static (Regex Pair, Regex After, Regex Whole) Patterns(IReadOnlyDictionary<string, HeightKind>? custom)
    {
        string key = custom == null || custom.Count == 0 ? "" : string.Join("\n", custom.Keys.OrderBy(k => k));
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
            string words = Words(custom);
            // one note: abbreviation (optionally "HT"/"HEIGHT"), an optional ':' or '=', the height
            string pair = $@"(?<w>{words})\.?{Ht}\.?\s*[:=]?\s*{Num}";
            var made = (new Regex(pair, RegexOptions.IgnoreCase),
                        new Regex($@"^\s*{Num}\s+(?<w>{words}){Ht}\.?\s*$", RegexOptions.IgnoreCase),
                        // the whole text is one or more notes, separated by spaces, '/', ',', ';' or '&'
                        new Regex($@"^\s*{pair}(?:\s*[/,;&]?\s*{pair})*\s*$", RegexOptions.IgnoreCase));
            if (Cache.Count > 32) Cache.Clear();
            return Cache[key] = made;
        }
    }

    /// <summary>The notes written in one text: (abbreviation, mm), or none when the text is not a height note.</summary>
    static IEnumerable<(string Key, double Mm)> Notes(string value, IReadOnlyDictionary<string, HeightKind>? custom)
    {
        var (pair, after, whole) = Patterns(custom);
        var text = value ?? "";
        IEnumerable<Match> found;
        if (whole.IsMatch(text)) found = pair.Matches(text);
        else if (after.Match(text) is { Success: true } m && (MayFollow.Contains(Norm(m.Groups["w"].Value)) || custom?.ContainsKey(Norm(m.Groups["w"].Value)) == true)) found = new[] { m };
        else yield break;
        foreach (var m in found)
        {
            var number = m.Groups[1].Value.Replace(',', '.');
            double v = double.Parse(number, CultureInfo.InvariantCulture);
            string unit = m.Groups[2].Value.ToLowerInvariant();
            double mm = Math.Round(unit == "mm" ? v : unit == "m" || number.Contains('.') || v < 100 ? v * 1000 : v);
            if (mm <= 0 || mm > 8000) continue; // not a height above this floor
            yield return (Norm(m.Groups["w"].Value), mm);
        }
    }

    static string Norm(string w) => Regex.Replace(w.Trim().ToLowerInvariant(), @"\s+", " ");

    public static List<HeightNote> Read(IEnumerable<(Vec2 Position, string Value)> texts, IReadOnlyDictionary<string, HeightKind>? custom = null)
    {
        var found = new List<(Vec2 P, string Key, double Mm)>();
        foreach (var (p, v) in texts)
            foreach (var (key, mm) in Notes(v, custom))
                found.Add((p, key, mm));
        // "SH" next to "FS" is the window's height (sill to head), not its sill; likewise "H" on a
        // plan that writes cills as "C" ("c800 h2400": floor to cill, cill to head)
        bool shIsHeight = found.Any(f => f.Key == "fs");
        bool hIsHeight = found.Any(f => f.Key == "c");
        return found.Select(f => new HeightNote(f.P, custom != null && custom.TryGetValue(f.Key, out var own) ? own : f.Key switch
        {
            "s" or "sl" or "sill" or "cill" or "fs" or "c" => HeightKind.Sill,
            "h" when hIsHeight => HeightKind.OpeningHeight,
            "sh" => shIsHeight ? HeightKind.OpeningHeight : HeightKind.Sill,
            "oh" => HeightKind.OpeningHeight,
            "h" or "hh" or "hd" or "head" or "ah" => HeightKind.Head,
            "dh" or "d" or "door" or "door head" => HeightKind.DoorHead,
            _ => HeightKind.Ceiling,
        }, f.Mm)).ToList();
    }

    /// <summary>True for a height note that sits in a window or door recess rather than in a room.</summary>
    public static bool IsOpeningNote(string value, IReadOnlyDictionary<string, HeightKind>? custom = null) =>
        Read(new[] { (default(Vec2), value) }, custom) is { Count: > 0 } r && r.All(n => n.Kind != HeightKind.Ceiling)
        || Regex.IsMatch(value.Trim(), @"^(S|H|ASH)\s*[\d.]|^(S|H)\s+Not|^No\s+Head", RegexOptions.IgnoreCase);

    /// <summary>Absolute levels and tags that look like notes but are not heights above the floor.</summary>
    static readonly Regex NotANote = new(@"^(cl|il|ffl|fl|ssl|sfl|afl|tow|tos|bos|aod|rl|el|lvl|level|ridge|eaves|gl|egl|tol|tor|sv|svp|rwp|mh|ic|w|dr|r|g|b|l|f|gf|ff|lg|ug|no|nr|ref|rm|room|flat|unit|ws|wc|st)$", RegexOptions.IgnoreCase);
    static readonly Regex LooksLikeNote = new(@"^\s*([A-Za-z][A-Za-z/]{0,5})\.?\s*[:=]?\s*\+?(\d+[.,]\d{1,3}|\d{3,4})\s*(?:mm|m)?\s*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Texts written like a height note (a short abbreviation and a height) that are not read as one: another
    /// surveyor's abbreviations. Grouped by abbreviation, most used first, with an example of each. Absolute
    /// levels (FFL, CL...) and tags (W12, D3) are left out.
    /// </summary>
    public static List<(string Word, int Count, string Example)> Unread(IEnumerable<string> texts, IReadOnlyDictionary<string, HeightKind>? custom = null)
    {
        var miss = new List<(string Word, string Text)>();
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t) || Notes(t, custom).Any()) continue;
            var m = LooksLikeNote.Match(t);
            if (!m.Success || NotANote.IsMatch(m.Groups[1].Value)) continue;
            miss.Add((m.Groups[1].Value.ToUpperInvariant(), t.Trim()));
        }
        return miss.GroupBy(x => x.Word).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count(), g.First().Text)).ToList();
    }

    /// <summary>The nearest note of a kind within reach of a point.</summary>
    public static double? Nearest(IEnumerable<HeightNote> notes, HeightKind kind, Vec2 p, double reach) =>
        notes.Where(n => n.Kind == kind && n.Position.DistanceTo(p) <= reach).OrderBy(n => n.Position.DistanceTo(p)).Select(n => (double?)n.Mm).FirstOrDefault();

    /// <summary>
    /// The floor's ceiling height: the middle (median) surveyed ceiling height, when the plan gives
    /// any that fit under the floor above; otherwise null and the default stands.
    /// </summary>
    public static double? CeilingHeight(IEnumerable<HeightNote> notes, double storeyMm)
    {
        var values = notes.Where(n => n.Kind == HeightKind.Ceiling && n.Mm >= 1800 && n.Mm <= storeyMm - 50).Select(n => n.Mm).ToList();
        if (values.Count == 0) return null;
        // the median: low beam and bulkhead undersides on one side, high voids on the other
        values.Sort();
        double median = values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
        return Math.Round(median / 10) * 10;
    }
}
