namespace SurveyToRevit.Core.Survey;

/// <summary>
/// A window or door must open into clear space: no other wall may stand in it. Where another wall's end
/// runs into the edge of an opening (a partition meeting the façade at a window's jamb, a wall end a
/// little past a door), the opening slides along its wall clear of it, or narrows to the clear width
/// when it cannot slide. A door set in a short wall made across a gap that really lies in a drawn wall
/// (the gap read at a slant) moves into that wall. Walls running through the middle of an opening are
/// left for the openings check in Revit. A single-line partition running into an opening stops at the wall's face.
/// </summary>
public static class OpeningClearance
{
    public static (int Moved, int Narrowed, int Rehosted, int Trimmed) Fit(SurveyLevel level)
    {
        var walls = level.Walls.Walls;
        int moved = 0, narrowed = 0, rehosted = 0, trims = 0;

        // doors in a made "door opening" wall that lies inside a drawn wall: into the drawn wall
        var doors = level.Doors.ToList();
        var drop = new HashSet<int>();
        for (int k = 0; k < doors.Count; k++)
        {
            var d = doors[k]; var h = walls[d.HostIndex];
            if (h.FaceA != "door opening" || h.Arc != null) continue;
            int best = -1; double bestOff = double.MaxValue;
            for (int j = 0; j < walls.Count; j++)
            {
                var w = walls[j];
                if (j == d.HostIndex || w.Arc != null || w.Curtain || w.FaceA == "door opening") continue;
                if (SegmentCleaner.AngleDiffDeg(w.Centerline.AngleDeg, h.Centerline.AngleDeg) > 25) continue;
                double off = Math.Abs(SegmentCleaner.Offset(w.Centerline, d.Center)), along = SegmentCleaner.Along(w.Centerline, d.Center);
                if (off > w.TypeThickness / 2 + 60 || along < d.WidthMm / 2 - 100 || along > w.Centerline.Length - d.WidthMm / 2 + 100 || w.Centerline.Length < d.WidthMm + 100) continue;
                if (off < bestOff) { bestOff = off; best = j; }
            }
            if (best < 0) continue;
            var to = walls[best].Centerline;
            double at = Math.Clamp(SegmentCleaner.Along(to, d.Center), d.WidthMm / 2, to.Length - d.WidthMm / 2);
            doors[k] = d with { HostIndex = best, Center = to.A + to.Direction * at };
            rehosted++;
            if (!doors.Where((o, i) => i != k && o.HostIndex == d.HostIndex).Any() && !level.Windows.Any(o => o.HostIndex == d.HostIndex)) drop.Add(d.HostIndex);
        }
        level.Doors = doors;
        if (drop.Count > 0) Remove(level, drop);
        walls = level.Walls.Walls;

        // a single-line partition (low confidence) whose end runs into an opening stops at the host's face instead
        var trimmed = walls.ToList();
        foreach (var (host, c, width) in level.Windows.Select(o => (o.HostIndex, o.Center, o.WidthMm)).Concat(level.Doors.Select(o => (o.HostIndex, o.Center, o.WidthMm))))
        {
            var h = trimmed[host];
            if (h.Arc != null) continue;
            var dir = h.Centerline.Direction; var n = dir.Perp();
            for (int j = 0; j < trimmed.Count; j++)
            {
                var w = trimmed[j];
                if (j == host || w.Arc != null || w.Confidence != Confidence.Low || w.FaceA == "door opening") continue;
                double angle = SegmentCleaner.AngleDiffDeg(w.Centerline.AngleDeg, h.Centerline.AngleDeg);
                if (angle < 45) continue;
                var cl = w.Centerline;
                foreach (bool atB in new[] { false, true })
                {
                    var end = atB ? cl.B : cl.A; var other = atB ? cl.A : cl.B;
                    double u = (end - c).Dot(dir), v = (end - c).Dot(n), vo = (other - c).Dot(n);
                    if (Math.Abs(u) > width / 2 + w.TypeThickness / 2 || Math.Abs(v) > h.TypeThickness / 2 + 5 || Math.Abs(vo) <= h.TypeThickness / 2) continue;
                    // back along the wall to the host's face on the side the wall comes from
                    double face = Math.Sign(vo) * h.TypeThickness / 2, t = (face - vo) / (v - vo);
                    var stop = other + (end - other) * t;
                    if ((stop - other).Length < 300) continue;
                    cl = atB ? cl with { B = stop } : cl with { A = stop };
                    trimmed[j] = w with { Centerline = cl };
                    trims++;
                }
            }
        }
        if (trims > 0) { level.Walls = level.Walls with { Walls = trimmed }; walls = trimmed; }

        level.Windows = level.Windows.Select(o =>
        {
            var skip = o.AlsoCut.Select(a => a.Wall).ToHashSet();
            var r = Clear(walls, o.HostIndex, o.Center, o.WidthMm, skip);
            if (r == null) return o;
            if (Math.Abs(r.Value.Width - o.WidthMm) > 1) narrowed++; else moved++;
            return o with { Center = r.Value.Center, WidthMm = r.Value.Width };
        }).ToList();
        level.Doors = level.Doors.Select(o =>
        {
            var r = Clear(walls, o.HostIndex, o.Center, o.WidthMm, new HashSet<int>());
            if (r == null) return o;
            if (Math.Abs(r.Value.Width - o.WidthMm) > 1) narrowed++; else moved++;
            var shift = r.Value.Center - o.Center;
            return o with { Center = r.Value.Center, WidthMm = r.Value.Width, Hinge = o.Hinge + shift, Swing = o.Swing + shift };
        }).ToList();
        return (moved, narrowed, rehosted, trims);
    }

