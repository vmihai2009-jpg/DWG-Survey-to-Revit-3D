namespace SurveyToRevit.Core;

/// <summary>2D point or vector in plan, in millimetres.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(Vec2 o) => X * o.X + Y * o.Y;
    public double Cross(Vec2 o) => X * o.Y - Y * o.X;
    public Vec2 Normalized() { var l = Length; return l == 0 ? this : new(X / l, Y / l); }
    public Vec2 Perp() => new(-Y, X);
    public double DistanceTo(Vec2 o) => (this - o).Length;

    public override string ToString() => $"({X:0.#}, {Y:0.#})";
}

/// <summary>A straight piece of survey linework, with where it came from.</summary>
public sealed record Segment2(Vec2 A, Vec2 B, string Layer = "", string Source = "")
{
    public double Length => A.DistanceTo(B);
    public Vec2 Direction => (B - A).Normalized();
    public Vec2 Mid => (A + B) * 0.5;

    /// <summary>Line angle folded into [0, 180) degrees, so A→B and B→A match.</summary>
    public double AngleDeg
    {
        get
        {
            var a = Math.Atan2(B.Y - A.Y, B.X - A.X) * 180.0 / Math.PI;
            if (a < 0) a += 180.0;
            if (a >= 180.0) a -= 180.0;
            return a;
        }
    }

    public override string ToString() => $"{A}-{B} [{Layer}]";
}

/// <summary>
/// Geometric tolerances, in millimetres and degrees. Fixed units (mm) throughout.
/// Defaults suit surveys of older buildings: small drawing errors are absorbed
/// rather than turned into odd sizes, angles or extra wall pieces.
/// </summary>
public sealed record Tolerances
{
    /// <summary>General error threshold: lines this close sideways are the same line, sizes round to this.</summary>
    public double Precision { get; init; } = 10.0;
    /// <summary>Lines within this angle of a main building direction are straightened onto it.</summary>
    public double AngleSnapDeg { get; init; } = 2.0;
    /// <summary>Lines within this angle count as parallel (after straightening).</summary>
    public double ParallelAngleDeg { get; init; } = 1.0;
    /// <summary>Collinear lines with a gap up to this are joined.</summary>
    public double JoinGap { get; init; } = 10.0;
    /// <summary>Ignore lines shorter than this (hatching, ticks, noise).</summary>
    public double MinSegmentLength { get; init; } = 50.0;
    /// <summary>Thinnest wall to look for.</summary>
    public double MinWallThickness { get; init; } = 50.0;
    /// <summary>Thickest wall to look for (old masonry can be very thick).</summary>
    public double MaxWallThickness { get; init; } = 1000.0;
    /// <summary>Shortest wall piece to accept.</summary>
    public double MinWallLength { get; init; } = 200.0;
    /// <summary>Wall pieces either side of a door or window up to this wide are joined into one wall.</summary>
    public double MaxOpeningWidth { get; init; } = 3000.0;
    /// <summary>Gaps up to this in a wall line are closed for a continuous concept wall (doors, missing linework), and hanging wall ends run on this far to meet a wall.</summary>
    public double DoorGap { get; init; } = 1500.0;

    /// <summary>
    /// Wall type sizes. Each measured thickness becomes the nearest of these, so an
    /// old building gives a handful of wall types instead of one per odd measurement.
    /// Thicker walls round to 100 mm.
    /// </summary>
    public IReadOnlyList<double> WallTypeSizes { get; init; } = new double[] { 100, 150, 200, 300, 450, 600, 900 };

    /// <summary>A wall drawn as a single line (one face only) becomes a wall this thick, centred on the line.</summary>
    public double SingleLineWallThickness { get; init; } = 100.0;
    /// <summary>Single lines shorter than this are not turned into walls (ticks, returns, furniture).</summary>
    public double MinSingleLineWall { get; init; } = 1000.0;

    /// <summary>Lines this close sideways count as the same line.</summary>
    public double CollinearOffset => Precision;
    /// <summary>Wall thicknesses within this are one wall type; type sizes are multiples of it.</summary>
    public double ThicknessGrouping => Precision;

    public static Tolerances Default { get; } = new();
}

public static class UnitConversion
{
    public const double MmPerFoot = 304.8;
    public static double FeetToMm(double feet) => feet * MmPerFoot;
    public static double MmToFeet(double mm) => mm / MmPerFoot;

