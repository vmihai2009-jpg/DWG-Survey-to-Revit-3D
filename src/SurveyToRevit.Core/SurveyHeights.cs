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
///   door head: DH, D, DOOR                            ceiling: U/S, US, CH, CLG, CEILING
/// Absolute levels (CL cover levels, FFL spot heights) are not relative heights and are left out.
/// </summary>
public static class SurveyHeights
{
    static readonly Regex Note = new(@"^\s*(u/s|us|ch|clg|ceiling|sill|cill|sl|fs|sh|s|c|hh|hd|head|ah|h|dh|door|d|oh)\.?\s*[:=]?\s*(\d+(?:\.\d+)?)\s*m?\s*$",
        RegexOptions.IgnoreCase);

    public static List<HeightNote> Read(IEnumerable<(Vec2 Position, string Value)> texts)
    {
        var found = new List<(Vec2 P, string Key, double Mm)>();
        foreach (var (p, v) in texts)
        {
            var m = Note.Match(v ?? "");
            if (!m.Success) continue;
            var number = m.Groups[2].Value;
            double value = double.Parse(number, CultureInfo.InvariantCulture);
            double mm = Math.Round(number.Contains('.') || value < 100 ? value * 1000 : value);
            if (mm <= 0 || mm > 8000) continue; // not a height above this floor
            found.Add((p, m.Groups[1].Value.ToLowerInvariant(), mm));
        }
        // "SH" next to "FS" is the window's height (sill to head), not its sill; likewise "H" on a
        // plan that writes cills as "C" ("c800 h2400": floor to cill, cill to head)
        bool shIsHeight = found.Any(f => f.Key == "fs");
        bool hIsHeight = found.Any(f => f.Key == "c");
        return found.Select(f => new HeightNote(f.P, f.Key switch
        {
            "s" or "sl" or "sill" or "cill" or "fs" or "c" => HeightKind.Sill,
            "h" when hIsHeight => HeightKind.OpeningHeight,
            "sh" => shIsHeight ? HeightKind.OpeningHeight : HeightKind.Sill,
            "oh" => HeightKind.OpeningHeight,
            "h" or "hh" or "hd" or "head" or "ah" => HeightKind.Head,
            "dh" or "d" or "door" => HeightKind.DoorHead,
            _ => HeightKind.Ceiling,
        }, f.Mm)).ToList();
    }

    /// <summary>True for a height note that sits in a window or door recess rather than in a room.</summary>
    public static bool IsOpeningNote(string value) =>
        Read(new[] { (default(Vec2), value) }) is { Count: 1 } r && r[0].Kind != HeightKind.Ceiling
        || Regex.IsMatch(value.Trim(), @"^(S|H|ASH)\s*[\d.]|^(S|H)\s+Not|^No\s+Head", RegexOptions.IgnoreCase);

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
