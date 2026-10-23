using System.Text;

namespace SurveyToRevit.Core;

/// <summary>A wall reduced to what the score compares: its level, centreline (mm, plan) and thickness.</summary>
public readonly record struct ScoreWall(string Level, Segment2 Line, double ThicknessMm);

/// <summary>A window or door: its level, position (mm, plan) and width.</summary>
public readonly record struct ScoreOpening(string Level, Vec2 At, double WidthMm);

/// <summary>Counts for one kind of element: right, off (found but not quite right), missed, invented.</summary>
public sealed class ScoreTally
{
    public int Right, Off, Missed, Invented;
    /// <summary>Share of the generated elements that are in the reference model (right or off).</summary>
    public double Precision => Right + Off + Invented == 0 ? double.NaN : (double)(Right + Off) / (Right + Off + Invented);
    /// <summary>Share of the reference model's elements that were found (right or off).</summary>
    public double Recall => Right + Off + Missed == 0 ? double.NaN : (double)(Right + Off) / (Right + Off + Missed);
    public void Add(ScoreTally t) { Right += t.Right; Off += t.Off; Missed += t.Missed; Invented += t.Invented; }
}

/// <summary>
/// Element-by-element comparison of the generated model with a reference model of the same building
/// (one modelled by hand). A reference wall is right when a generated wall runs along it (parallel within
/// 5°, centrelines within 150 mm, covering half its length or more) at the right thickness (±30 mm); off when
/// it is found but at the wrong thickness, or only roughly (within 400 mm, or a fifth of its length); missed
/// otherwise. A generated wall with nothing in the reference along it is invented. Windows and doors are
/// right within 300 mm at the right width (±100 mm), off within 800 mm or at another width. Walls are
/// counted per wall; the metres are given too.
/// </summary>
public static class ScoreCard
{
    public const double AngleDeg = 5, OffsetMm = 150, RoughOffsetMm = 400, Cover = 0.5, RoughCover = 0.2, ThicknessMm = 30;
    public const double OpeningMm = 300, RoughOpeningMm = 800, WidthMm = 100;

    public sealed class Result
    {
        public Dictionary<string, (ScoreTally Walls, ScoreTally Windows, ScoreTally Doors)> Levels { get; } = new();
        public double RefWallM, FoundWallM, InventedWallM;
        public List<string> Notes { get; } = new();

        public (ScoreTally Walls, ScoreTally Windows, ScoreTally Doors) Total()
        {
            var t = (new ScoreTally(), new ScoreTally(), new ScoreTally());
            foreach (var l in Levels.Values) { t.Item1.Add(l.Walls); t.Item2.Add(l.Windows); t.Item3.Add(l.Doors); }
            return t;
        }

        public string Text()
        {
            var sb = new StringBuilder();
            static string Pct(double v) => double.IsNaN(v) ? "-" : $"{v * 100:0}%";
            void Row(string name, ScoreTally t) =>
                sb.AppendLine($"  {name,-10} right {t.Right,4}  off {t.Off,4}  missed {t.Missed,4}  invented {t.Invented,4}   found {Pct(t.Recall),4} of the reference, {Pct(t.Precision),4} of ours real");
            var (w, wi, d) = Total();
            sb.AppendLine("WHOLE BUILDING");
            Row("Walls", w); Row("Windows", wi); Row("Doors", d);
            sb.AppendLine($"  Wall length: {FoundWallM:0} of {RefWallM:0} m of the reference found; {InventedWallM:0} m generated with nothing in the reference.");
            foreach (var (level, (lw, lwi, ld)) in Levels)
            {
                sb.AppendLine().AppendLine(level.ToUpperInvariant());
                Row("Walls", lw); Row("Windows", lwi); Row("Doors", ld);
            }
            if (Notes.Count > 0) { sb.AppendLine().AppendLine("NOTES"); foreach (var n in Notes) sb.AppendLine("  - " + n); }
            return sb.ToString();
        }
    }

