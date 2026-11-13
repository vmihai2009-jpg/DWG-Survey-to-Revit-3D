namespace SurveyToRevit.Core.Survey;

/// <summary>
/// Item U of MCP-AUDIT-AND-TOOLS.md, the matching part: when a floor is built again, which of the walls already in the model
/// are the same as walls about to be built (same line within 20 mm, either way round, thickness within 5 mm), and which
/// windows and doors on those walls are the same as ones about to be placed (same host, within 100 mm, width within 50 mm).
/// Those are kept rather than deleted and made again, so work done on them since (types changed, sweeps, families placed
/// against them) survives. Each old element is matched once, the closest first. Revit-free and tested.
/// </summary>
public static class RebuildMatch
{
    public const double LineTolMm = 20, ThicknessTolMm = 5, OpeningTolMm = 100, OpeningWidthTolMm = 50;

    static double EndGap(Segment2 a, Segment2 b) =>
        Math.Min(Math.Max(a.A.DistanceTo(b.A), a.B.DistanceTo(b.B)), Math.Max(a.A.DistanceTo(b.B), a.B.DistanceTo(b.A)));

    /// <returns>new wall index → old wall id</returns>
    public static Dictionary<int, long> Walls(IReadOnlyList<(long Id, Segment2 Line, double ThicknessMm)> old, IReadOnlyList<(int Index, Segment2 Line, double ThicknessMm)> fresh)
    {
        var pairs = from o in old from f in fresh
                    let gap = EndGap(o.Line, f.Line)
                    where gap <= LineTolMm && Math.Abs(o.ThicknessMm - f.ThicknessMm) <= ThicknessTolMm
                    orderby gap select (f.Index, o.Id);
        return Greedy(pairs);
    }

    /// <returns>new opening index → old opening id</returns>
    public static Dictionary<int, long> Openings(IReadOnlyList<(long Id, long Host, Vec2 At, double WidthMm)> old, IReadOnlyList<(int Index, long Host, Vec2 At, double WidthMm)> fresh)
    {
        var pairs = from o in old from f in fresh
                    where o.Host == f.Host
                    let gap = o.At.DistanceTo(f.At)
                    where gap <= OpeningTolMm && Math.Abs(o.WidthMm - f.WidthMm) <= OpeningWidthTolMm
                    orderby gap select (f.Index, o.Id);
        return Greedy(pairs);
    }

    static Dictionary<int, long> Greedy(IEnumerable<(int Index, long Id)> pairs)
    {
        var res = new Dictionary<int, long>(); var used = new HashSet<long>();
        foreach (var (i, id) in pairs)
            if (!res.ContainsKey(i) && used.Add(id)) res[i] = id;
        return res;
    }
}
