namespace SurveyToRevit.Core;

/// <summary>
/// Folding or sliding glazed screens drawn with door swings: three or more door leaves side by side along one outside
/// wall, each no more than <c>maxGapMm</c> from the next (a mullion or slim pier between them; a folding pair is often read
/// as one leaf, so the gaps look wider than drawn). Surveyors draw folding panels onto a
/// terrace as a row of swings; they are glazing from the floor, not a row of doors. Each leaf becomes a glazed panel
/// (a window with sill 0 and the door's head).
/// </summary>
public static class GlazedScreens
{
    public sealed record Screen(List<DoorCandidate> Doors, List<WindowCandidate> Panels);

    public static List<Screen> FromDoorRows(IReadOnlyList<DoorCandidate> doors, IReadOnlyList<WallCandidate> walls, Polygon2? outline,
        int minLeaves = 3, double maxGapMm = 900, double minLeafMm = 800, double minHeadMm = 2100)
    {
        var result = new List<Screen>();
        if (outline == null) return result;
        foreach (var g in doors.Where(d => d.HostIndex >= 0 && d.HostIndex < walls.Count).GroupBy(d => d.HostIndex))
        {
            var w = walls[g.Key];
            if (w.Curtain) continue;
            var row = g.Select(d => (d, a: WallGeometry.Along(w, d.Center))).OrderBy(x => x.a).ToList();
            var run = new List<(DoorCandidate d, double a)>();
            void Close()
            {
                int leaves = run.Sum(x => x.d.Double ? 2 : 1);
                // outside wall: the outline says so; where it can't (a terrace inside the floor's outline), full-size
                // leaves only (a row of WC cubicle or cupboard doors is narrower and lower)
                var outward = WindowFinder.Outward(w, run.Average(x => x.a), outline);
                bool fullSize = run.All(x => x.d.WidthMm >= minLeafMm && x.d.HeightMm >= minHeadMm);
                if (leaves >= minLeaves && (outward != null || fullSize))
                {
                    var panels = run.Select(x => new WindowCandidate(x.d.HostIndex, x.d.Center, x.d.WidthMm, 0, x.d.HeightMm, x.d.HeightFromSurvey) { Outward = outward }).ToList();
                    result.Add(new Screen(run.Select(x => x.d).ToList(), panels));
                }
                run.Clear();
            }
            foreach (var x in row)
            {
                if (run.Count > 0)
                {
                    var p = run[^1];
                    double gap = (x.a - x.d.WidthMm / 2) - (p.a + p.d.WidthMm / 2);
                    if (gap > maxGapMm) Close();
                }
                run.Add(x);
            }
            Close();
        }
        return result;
    }
}
