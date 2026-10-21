namespace SurveyToRevit.Core;

/// <summary>A door to place: on wall <see cref="HostIndex"/>, centred at <see cref="Center"/>, in mm.</summary>
public sealed record DoorCandidate(int HostIndex, Vec2 Center, double WidthMm, double HeightMm, bool Double, bool HeightFromSurvey = false);

/// <summary>
/// Doors from the door layers, read the way an architect reads a door symbol. A door is drawn
/// as its leaf swung open (a line from the hinge) and the swing (an arc, or a polyline, from
/// the leaf's tip back to where the leaf closes). The hinge, and the point where the swing ends
/// at the leaf's length from the hinge, are the two sides of the opening, so the opening's width
/// and direction come from the symbol itself, not from whichever wall is nearest. Two swings
/// closing on one point are a double door (leaf-and-a-half too). The door goes in the wall that
/// runs through the opening; where no wall does (a door between two wall ends, as in a lobby),
/// a wall is made across the opening, jamb to jamb, to hold it. A leaf with no swing drawn
/// falls back to the leaf alone. A door head height written nearby ("dh 2.03") is used;
/// otherwise 2100 mm.
/// </summary>
public static class DoorFinder
{
    public const double DefaultHeightMm = 2100;

    /// <summary>Doors on <paramref name="walls"/>; walls made across empty openings are added to <paramref name="added"/> (their doors' HostIndex counts on from the end of <paramref name="walls"/>).</summary>
    public static List<DoorCandidate> Find(IReadOnlyList<Segment2> doorLines, IReadOnlyList<WallCandidate> walls,
        IEnumerable<WindowCandidate>? windows = null, double minLeaf = 350, double maxLeaf = 1300,
        IReadOnlyList<HeightNote>? heights = null, double storeyMm = double.MaxValue, double textReach = 1500,
        IReadOnlyList<Arc2>? arcs = null, List<WallCandidate>? added = null, IReadOnlyList<double>? wallSizes = null)
    {
        var lines = doorLines.Concat((arcs ?? Array.Empty<Arc2>()).Where(a => a.Radius >= minLeaf && a.Radius <= maxLeaf * 1.05).SelectMany(a => a.Tessellate(100))).ToList();
        var pool = lines.SelectMany(l => new[] { l.A, l.B }).ToList();

        // 1. openings from leaf + swing: hinge H, leaf tip T, swing end C with |HC| = |HT|
        var openings = new List<(Vec2 H, Vec2 C, double R)>();
        var usedLeaves = new HashSet<Segment2>();
        foreach (var s in doorLines.Where(l => l.Length >= minLeaf && l.Length <= maxLeaf))
        {
            (Vec2 H, Vec2 C, int score)? best = null;
            foreach (var (h, t) in new[] { (s.A, s.B), (s.B, s.A) })
            {
                double r = s.Length, tolR = Math.Max(30, 0.04 * r);
                var leafDir = (t - h).Normalized();
                Vec2? c = null; double maxAng = 0; int score = 0;
                foreach (var p in pool)
                {
                    double d = p.DistanceTo(h);
                    if (Math.Abs(d - r) > tolR || p.DistanceTo(t) < 20) continue;
                    double ang = Math.Acos(Math.Clamp((p - h).Normalized().Dot(leafDir), -1, 1)) * 180 / Math.PI;
                    if (ang < 5 || ang > 100) continue;
                    score++;
                    if (ang >= 25 && ang > maxAng) { maxAng = ang; c = p; }
                }
                if (c is Vec2 cc && (best == null || score > best.Value.score)) best = (h, cc, score);
            }
            if (best is not { } b) continue;
            usedLeaves.Add(s);
            if (!openings.Any(o => o.H.DistanceTo(b.H) < 50 && o.C.DistanceTo(b.C) < 50))
                openings.Add((b.H, b.C, b.H.DistanceTo(b.C)));
        }

        // 2. two swings closing on one point: one double opening from hinge to hinge
        var merged = new List<(Vec2 A, Vec2 B, bool Double)>();
        var done = new bool[openings.Count];
        for (int i = 0; i < openings.Count; i++)
        {
            if (done[i]) continue;
            done[i] = true;
            var o = openings[i];
            int partner = -1;
            for (int j = i + 1; j < openings.Count && partner < 0; j++)
            {
                if (done[j]) continue;
                var q = openings[j];
                if (q.C.DistanceTo(o.C) > 150) continue;
                var d1 = (o.C - o.H).Normalized(); var d2 = (q.H - q.C).Normalized();
                if (d1.Dot(d2) > Math.Cos(15 * Math.PI / 180)) partner = j;
            }
            if (partner >= 0) { done[partner] = true; merged.Add((o.H, openings[partner].H, true)); }
            else merged.Add((o.H, o.C, false));
        }

        // 3. each opening in the wall running through it, or in a wall made across it
        var all = walls.ToList();
        var doors = new List<DoorCandidate>();
        var wins = windows?.ToList() ?? new List<WindowCandidate>();
        foreach (var (a, b, dbl) in merged.OrderByDescending(m => m.A.DistanceTo(m.B)))
        {
            var mid = (a + b) * 0.5;
            double width = Math.Round(a.DistanceTo(b) / 10) * 10;
            var seg = new Segment2(a, b);
            if (doors.Any(d => d.Center.DistanceTo(mid) < (d.WidthMm + width) / 2 - 50)) continue;
            int host = -1; double bestOff = double.MaxValue;
            for (int k = 0; k < all.Count; k++)
            {
                var w = all[k];
                if (w.Curtain) continue;
                double along = WallGeometry.Along(w, mid), off = Math.Abs(WallGeometry.Offset(w, mid));
                if (off > w.TypeThickness / 2 + 150 || along < -50 || along > WallGeometry.Length(w) + 50) continue;
                if (SegmentCleaner.AngleDiffDeg(WallGeometry.AngleDegAt(w, Math.Clamp(along, 0, WallGeometry.Length(w))), seg.AngleDeg) > 12) continue;
                if (off < bestOff) { bestOff = off; host = k; }
            }
            if (host < 0)
            {
                if (added == null) continue;
                var wall = AcrossOpening(a, b, all, wallSizes);
                added.Add(wall);
                all.Add(wall);
                host = all.Count - 1;
            }
            var hw = all[host];
            double at = Math.Clamp(WallGeometry.Along(hw, mid), width / 2, Math.Max(width / 2, WallGeometry.Length(hw) - width / 2));
            var center = WallGeometry.PointAt(hw, at);
            if (wins.Any(w => w.HostIndex == host && w.Center.DistanceTo(center) < (w.WidthMm + width) / 2)) continue;
            doors.Add(new DoorCandidate(host, center, width, DefaultHeightMm, dbl));
        }

        // 4. leaves drawn without a swing
        foreach (var d in LeafDoors(doorLines.Where(l => !usedLeaves.Contains(l) && l.Length >= 500 && l.Length <= maxLeaf).ToList(), all))
        {
            bool clash = doors.Any(k => k.Center.DistanceTo(d.Center) < (k.WidthMm + d.WidthMm) / 2)
                      || wins.Any(w => w.HostIndex == d.HostIndex && w.Center.DistanceTo(d.Center) < (w.WidthMm + d.WidthMm) / 2);
            if (!clash) doors.Add(d);
        }

        // a surveyed door head ("dh 2.03") nearby sets the height
        if (heights == null) return doors;
        return doors.Select(d => SurveyHeights.Nearest(heights, HeightKind.DoorHead, d.Center, textReach) is double h && h >= 1500 && h <= storeyMm - 100
            ? d with { HeightMm = Math.Round(h / 10) * 10, HeightFromSurvey = true } : d).ToList();
    }