    public static Result Score(IReadOnlyList<ScoreWall> ours, IReadOnlyList<ScoreWall> reference,
        IReadOnlyList<ScoreOpening> ourWindows, IReadOnlyList<ScoreOpening> refWindows,
        IReadOnlyList<ScoreOpening> ourDoors, IReadOnlyList<ScoreOpening> refDoors)
    {
        var result = new Result();
        var levels = ours.Select(w => w.Level).Concat(reference.Select(w => w.Level))
            .Concat(ourWindows.Select(o => o.Level)).Concat(refWindows.Select(o => o.Level))
            .Concat(ourDoors.Select(o => o.Level)).Concat(refDoors.Select(o => o.Level)).Distinct().ToList();
        foreach (var level in levels)
        {
            var o = ours.Where(w => w.Level == level && w.Line.Length > 1).ToList();
            var r = reference.Where(w => w.Level == level && w.Line.Length > 1).ToList();
            var walls = new ScoreTally();
            foreach (var rw in r)
            {
                double cover = Covered(rw, o, OffsetMm, out double thick);
                result.RefWallM += rw.Line.Length / 1000;
                if (cover >= Cover)
                {
                    if (Math.Abs(thick - rw.ThicknessMm) <= ThicknessMm) walls.Right++; else walls.Off++;
                    result.FoundWallM += rw.Line.Length * cover / 1000;
                }
                else if (Covered(rw, o, RoughOffsetMm, out _) >= RoughCover) { walls.Off++; result.FoundWallM += rw.Line.Length * Covered(rw, o, RoughOffsetMm, out _) / 1000; }
                else walls.Missed++;
            }
            foreach (var ow in o)
            {
                double cover = Covered(ow, r, RoughOffsetMm, out _);
                if (cover < RoughCover) { walls.Invented++; result.InventedWallM += ow.Line.Length / 1000; }
            }
            result.Levels[level] = (walls,
                Openings(ourWindows.Where(x => x.Level == level).ToList(), refWindows.Where(x => x.Level == level).ToList()),
                Openings(ourDoors.Where(x => x.Level == level).ToList(), refDoors.Where(x => x.Level == level).ToList()));
        }
        return result;
    }

    /// <summary>Share of <paramref name="w"/>'s length that walls in <paramref name="others"/> run along, and the thickness of the one covering most.</summary>
    static double Covered(ScoreWall w, IReadOnlyList<ScoreWall> others, double offsetMm, out double thickness)
    {
        var c = w.Line; var dir = c.Direction;
        var spans = new List<(double, double)>();
        thickness = double.NaN; double best = 0;
        foreach (var g in others)
        {
            if (SegmentCleaner.AngleDiffDeg(g.Line.AngleDeg, c.AngleDeg) > AngleDeg) continue;
            if (Math.Abs(SegmentCleaner.Offset(c, g.Line.Mid)) > offsetMm) continue;
            double a = (g.Line.A - c.A).Dot(dir), b = (g.Line.B - c.A).Dot(dir);
            double lo = Math.Max(0, Math.Min(a, b)), hi = Math.Min(c.Length, Math.Max(a, b));
            if (hi <= lo) continue;
            spans.Add((lo, hi));
            if (hi - lo > best) { best = hi - lo; thickness = g.ThicknessMm; }
        }
        if (spans.Count == 0) return 0;
        double covered = 0, end = double.MinValue;
        foreach (var (lo, hi) in spans.OrderBy(s => s.Item1))
        {
            if (hi <= end) continue;
            covered += hi - Math.Max(lo, end);
            end = hi;
        }
        return Math.Min(1, covered / c.Length);
    }

    /// <summary>Pairs each reference opening with the nearest unused generated one, nearest pairs first.</summary>
    static ScoreTally Openings(IReadOnlyList<ScoreOpening> ours, IReadOnlyList<ScoreOpening> reference)
    {
        var t = new ScoreTally();
        var pairs = new List<(double D, int R, int O)>();
        for (int i = 0; i < reference.Count; i++)
            for (int j = 0; j < ours.Count; j++)
            {
                double d = (reference[i].At - ours[j].At).Length;
                if (d <= RoughOpeningMm) pairs.Add((d, i, j));
            }
        var usedR = new HashSet<int>(); var usedO = new HashSet<int>();
        foreach (var (d, i, j) in pairs.OrderBy(p => p.D))
        {
            if (usedR.Contains(i) || usedO.Contains(j)) continue;
            usedR.Add(i); usedO.Add(j);
            bool width = double.IsNaN(reference[i].WidthMm) || double.IsNaN(ours[j].WidthMm) || Math.Abs(reference[i].WidthMm - ours[j].WidthMm) <= WidthMm;
            if (d <= OpeningMm && width) t.Right++; else t.Off++;
        }
        t.Missed = reference.Count - usedR.Count;
        t.Invented = ours.Count - usedO.Count;
        return t;
    }
}
