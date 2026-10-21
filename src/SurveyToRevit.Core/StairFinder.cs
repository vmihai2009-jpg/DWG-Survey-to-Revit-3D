using System.Text.RegularExpressions;

namespace SurveyToRevit.Core;

/// <summary>
/// One flight: its riser lines in walking order (bottom first), each drawn left to right
/// for someone walking up. Straight and curved flights alike (a curved flight's risers fan out).
/// </summary>
public sealed record StairFlight(IReadOnlyList<Segment2> Risers)
{
    public double Width => Risers.Select(r => r.Length).OrderBy(x => x).ElementAt(Risers.Count / 2);
    /// <summary>Typical tread depth, along the walking line.</summary>
    public double Going => Risers.Count < 2 ? 0 : Enumerable.Range(1, Risers.Count - 1).Select(i => Risers[i].Mid.DistanceTo(Risers[i - 1].Mid)).OrderBy(x => x).ElementAt((Risers.Count - 1) / 2);
    public bool Curved => Risers.Count >= 3 && SegmentCleaner.AngleDiffDeg(Risers[0].AngleDeg, Risers[^1].AngleDeg) > 10;
    public Vec2 Bottom => Risers[0].Mid;
    public Vec2 Top => Risers[^1].Mid;
    /// <summary>The walking line through the middle of the risers.</summary>
    public IReadOnlyList<Vec2> Path => Risers.Select(r => r.Mid).ToList();
}

/// <summary>A stair: flights in the order they are climbed. <see cref="DirectionKnown"/> is false when the drawing gave no "UP" note.</summary>
public sealed record StairCandidate(IReadOnlyList<StairFlight> Flights, bool DirectionKnown)
{
    public int Risers => Flights.Sum(f => f.Risers.Count);

    /// <summary>The stair's footprint (flights and the landings between them) as a convex outline, grown by a margin: the opening it needs in the floor above.</summary>
    public Polygon2 Footprint(double margin = 50)
    {
        var pts = Flights.SelectMany(f => f.Risers.SelectMany(r => new[] { r.A, r.B })).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return new Polygon2(pts);
        double Cross(Vec2 o, Vec2 a, Vec2 b) => (a - o).Cross(b - o);
        var hull = new List<Vec2>();
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            int start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        var c = new Vec2(hull.Average(p => p.X), hull.Average(p => p.Y));
        return new Polygon2(hull.Select(p => p + (p - c).Normalized() * margin).ToList());
    }
}

/// <summary>
/// Stairs from the lines on stair layers, the same way for any survey: risers are lines a
/// stair's width long, side by side a tread apart (150–450 mm) and nearly parallel (a curved
/// flight's risers turn a little from one to the next). Three or more in a row are a flight.
/// Flights whose ends are close belong to one stair (a dogleg, an L); an "UP" note near a
/// flight end marks where the climb starts.
/// </summary>
public static class StairFinder
{
    static readonly Regex Up = new(@"^\s*up\b", RegexOptions.IgnoreCase);

    public static List<StairCandidate> Find(IReadOnlyList<Segment2> stairLines, IReadOnlyList<(Vec2 Position, string Value)> texts,
        double minWidth = 600, double maxWidth = 3500, double minGoing = 150, double maxGoing = 450, double maxTurnDeg = 20, double joinFlights = 3000)
    {
        var risers = stairLines.Where(l => l.Length >= minWidth && l.Length <= maxWidth).ToList();
        int n = risers.Count;
        // each riser's nearest neighbour on either side
        var next = new int[n]; var prev = new int[n];
        Array.Fill(next, -1); Array.Fill(prev, -1);
        var best = new double[n, 2];
        for (int i = 0; i < n; i++) { best[i, 0] = best[i, 1] = double.MaxValue; }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                var a = risers[i]; var b = risers[j];
                if (SegmentCleaner.AngleDiffDeg(a.AngleDeg, b.AngleDeg) > maxTurnDeg) continue;
                double ratio = a.Length / b.Length;
                if (ratio < 0.6 || ratio > 1.67) continue;
                var d = b.Mid - a.Mid;
                double dist = d.Length;
                if (dist < minGoing || dist > maxGoing) continue;
                var u = a.Direction;
                if (Math.Abs(d.Dot(u)) > dist * 0.5) continue; // side by side, not end to end
                double along = (b.Mid - a.A).Dot(u);
                if (along < 0 || along > a.Length) continue;
                int side = u.Cross(d) > 0 ? 0 : 1;
                if (dist < best[i, side]) { best[i, side] = dist; if (side == 0) next[i] = j; else prev[i] = j; }
            }
        // keep only mutual links: i's next is j and j's prev is i (or the other way round for a riser drawn the other way)
        var link = new List<int>[n];
        for (int i = 0; i < n; i++) link[i] = new List<int>();
        for (int i = 0; i < n; i++)
            foreach (var j in new[] { next[i], prev[i] })
                if (j >= 0 && (next[j] == i || prev[j] == i) && !link[i].Contains(j)) { link[i].Add(j); link[j].Add(i); }

