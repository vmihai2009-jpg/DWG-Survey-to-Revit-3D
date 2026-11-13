namespace SurveyToRevit.Core.Survey;

/// <summary>One problem in the built model (item Q of MCP-AUDIT-AND-TOOLS.md): what, which elements, where, the fix.</summary>
public sealed record BuiltFinding(string Kind, string Level, IReadOnlyList<long> Ids, double[] At, string Message, string Fix, double Severity, string Unit);

/// <summary>The Revit-free parts of the built-model checks.</summary>
public static class BuiltChecks
{
    public const double DuplicateMm = 20, DuplicateShare = 0.5, LevelOffMm = 50;

    /// <summary>Walls on the same line (within 20 mm and 2°) that overlap by half the shorter one or more: pairs of ids.</summary>
    public static List<(long A, long B, double OverlapMm)> DuplicateWalls(IReadOnlyList<(long Id, Segment2 Line)> walls, double tolMm = DuplicateMm, double share = DuplicateShare)
    {
        var res = new List<(long, long, double)>();
        for (int i = 0; i < walls.Count; i++)
            for (int j = i + 1; j < walls.Count; j++)
            {
                var a = walls[i].Line; var b = walls[j].Line;
                if (a.Length < 1 || b.Length < 1) continue;
                var d = a.Direction;
                if (Math.Abs(d.X * b.Direction.X + d.Y * b.Direction.Y) < Math.Cos(2 * Math.PI / 180)) continue;
                var n = new Vec2(-d.Y, d.X);
                double o1 = (b.A.X - a.A.X) * n.X + (b.A.Y - a.A.Y) * n.Y, o2 = (b.B.X - a.A.X) * n.X + (b.B.Y - a.A.Y) * n.Y;
                if (Math.Abs(o1) > tolMm || Math.Abs(o2) > tolMm) continue;
                double t1 = (b.A.X - a.A.X) * d.X + (b.A.Y - a.A.Y) * d.Y, t2 = (b.B.X - a.A.X) * d.X + (b.B.Y - a.A.Y) * d.Y;
                double over = Math.Min(Math.Max(t1, t2), a.Length) - Math.Max(Math.Min(t1, t2), 0);
                if (over >= share * Math.Min(a.Length, b.Length)) res.Add((walls[i].Id, walls[j].Id, Math.Round(over)));
            }
        return res;
    }

    /// <summary>The findings as build-log entries ("built: kind"), so they sort with the rest of the log.</summary>
    public static IEnumerable<BuildLogEntry> AsLog(IEnumerable<BuiltFinding> found) => found.Select(f => new BuildLogEntry
    {
        Kind = "built: " + f.Kind, Level = f.Level, At = f.At, Ids = f.Ids.ToList(), Confidence = "Medium",
        Why = f.Message, Evidence = "the engine's check of the built model", Settle = f.Fix, Severity = f.Severity, SeverityUnit = f.Unit,
    });
}
