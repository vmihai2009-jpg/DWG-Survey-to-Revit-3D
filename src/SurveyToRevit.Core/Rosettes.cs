namespace SurveyToRevit.Core;

/// <summary>
/// Walls never run through one another at their middles. Three or more straight "walls" crossing at one point,
/// each passing through it near its middle, are a drawn pattern read as walls: a lantern or rooflight seen from
/// below, a dome's ribs, a round table or a revolving door. The spokes and everything inside the circle they
/// sweep (the rings drawn round them) are taken out.
/// </summary>
public static class Rosettes
{
    public static List<WallCandidate> Remove(List<WallCandidate> walls, out List<(Vec2 Centre, double RadiusMm, int Walls)> found)
    {
        found = new();
        var straight = walls.Where(w => w.Arc == null && !w.Curtain && w.Centerline.Length >= 1000).ToList();
        var gone = new HashSet<WallCandidate>(ReferenceEqualityComparer.Instance);
        for (int a = 0; a < straight.Count; a++)
        {
            if (gone.Contains(straight[a])) continue;
            var ca = straight[a].Centerline;
            for (int b = a + 1; b < straight.Count; b++)
            {
                if (gone.Contains(straight[b])) continue;
                var cb = straight[b].Centerline;
                if (SegmentCleaner.AngleDiffDeg(ca.AngleDeg, cb.AngleDeg) < 20) continue;
                if (Cross(ca, cb) is not Vec2 x || !NearMiddle(ca, x) || !NearMiddle(cb, x)) continue;
                var spokes = straight.Where(w => !gone.Contains(w) && Math.Abs(SegmentCleaner.Offset(w.Centerline, x)) <= 100 && NearMiddle(w.Centerline, x)).ToList();
                if (spokes.Count < 3) continue;
                // the spokes must point several ways (not one wall split in pieces)
                if (spokes.Select(w => Math.Round(((w.Centerline.AngleDeg % 180 + 180) % 180) / 20)).Distinct().Count() < 3) continue;
                double r = spokes.Max(w => Math.Max((w.Centerline.A - x).Length, (w.Centerline.B - x).Length)) + 200;
                int n = 0;
                foreach (var w in walls)
                    if (!gone.Contains(w) && (w.Centerline.A - x).Length <= r && (w.Centerline.B - x).Length <= r) { gone.Add(w); n++; }
                found.Add((x, r, n));
                break;
            }
        }
        return gone.Count == 0 ? walls : walls.Where(w => !gone.Contains(w)).ToList();
    }

    /// <summary>The point <paramref name="x"/> lies along the middle third of <paramref name="s"/>.</summary>
    static bool NearMiddle(Segment2 s, Vec2 x)
    {
        double t = (x - s.A).Dot(s.Direction) / s.Length;
        return t > 0.3 && t < 0.7;
    }

    static Vec2? Cross(Segment2 a, Segment2 b)
    {
        var d1 = a.B - a.A; var d2 = b.B - b.A;
        double den = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(den) < 1e-9) return null;
        var w = b.A - a.A;
        double t = (w.X * d2.Y - w.Y * d2.X) / den, u = (w.X * d1.Y - w.Y * d1.X) / den;
        return t is > 0 and < 1 && u is > 0 and < 1 ? a.A + d1 * t : null;
    }
}