    // a wall across an empty opening, run on at each end to the wall body forming the jamb (up to 400 mm), as thick as those walls (100–300 mm)
    static WallCandidate AcrossOpening(Vec2 a, Vec2 b, List<WallCandidate> walls, IReadOnlyList<double>? sizes)
    {
        var u = (b - a).Normalized();
        var hits = new List<WallCandidate>();
        Vec2 Run(Vec2 p, Vec2 dir)
        {
            for (double d = 0; d <= 400; d += 10)
            {
                var q = p + dir * d;
                var hit = walls.FirstOrDefault(w => Body(w, q));
                if (hit != null) { hits.Add(hit); return q + dir * Math.Min(hit.TypeThickness / 2, 50); }
            }
            return p;
        }
        var s = Run(a, u * -1); var e = Run(b, u);
        double t = hits.Count > 0 ? Math.Clamp(hits.Min(h => h.TypeThickness), 100, 300) : 100;
        double type = sizes != null && sizes.Count > 0 ? WallDetector.SnapThickness(t, sizes) : t;
        return new WallCandidate(new Segment2(s, e, "door opening", "door opening"), t, type, Confidence.Low, "door opening", "door opening");
    }

    static bool Body(WallCandidate w, Vec2 p)
    {
        double along = WallGeometry.Along(w, p);
        return along >= 0 && along <= WallGeometry.Length(w) && Math.Abs(WallGeometry.Offset(w, p)) <= w.TypeThickness / 2;
    }

