using System.Text.Json;
using Autodesk.Revit.DB;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Learning;
using SurveyToRevit.Core.Survey;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Revit;

/// <summary>
/// Learning point 1 (docs/LEARNING.md): after each build, a snapshot of the walls it made with the reading's evidence;
/// before the next build of the same project, what people did to each (kept, deleted, moved, retyped) goes to the
/// office's learning folder. Never stops a build.
/// </summary>
public static class LearningCapture
{
    /// <summary>Points the engine at the office's learning folder (the shared one when it can be written, else Documents).</summary>
    public static void Init()
    {
        try
        {
            var wanted = UserSettings.Load().LearningFolder;
            if (string.IsNullOrWhiteSpace(wanted)) { LearningFolder.Path = null; return; }
            try { Directory.CreateDirectory(wanted); LearningFolder.Path = wanted; }
            catch { LearningFolder.Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SurveyToRevit", "learning"); }
        }
        catch { LearningFolder.Path = null; }
    }

    sealed record Entry(long Id, string Level, double[] Line, double ThicknessMm, string Confidence, string Layer, string? FaceA, string? FaceB);
    sealed record Snap(string Project, string Surveyor, DateTime Built, List<Entry> Walls);

    static string? SnapPath(Document doc)
    {
        try
        {
            var safe = System.Text.RegularExpressions.Regex.Replace(doc.Title ?? "project", @"[^\w\- ]", "_");
            var dir = Path.Combine(UserSettings.Load().Reports, safe);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "learning-snapshot.json");
        }
        catch { return null; }
    }

    static double Mm(double ft) => ft * UnitConversion.MmPerFoot;

    /// <summary>After a build: the walls it made, each with the reading's evidence (the candidate it came from).</summary>
    public static void Snapshot(Document doc, SurveyModel model)
    {
        try
        {
            if (LearningFolder.Path == null || SnapPath(doc) is not string path) return;
            var entries = new List<Entry>();
            foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                var note = w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                var parts = note.Split('|');
                if (parts.Length < 3 || parts[0] != ModelBuilder.Tag || parts[1] is "CONTEXT" or "ROOF" or "MANSARD") continue;
                if (w.Location is not LocationCurve { Curve: Line line }) continue;
                var a = line.GetEndPoint(0); var b = line.GetEndPoint(1);
                var seg = new Segment2(new Vec2(Mm(a.X), Mm(a.Y)), new Vec2(Mm(b.X), Mm(b.Y)));
                var level = model.Levels.FirstOrDefault(l => l.Code == parts[1]);
                // the candidate it was built from: same floor, centre line close and parallel
                var cand = level?.Walls.Walls.Where(c => c.Arc == null && Math.Abs(c.Centerline.Direction.Dot(seg.Direction)) > 0.99)
                    .OrderBy(c => c.Centerline.Mid.DistanceTo(seg.Mid)).FirstOrDefault(c => c.Centerline.Mid.DistanceTo(seg.Mid) < 300 + c.Centerline.Length / 2);
                entries.Add(new Entry(w.Id.Value, parts[1], new[] { seg.A.X, seg.A.Y, seg.B.X, seg.B.Y }, Math.Round(Mm(w.Width)),
                    cand?.Confidence.ToString() ?? parts[2], cand?.Centerline.Layer ?? "", cand?.FaceA, cand?.FaceB));
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new Snap(doc.Title ?? "", model.Surveyor, DateTime.Now, entries)));
        }
        catch { /* learning never stops a build */ }
    }

    /// <summary>Before a build: what people did to the last build's walls, as examples. Returns how many were written.</summary>
    public static int Capture(Document doc)
    {
        try
        {
            if (LearningFolder.Path == null || SnapPath(doc) is not string path || !File.Exists(path)) return 0;
            var snap = JsonSerializer.Deserialize<Snap>(File.ReadAllText(path));
            File.Move(path, path + ".used", true); // each snapshot is learned from once
            if (snap == null || snap.Walls.Count == 0) return 0;
            var examples = new List<WallExample>();
            foreach (var e in snap.Walls)
            {
                var ex = new WallExample
                {
                    Time = DateTime.Now, Project = snap.Project, Surveyor = snap.Surveyor, Level = e.Level, Layer = e.Layer, FaceA = e.FaceA, FaceB = e.FaceB,
                    Confidence = e.Confidence, ThicknessMm = e.ThicknessMm,
                    LengthMm = Math.Round(new Vec2(e.Line[0], e.Line[1]).DistanceTo(new Vec2(e.Line[2], e.Line[3]))),
                };
                if (doc.GetElement(new ElementId(e.Id)) is not Wall w) { examples.Add(ex with { Outcome = WallOutcome.Deleted }); continue; }
                double moved = 0;
                if (w.Location is LocationCurve { Curve: Line line })
                {
                    var a = line.GetEndPoint(0); var b = line.GetEndPoint(1);
                    var p = new Vec2(Mm(a.X), Mm(a.Y)); var q = new Vec2(Mm(b.X), Mm(b.Y));
                    var p0 = new Vec2(e.Line[0], e.Line[1]); var q0 = new Vec2(e.Line[2], e.Line[3]);
                    moved = Math.Min(Math.Max(p.DistanceTo(p0), q.DistanceTo(q0)), Math.Max(p.DistanceTo(q0), q.DistanceTo(p0)));
                }
                double width = Math.Round(Mm(w.Width));
                examples.Add(moved > 50 ? ex with { Outcome = WallOutcome.Moved, MovedMm = Math.Round(moved) }
                           : Math.Abs(width - e.ThicknessMm) > 5 ? ex with { Outcome = WallOutcome.Retyped, NewThicknessMm = width }
                           : ex with { Outcome = WallOutcome.Kept });
            }
            // a rebuild with nothing touched teaches nothing new (and would only repeat the same walls as kept)
            if (examples.All(x => x.Outcome == WallOutcome.Kept)) return 0;
            return LearningStore.Append(examples);
        }
        catch { return 0; }
    }
}
