namespace SurveyToRevit.Core.Interior;

/// <summary>
/// One element of the model already in the project (the surveyed exterior: a façade wall, window, shopfront panel,
/// roof...) as triangles in the project's internal coordinates, mm. Revit-free, so it can be cut and tested anywhere.
/// </summary>
public sealed class ExistingElement
{
    public string Category { get; }
    public string Name { get; }
    /// <summary>x, y, z of each corner, three corners per triangle.</summary>
    public double[] Triangles { get; }
    public double MinZ { get; }
    public double MaxZ { get; }

    public ExistingElement(string category, string name, double[] triangles)
    {
        Category = category; Name = name; Triangles = triangles;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 2; i < triangles.Length; i += 3) { lo = Math.Min(lo, triangles[i]); hi = Math.Max(hi, triangles[i]); }
        MinZ = lo; MaxZ = hi;
    }

    /// <summary>The element cut by a level plane at height z: the outline of its section on plan, as segments.</summary>
    public List<Segment2> Cut(double z)
    {
        var result = new List<Segment2>();
        if (z <= MinZ || z >= MaxZ) return result;
        var t = Triangles;
        Span<Vec2> hits = stackalloc Vec2[3];
        for (int i = 0; i + 8 < t.Length; i += 9)
        {
            int n = 0;
            for (int e = 0; e < 3; e++)
            {
                int a = i + e * 3, b = i + (e + 1) % 3 * 3;
                double za = t[a + 2], zb = t[b + 2];
                // a corner exactly on the plane counts as above it, so each crossing is found once
                if ((za < z) == (zb < z)) continue;
                double k = (z - za) / (zb - za);
                if (n < 3) hits[n++] = new Vec2(t[a] + (t[b] - t[a]) * k, t[a + 1] + (t[b + 1] - t[a + 1]) * k);
            }
            if (n == 2 && hits[0].DistanceTo(hits[1]) > 0.5) result.Add(new Segment2(hits[0], hits[1], Category));
        }
        return result;
    }
}

/// <summary>A level of the model already in the project: its height in internal coordinates (mm), and whether it is this project's own (a linked model's levels only help find the heights).</summary>
public sealed record ExistingLevel(string Name, double ElevationMm, bool Own);

/// <summary>The model already in the project: the exterior the interior is fitted into.</summary>
public sealed class ExistingBuilding
{
    public IReadOnlyList<ExistingElement> Elements { get; }
    public IReadOnlyList<ExistingLevel> Levels { get; }
    /// <summary>
    /// Where the project's shared coordinates put the survey, if its shared coordinates are the survey's grid (most
    /// projects set up from a survey): kept exactly when the fit agrees with it. Null when not known.
    /// </summary>
    public IReadOnlyList<SurveyToRevit.Core.Survey.SurveyFrame> SharedFrames { get; init; } = Array.Empty<SurveyToRevit.Core.Survey.SurveyFrame>();
    /// <summary>The shared elevation of the project's internal 0 (mm), the datum when the levels do not settle it.</summary>
    public double? SharedDatumMm { get; init; }

    public ExistingBuilding(IReadOnlyList<ExistingElement> elements, IReadOnlyList<ExistingLevel> levels)
    {
        Elements = elements; Levels = levels;
    }

    /// <summary>Everything cut at height z (internal mm), one list per element that reaches it.</summary>
    public List<(ExistingElement Element, List<Segment2> Outline)> Cut(double z)
    {
        var result = new List<(ExistingElement, List<Segment2>)>();
        foreach (var e in Elements)
        {
            if (z <= e.MinZ || z >= e.MaxZ) continue;
            var cut = e.Cut(z);
            if (cut.Count > 0) result.Add((e, cut));
        }
        return result;
    }
}