    /// <summary>Millimetres per drawing unit for common CAD units.</summary>
    public static double MmPerUnit(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres" => 1.0,
        "cm" or "centimeter" or "centimeters" or "centimetre" or "centimetres" => 10.0,
        "m" or "meter" or "meters" or "metre" or "metres" => 1000.0,
        "in" or "inch" or "inches" => 25.4,
        "ft" or "foot" or "feet" => MmPerFoot,
        _ => throw new ArgumentException($"Unknown drawing unit '{unit}'.")
    };
}

/// <summary>A circular arc in plan, anticlockwise from <see cref="Start"/> to <see cref="End"/> (radians), in mm.</summary>
public sealed record Arc2(Vec2 Center, double Radius, double Start, double End, string Layer = "")
{
    public double Sweep { get { double s = Norm(End - Start); return s < 1e-9 ? 2 * Math.PI : s; } }
    public double Length => Radius * Sweep;
    public Vec2 PointAt(double angle) => Center + new Vec2(Math.Cos(angle), Math.Sin(angle)) * Radius;

    /// <summary>Angle of a point around the centre, measured from <see cref="Start"/> (0..2π).</summary>
    public double AngleFromStart(Vec2 p) => Norm(Math.Atan2(p.Y - Center.Y, p.X - Center.X) - Start);

    /// <summary>Short straight pieces along the arc (chords no longer than <paramref name="maxChord"/>).</summary>
    public IEnumerable<Segment2> Tessellate(double maxChord = 250)
    {
        int n = Math.Max(1, (int)Math.Ceiling(Length / maxChord));
        for (int i = 0; i < n; i++)
            yield return new Segment2(PointAt(Start + Sweep * i / n), PointAt(Start + Sweep * (i + 1) / n), Layer, "arc");
    }

    public static double Norm(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }

    /// <summary>The arc through a bulged polyline piece from a to b (bulge = tan(sweep/4), positive = anticlockwise).</summary>
    public static Arc2 FromBulge(Vec2 a, Vec2 b, double bulge, string layer = "")
    {
        double sweep = 4 * Math.Atan(Math.Abs(bulge));
        double chord = a.DistanceTo(b);
        double r = chord / (2 * Math.Sin(sweep / 2));
        var mid = (a + b) * 0.5;
        var dir = (b - a) * (1 / chord);
        var left = new Vec2(-dir.Y, dir.X);
        double h = r * Math.Cos(sweep / 2); // centre distance from the chord
        // anticlockwise travel a→b keeps the centre on the left
        var (from, to, side) = bulge > 0 ? (a, b, 1.0) : (b, a, -1.0);
        var c = mid + left * (h * side);
        return new Arc2(c, r, Math.Atan2(from.Y - c.Y, from.X - c.X), Math.Atan2(to.Y - c.Y, to.X - c.X), layer);
    }
}

/// <summary>Measuring along and across a wall, straight or curved.</summary>
public static class WallGeometry
{
    /// <summary>Distance of a point from the wall's centre line, sideways (sign: straight walls left +, curved walls outside +).</summary>
    public static double Offset(WallCandidate w, Vec2 p) =>
        w.Arc is Arc2 a ? p.DistanceTo(a.Center) - a.Radius : SegmentCleaner.Offset(w.Centerline, p);

    /// <summary>Distance along the centre line from its start to the point's foot (negative before the start).</summary>
    public static double Along(WallCandidate w, Vec2 p)
    {
        if (w.Arc is not Arc2 a) return SegmentCleaner.Along(w.Centerline, p);
        double d = a.AngleFromStart(p);
        if (d <= a.Sweep) return d * a.Radius;
        // beyond the end: measure to whichever end is nearer
        return d - a.Sweep < 2 * Math.PI - d ? d * a.Radius : -(2 * Math.PI - d) * a.Radius;
    }

    public static double Length(WallCandidate w) => w.Arc?.Length ?? w.Centerline.Length;

    public static Vec2 PointAt(WallCandidate w, double along) =>
        w.Arc is Arc2 a ? a.PointAt(a.Start + along / a.Radius) : w.Centerline.A + w.Centerline.Direction * along;

    /// <summary>Direction of the wall (degrees, 0..180) at a distance along it.</summary>
    public static double AngleDegAt(WallCandidate w, double along)
    {
        if (w.Arc is not Arc2 a) return w.Centerline.AngleDeg;
        double t = a.Start + along / a.Radius + Math.PI / 2;
        return (t * 180 / Math.PI % 180 + 180) % 180;
    }
}