    /// <summary>A clear place for the opening when another wall stands in its edge; null when it is clear, or blocked in the middle.</summary>
    static (Vec2 Center, double Width)? Clear(IReadOnlyList<WallCandidate> walls, int host, Vec2 c, double width, HashSet<int> skip)
    {
        var h = walls[host];
        if (h.Arc != null || h.Curtain) return null;
        var d = h.Centerline.Direction; var n = d.Perp();
        double band = h.TypeThickness / 2 - 10;
        double reach = width; // look this far past each jamb for room to slide
        var spans = new List<(double Lo, double Hi)>();
        for (int j = 0; j < walls.Count; j++)
        {
            var w = walls[j];
            if (j == host || skip.Contains(j) || w.Arc != null) continue;
            double angle = SegmentCleaner.AngleDiffDeg(w.Centerline.AngleDeg, h.Centerline.AngleDeg);
            if (angle < 15) continue; // parallel linings are cut through in Revit
            double sin = Math.Max(0.3, Math.Sin(angle * Math.PI / 180)), halfAlong = w.TypeThickness / 2 / sin;
            var cl = w.Centerline;
            for (double t = 0; t <= cl.Length; t += 10)
            {
                var p = cl.A + cl.Direction * t;
                double u = (p - c).Dot(d), v = (p - c).Dot(n);
                if (Math.Abs(v) > band || Math.Abs(u) > width / 2 + reach + halfAlong) continue;
                spans.Add((u - halfAlong, u + halfAlong));
            }
        }
        if (spans.Count == 0) return null;
        // the host's own ends limit the slide too
        double a0 = (h.Centerline.A - c).Dot(d), b0 = (h.Centerline.B - c).Dot(d);
        double hostLo = Math.Min(a0, b0), hostHi = Math.Max(a0, b0);
        bool Blocked(double lo, double hi) => spans.Any(s => s.Hi > lo + 5 && s.Lo < hi - 5);
        if (!Blocked(-width / 2, width / 2)) return null;
        // free intervals along the host near the opening
        var merged = new List<(double Lo, double Hi)>();
        foreach (var s in spans.OrderBy(s => s.Lo))
            if (merged.Count > 0 && s.Lo <= merged[^1].Hi) merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, s.Hi));
            else merged.Add(s);
        double from = Math.Max(hostLo, -width / 2 - reach), to = Math.Min(hostHi, width / 2 + reach);
        var free = new List<(double Lo, double Hi)>();
        double at = from;
        foreach (var m in merged)
        {
            if (m.Lo > at) free.Add((at, Math.Min(m.Lo, to)));
            at = Math.Max(at, m.Hi);
        }
        if (at < to) free.Add((at, to));
        // the free stretch overlapping most of the drawn opening
        var best = free.Where(f => f.Hi > f.Lo).OrderByDescending(f => Math.Min(f.Hi, width / 2) - Math.Max(f.Lo, -width / 2)).FirstOrDefault();
        double overlap = Math.Min(best.Hi, width / 2) - Math.Max(best.Lo, -width / 2);
        if (overlap < 0.6 * width) return null; // blocked in the middle: not for here
        double centre, w2;
        if (best.Hi - best.Lo >= width)
        {
            // slide the least distance that clears it
            centre = Math.Clamp(0, best.Lo + width / 2, best.Hi - width / 2);
            w2 = width;
        }
        else { centre = (best.Lo + best.Hi) / 2; w2 = Math.Floor((best.Hi - best.Lo) / 10) * 10; }
        if (w2 < 400) return null;
        return (c + d * centre, w2);
    }

    /// <summary>Takes walls out of the level, keeping the windows' and doors' hosts and cuts pointing at the same walls.</summary>
    static void Remove(SurveyLevel level, HashSet<int> drop)
    {
        var walls = level.Walls.Walls;
        var map = new int[walls.Count];
        var kept = new List<WallCandidate>();
        for (int i = 0; i < walls.Count; i++) { if (drop.Contains(i)) { map[i] = -1; continue; } map[i] = kept.Count; kept.Add(walls[i]); }
        level.Walls = level.Walls with { Walls = kept, ThicknessGroups = kept.Select(w => w.TypeThickness).Distinct().OrderBy(x => x).ToList() };
        level.Windows = level.Windows.Where(o => map[o.HostIndex] >= 0)
            .Select(o => o with { HostIndex = map[o.HostIndex], AlsoCut = o.AlsoCut.Where(a => map[a.Wall] >= 0).Select(a => (map[a.Wall], a.At)).ToList() }).ToList();
        level.Doors = level.Doors.Where(o => map[o.HostIndex] >= 0).Select(o => o with { HostIndex = map[o.HostIndex] }).ToList();
    }
}