        // flights: chains of linked risers
        var seen = new bool[n];
        var flights = new List<StairFlight>();
        for (int s = 0; s < n; s++)
        {
            if (seen[s] || link[s].Count > 1) continue; // start at a chain end
            var chain = new List<int> { s };
            seen[s] = true;
            int cur = s;
            while (link[cur].FirstOrDefault(k => !seen[k], -1) is int k && k >= 0) { chain.Add(k); seen[k] = true; cur = k; }
            if (chain.Count >= 3) flights.Add(Orient(chain.Select(i => risers[i]).ToList()));
        }

        // flights close to each other are one stair; climbing order from an "UP" note, else from the free end
        var ups = texts.Where(t => Up.IsMatch(t.Value ?? "")).Select(t => t.Position).ToList();
        var left = new List<StairFlight>(flights);
        var stairs = new List<StairCandidate>();
        while (left.Count > 0)
        {
            var group = new List<StairFlight> { left[0] };
            left.RemoveAt(0);
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var f in left.ToList())
                    if (group.Any(g => Ends(g).Any(e => Ends(f).Any(o => o.DistanceTo(e) <= joinFlights))))
                    { group.Add(f); left.Remove(f); grew = true; }
            }
            stairs.Add(Order(group, ups, joinFlights));
        }
        return stairs;
    }

    static IEnumerable<Vec2> Ends(StairFlight f) => new[] { f.Bottom, f.Top };

    // risers in walking order, each drawn left to right
    static StairFlight Orient(List<Segment2> chain)
    {
        var walk = chain[^1].Mid - chain[0].Mid;
        var list = chain.Select(r => r.Direction.Cross(walk) < 0 ? r : new Segment2(r.B, r.A, r.Layer, r.Source)).ToList();
        return new StairFlight(list);
    }

    static StairFlight Reverse(StairFlight f) => Orient(f.Risers.Reverse().ToList());

    static StairCandidate Order(List<StairFlight> group, List<Vec2> ups, double reach)
    {
        // the first flight and its bottom: an UP note by a flight end, or the end farthest from the other flights
        StairFlight? first = null; bool known = false;
        foreach (var f in group)
            foreach (var (end, flip) in new[] { (f.Bottom, false), (f.Top, true) })
                if (ups.Any(u => u.DistanceTo(end) <= 2000)) { first = flip ? Reverse(f) : f; known = true; }
        if (first == null)
        {
            double Score(Vec2 e, StairFlight self) => group.Where(g => !ReferenceEquals(g, self)).SelectMany(Ends).Select(o => o.DistanceTo(e)).DefaultIfEmpty(0).Min();
            var pick = group.SelectMany(f => new[] { (f, Bottom: true, S: Score(f.Bottom, f)), (f, Bottom: false, S: Score(f.Top, f)) })
                            .OrderByDescending(x => x.S).First();
            first = pick.Bottom ? pick.f : Reverse(pick.f);
        }
        var original = group.First(g => g.Risers.Contains(first.Risers[0]) || g.Risers.Contains(new Segment2(first.Risers[0].B, first.Risers[0].A, first.Risers[0].Layer, first.Risers[0].Source)));
        var rest = group.Where(g => !ReferenceEquals(g, original)).ToList();
        var ordered = new List<StairFlight> { first };
        while (rest.Count > 0)
        {
            var top = ordered[^1].Top;
            var nextFlight = rest.OrderBy(f => Math.Min(f.Bottom.DistanceTo(top), f.Top.DistanceTo(top))).First();
            rest.Remove(nextFlight);
            ordered.Add(nextFlight.Bottom.DistanceTo(top) <= nextFlight.Top.DistanceTo(top) ? nextFlight : Reverse(nextFlight));
        }
        return new StairCandidate(ordered, known);
    }
}
