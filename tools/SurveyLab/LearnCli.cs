using SurveyToRevit.Core;
using SurveyToRevit.Core.Learning;
using SurveyToRevit.Core.Survey;

/// <summary>
/// The learning gate (docs/LEARNING.md), offline:
///   SurveyLab --learning DIR --propose                  write proposals from the office's examples
///   SurveyLab --learning DIR --bench FILE [--adopt]     every survey in FILE (a folder per line) read with the adopted
///                                                       learning and with the proposals on top; --adopt adopts them only
///                                                       when no survey got worse
///   SurveyLab --learning DIR --bench FILE --sweep Name=v1,v2,...   the same surveys at each value of a tolerance
/// </summary>
static class LearnCli
{
    public static bool Handles(string[] args) => args.Contains("--propose") || args.Contains("--bench");

    public static void Run(string[] args)
    {
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        LearningFolder.Path = Arg("--learning") ?? LearningFolder.Path;
        if (LearningFolder.Path == null) { Console.WriteLine("Say where the learning folder is: --learning DIR"); return; }
        if (args.Contains("--propose")) foreach (var line in Adopted.Propose()) Console.WriteLine(line);
        if (Arg("--bench") is not string benchFile) return;
        var surveys = File.ReadAllLines(benchFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && Directory.Exists(l)).ToList();
        if (surveys.Count == 0) { Console.WriteLine("No survey folders in " + benchFile); return; }

        if (Arg("--sweep") is string sweep)
        {
            var (name, values) = (sweep.Split('=')[0], sweep.Split('=')[1].Split(',').Select(double.Parse).ToList());
            Console.WriteLine($"Sweep {name}: " + string.Join(", ", values));
            foreach (var v in values)
            {
                var tol = Tuning.With(Adopted.Tolerances(), name, v);
                var m = surveys.Select(s => Score.Of(SurveyAnalyzer.Analyze(Directory.GetFiles(s, "*.dwg"), tol))).ToList();
                Console.WriteLine($"  {name}={v}: " + string.Join(" | ", m.Select((x, i) => $"{Path.GetFileName(surveys[i])}: {x}")));
            }
            return;
        }

        // adopted only, then adopted with the proposals on top (in a scratch copy of the folder)
        var real = LearningFolder.Path;
        var before = surveys.Select(s => Score.Of(SurveyAnalyzer.Analyze(Directory.GetFiles(s, "*.dwg")))).ToList();
        var scratch = Path.Combine(Path.GetTempPath(), "s2r-learning-candidate");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        Directory.CreateDirectory(Path.Combine(scratch, "adopted"));
        foreach (var dir in new[] { "adopted", "proposed" })
            if (Directory.Exists(Path.Combine(real, dir)))
                foreach (var f in Directory.GetFiles(Path.Combine(real, dir))) File.Copy(f, Path.Combine(scratch, "adopted", Path.GetFileName(f)), true);
        LearningFolder.Path = scratch;
        var after = surveys.Select(s => Score.Of(SurveyAnalyzer.Analyze(Directory.GetFiles(s, "*.dwg")))).ToList();
        LearningFolder.Path = real;

        bool worse = false;
        for (int i = 0; i < surveys.Count; i++)
        {
            var why = after[i].WorseThan(before[i]);
            Console.WriteLine($"{Path.GetFileName(surveys[i])}: before {before[i]} | with proposals {after[i]}{(why != null ? "  WORSE: " + why : "")}");
            worse |= why != null;
        }
        Console.WriteLine(worse ? "GATE: not passed (a survey got worse); nothing adopted." : "GATE: passed (no survey got worse).");
        if (!worse && args.Contains("--adopt"))
            foreach (var f in Adopted.Adopt()) Console.WriteLine("  adopted " + f);
    }
}

/// <summary>What the gate compares per survey: sure walls, windows and doors must not drop; the self-checks must not grow.</summary>
sealed record Score(int Floors, int WallsHigh, int WallsLow, int Windows, int Doors, int Checks, int Stairs)
{
    public static Score Of(SurveyModel m) => new(
        m.Levels.Count(l => l.Include),
        m.Levels.Sum(l => l.Walls.Walls.Count(w => w.Confidence == Confidence.High)),
        m.Levels.Sum(l => l.Walls.Walls.Count(w => w.Confidence == Confidence.Low)),
        m.Levels.Sum(l => l.Windows.Count), m.Levels.Sum(l => l.Doors.Count), m.Checks.Count, m.Levels.Sum(l => l.Stairs.Count));

    public string? WorseThan(Score b)
    {
        var why = new List<string>();
        if (Floors < b.Floors) why.Add($"floors {b.Floors}->{Floors}");
        if (WallsHigh < b.WallsHigh) why.Add($"sure walls {b.WallsHigh}->{WallsHigh}");
        if (Windows < b.Windows) why.Add($"windows {b.Windows}->{Windows}");
        if (Doors < b.Doors) why.Add($"doors {b.Doors}->{Doors}");
        if (Checks > b.Checks) why.Add($"checks {b.Checks}->{Checks}");
        if (Stairs < b.Stairs) why.Add($"stairs {b.Stairs}->{Stairs}");
        return why.Count == 0 ? null : string.Join(", ", why);
    }

    public override string ToString() => $"walls {WallsHigh} sure/{WallsLow} low, {Windows} windows, {Doors} doors, {Stairs} stairs, {Checks} checks";
}
