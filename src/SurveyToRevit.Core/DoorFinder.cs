namespace SurveyToRevit.Core;

/// <summary>A door to place: on wall <see cref="HostIndex"/>, centred at <see cref="Center"/>, in mm.</summary>
public sealed record DoorCandidate(int HostIndex, Vec2 Center, double WidthMm, double HeightMm, bool Double);

/// <summary>
/// Doors from the door layers. Surveys draw a door as its leaf shown open: a line from
/// the hinge, at the wall, swung into the room, as long as the leaf is wide. So a door
/// line of leaf length with one end at a wall and running away from it is a leaf; the
/// opening runs along the wall from the hinge. Two leaves meeting in one opening are a
/// double door.
/// </summary>
public static class DoorFinder
{
    public const double DefaultHeightMm = 2100;

    public static List<DoorCandidate> Find(IReadOnlyList<Segment2> doorLines, IReadOnlyList<WallCandidate> walls,
        IEnumerable<WindowCandidate>? windows = null, double minLeaf = 500, double maxLeaf = 1300)
    {
        var leaves = new List<(int Host, double Hinge, int Side, double Width)>();
        foreach (var s in doorLines.Where(l => l.Length >= minLeaf && l.Length <= maxLeaf))
        {
            // hinge end: on a wall; the leaf runs away from that wall at an angle
            (int host, double hinge, Vec2 free, double off) best = (-1, 0, default, double.MaxValue);
            foreach (var (end, other) in new[] { (s.A, s.B), (s.B, s.A) })
                for (int k = 0; k < walls.Count; k++)
                {
                    var c = walls[k].Centerline;
                    double off = Math.Abs(SegmentCleaner.Offset(c, end)), along = SegmentCleaner.Along(c, end);
                    if (off > walls[k].TypeThickness / 2 + 150 || along < -50 || along > c.Length + 50) continue;
                    if (SegmentCleaner.AngleDiffDeg(c.AngleDeg, s.AngleDeg) < 20) continue; // lies along the wall: a frame line
                    if (off < best.off) best = (k, along, other, off);
                }
            if (best.host < 0) continue;
            var axis = walls[best.host].Centerline;
            double toFree = SegmentCleaner.Along(axis, best.free) - best.hinge;
            // the leaf swings away from the opening's far jamb; a leaf drawn square to the wall is ambiguous: take the side with more wall
            int side = Math.Abs(toFree) > 50 ? Math.Sign(toFree) : (best.hinge < axis.Length / 2 ? 1 : -1);
            leaves.Add((best.host, best.hinge, side, Math.Round(s.Length / 10) * 10));
        }

        // pair leaves of double doors: same wall, hinges a pair of leaf widths apart, opening towards each other
        var used = new bool[leaves.Count];
        var doors = new List<DoorCandidate>();
        for (int i = 0; i < leaves.Count; i++)
        {
            if (used[i]) continue;
            var a = leaves[i];
            var axis = walls[a.Host].Centerline;
            int partner = -1;
            for (int j = i + 1; j < leaves.Count && partner < 0; j++)
            {
                var b = leaves[j];
                if (used[j] || b.Host != a.Host || b.Side == a.Side) continue;
                double gap = Math.Abs(b.Hinge - a.Hinge);
                if (Math.Abs(gap - (a.Width + b.Width)) <= 150 && Math.Sign(b.Hinge - a.Hinge) == a.Side) partner = j;
            }
            used[i] = true;
            if (partner >= 0)
            {
                used[partner] = true;
                var b = leaves[partner];
                double mid = (a.Hinge + b.Hinge) / 2, width = Math.Round(Math.Abs(b.Hinge - a.Hinge) / 10) * 10;
                doors.Add(new DoorCandidate(a.Host, axis.A + axis.Direction * mid, width, DefaultHeightMm, true));
            }
            else
            {
                double mid = Math.Clamp(a.Hinge + a.Side * a.Width / 2, a.Width / 2, Math.Max(a.Width / 2, axis.Length - a.Width / 2));
                doors.Add(new DoorCandidate(a.Host, axis.A + axis.Direction * mid, a.Width, DefaultHeightMm, false));
            }
        }

        // one door per opening, and none on top of a window
        var wins = windows?.ToList() ?? new List<WindowCandidate>();
        var kept = new List<DoorCandidate>();
        foreach (var d in doors.OrderByDescending(d => d.WidthMm))
        {
            bool clash = kept.Any(k => k.HostIndex == d.HostIndex && k.Center.DistanceTo(d.Center) < (k.WidthMm + d.WidthMm) / 2)
                      || wins.Any(w => w.HostIndex == d.HostIndex && w.Center.DistanceTo(d.Center) < (w.WidthMm + d.WidthMm) / 2);
            if (!clash) kept.Add(d);
        }
        return kept;
    }
}
