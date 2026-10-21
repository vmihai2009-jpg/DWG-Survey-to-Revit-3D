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
/// Geometric tolerances, in millimetres and degrees. Defaults suit typical
/// building surveys drawn in mm; all are user-adjustable.
/// </summary>
public sealed record Tolerances
{
    /// <summary>Lines within this angle count as parallel.</summary>
    public double ParallelAngleDeg { get; init; } = 1.0;
    /// <summary>Parallel lines this close sideways count as the same line.</summary>
    public double CollinearOffset { get; init; } = 5.0;
    /// <summary>Collinear lines with a gap up to this are joined.</summary>
    public double JoinGap { get; init; } = 10.0;
    /// <summary>Ignore lines shorter than this (hatching, ticks, noise).</summary>
    public double MinSegmentLength { get; init; } = 50.0;
    /// <summary>Thinnest wall to look for.</summary>
    public double MinWallThickness { get; init; } = 50.0;
    /// <summary>Thickest wall to look for.</summary>
    public double MaxWallThickness { get; init; } = 800.0;
    /// <summary>Shortest wall piece to accept.</summary>
    public double MinWallLength { get; init; } = 200.0;
    /// <summary>Thicknesses within this are grouped into one wall type.</summary>
    public double ThicknessGrouping { get; init; } = 10.0;

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
