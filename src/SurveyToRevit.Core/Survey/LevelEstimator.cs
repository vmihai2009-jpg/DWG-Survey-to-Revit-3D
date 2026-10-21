using System.Globalization;
using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Survey;

/// <summary>The floor level of one plan, worked out from its spot heights.</summary>
public sealed record LevelEstimate(double FloorLevelMm, int Supporting, int Total, IReadOnlyList<(double LevelMm, int Count)> OtherLevels);

/// <summary>
/// Surveyed floor plans carry many spot heights (e.g. "27.05" m above datum).
/// The floor level is the height most of them agree on, within a tolerance;
/// other clusters (steps, yards, split levels) are reported, not hidden.
/// </summary>
public static class LevelEstimator
{
    static readonly Regex Number = new(@"^(?:FFL|FL|SSL)?\s*([+-]?\d{1,4}\.\d{2,3})\s*m?$", RegexOptions.IgnoreCase);

    /// <summary>Parses a spot-height text into millimetres. Survey levels are given in metres.</summary>
    public static double? ParseMm(string text)
    {
        var m = Number.Match(text.Trim());
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1000 : null;
    }

    public static LevelEstimate? Estimate(IEnumerable<double> heightsMm, double clusterTolMm = 30, double roundToMm = 10)
    {
        var values = heightsMm.OrderBy(v => v).ToList();
        if (values.Count == 0) return null;

        var clusters = new List<List<double>>();
        foreach (var v in values)
        {
            if (clusters.Count > 0 && v - clusters[^1][0] <= clusterTolMm) clusters[^1].Add(v);
            else clusters.Add(new List<double> { v });
        }
        var ranked = clusters.OrderByDescending(c => c.Count).ThenBy(c => c.Max() - c.Min()).ToList();
        double Level(List<double> c) => Math.Round(Median(c) / roundToMm) * roundToMm;
        var main = ranked[0];
        var others = ranked.Skip(1).Where(c => c.Count >= 2).Take(4).Select(c => (Level(c), c.Count)).ToList();
        return new LevelEstimate(Level(main), main.Count, values.Count, others);
    }

    static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