    // the leaf alone: a line of leaf length hinged at a wall and running away from it
    static List<DoorCandidate> LeafDoors(IReadOnlyList<Segment2> doorLines, IReadOnlyList<WallCandidate> walls)
    {
        var leaves = new List<(int Host, double Hinge, int Side, double Width)>();
        foreach (var s in doorLines)
        {
            // hinge end: on a wall; the leaf runs away from that wall at an angle
            (int host, double hinge, Vec2 free, double off) best = (-1, 0, default, double.MaxValue);
            foreach (var (end, other) in new[] { (s.A, s.B), (s.B, s.A) })
                for (int k = 0; k < walls.Count; k++)
                {
                    var w = walls[k];
                    double off = Math.Abs(WallGeometry.Offset(w, end)), along = WallGeometry.Along(w, end);
                    if (off > w.TypeThickness / 2 + 150 || along < -50 || along > WallGeometry.Length(w) + 50) continue;
                    if (SegmentCleaner.AngleDiffDeg(WallGeometry.AngleDegAt(w, along), s.AngleDeg) < 20) continue; // lies along the wall: a frame line
                    if (off < best.off) best = (k, along, other, off);
                }
            if (best.host < 0) continue;
            var hostWall = walls[best.host];
            double toFree = WallGeometry.Along(hostWall, best.free) - best.hinge;
            // the leaf swings away from the opening's far jamb; a leaf drawn square to the wall is ambiguous: take the side with more wall
            int side = Math.Abs(toFree) > 50 ? Math.Sign(toFree) : (best.hinge < WallGeometry.Length(hostWall) / 2 ? 1 : -1);
            leaves.Add((best.host, best.hinge, side, Math.Round(s.Length / 10) * 10));
        }

        // pair leaves of double doors: same wall, hinges a pair of leaf widths apart, opening towards each other
        var used = new bool[leaves.Count];
        var doors = new List<DoorCandidate>();
        for (int i = 0; i < leaves.Count; i++)
        {
            if (used[i]) continue;
            var a = leaves[i];
            var hw = walls[a.Host];
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
                doors.Add(new DoorCandidate(a.Host, WallGeometry.PointAt(hw, mid), width, DefaultHeightMm, true));
            }
            else
            {
                double mid = Math.Clamp(a.Hinge + a.Side * a.Width / 2, a.Width / 2, Math.Max(a.Width / 2, WallGeometry.Length(hw) - a.Width / 2));
                doors.Add(new DoorCandidate(a.Host, WallGeometry.PointAt(hw, mid), a.Width, DefaultHeightMm, false));
            }
        }

        return doors;
    }
}
