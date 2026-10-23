using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Revit;

/// <summary>
/// How close the generated model is to a reference model of the same building (one modelled by hand),
/// linked into this project. Walls, windows and doors of the Existing phase are compared level by level
/// (levels paired by height) and counted as right, off, missed or invented: see <see cref="ScoreCard"/>.
/// The result is a text report; nothing in the model is changed.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class ScoreCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() != null).ToList();
        if (links.Count == 0)
        {
            TaskDialog.Show("Score against a reference", "Link the reference model first (Insert > Link Revit, Auto - Internal Origin to Internal Origin, " +
                "or Shared Coordinates), and make sure it is loaded. Then run this again.");
            return Result.Cancelled;
        }
        var link = links[0];
        if (links.Count > 1)
        {
            var pick = new TaskDialog("Score against a reference") { MainInstruction = "Which linked model is the reference?" };
            var ids = new[] { TaskDialogCommandLinkId.CommandLink1, TaskDialogCommandLinkId.CommandLink2, TaskDialogCommandLinkId.CommandLink3, TaskDialogCommandLinkId.CommandLink4 };
            for (int i = 0; i < Math.Min(4, links.Count); i++) pick.AddCommandLink(ids[i], links[i].GetLinkDocument().Title);
            var res = pick.Show();
            int k = Array.FindIndex(ids, id => (int)id == (int)res);
            if (k < 0) return Result.Cancelled;
            link = links[k];
        }

        var ourLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var transform = link.GetTotalTransform();
        var refDoc = link.GetLinkDocument();
        // each reference level goes with our level at (nearly) the same height
        string? OurLevel(double elevationFt) =>
            ourLevels.OrderBy(l => Math.Abs(l.Elevation - elevationFt)).FirstOrDefault(l => Math.Abs(l.Elevation - elevationFt) <= Ft(300))?.Name;

        var notes = new List<string>();
        var ours = Collect(doc, Transform.Identity, e => Ours(e), l => l.Name, notes, "this model");
        var reference = Collect(refDoc, transform, _ => true, l => OurLevel(transform.OfPoint(new XYZ(0, 0, l.Elevation)).Z), notes, refDoc.Title);
        if (ours.Walls.Count == 0) { TaskDialog.Show("Score against a reference", "No walls made by Build from Survey were found in this model."); return Result.Cancelled; }

        var score = ScoreCard.Score(ours.Walls, reference.Walls, ours.Windows, reference.Windows, ours.Doors, reference.Doors);
        score.Notes.AddRange(notes);
        var text = $"SCORE AGAINST {refDoc.Title}\n{DateTime.Now:d MMM yyyy HH:mm}\n" +
                   "right = in place at the right size; off = found, but out of place or the wrong size; missed = in the reference only; invented = in ours only.\n\n" + score.Text();
        var path = Save(text);
        var (w, wi, d) = score.Total();
        static string Pct(double v) => double.IsNaN(v) ? "-" : $"{v * 100:0}%";
        var done = new TaskDialog("Score against a reference")
        {
            MainInstruction = $"Walls {Pct(w.Recall)} found · windows {Pct(wi.Recall)} · doors {Pct(d.Recall)}",
            MainContent = $"Walls: {w.Right} right, {w.Off} off, {w.Missed} missed, {w.Invented} invented\n" +
                          $"Windows: {wi.Right} right, {wi.Off} off, {wi.Missed} missed, {wi.Invented} invented\n" +
                          $"Doors: {d.Right} right, {d.Off} off, {d.Missed} missed, {d.Invented} invented\n\n" +
                          $"Wall length found: {score.FoundWallM:0} of {score.RefWallM:0} m.\nReport with each level:\n{path}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the report");
        if (done.Show() == TaskDialogResult.CommandLink1)
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* path is shown */ }
        return Result.Succeeded;
    }

    static double Ft(double mm) => UnitConversion.MmToFeet(mm);
    static double Mm(double ft) => UnitConversion.FeetToMm(ft);

    static bool Ours(Element e) =>
        e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(ModelBuilder.Tag + "|") == true;

    sealed record Set(List<ScoreWall> Walls, List<ScoreOpening> Windows, List<ScoreOpening> Doors);

    /// <summary>The existing walls, windows and doors of a model, in this project's coordinates, by our level name.</summary>
    static Set Collect(Document d, Transform t, Func<Element, bool> keep, Func<Level, string?> levelName, List<string> notes, string what)
    {
        var set = new Set(new(), new(), new());
        // the Existing phase when the model has one (a survey model may also hold proposals); otherwise everything
        var existing = d.Phases.Cast<Phase>().FirstOrDefault(p => p.Name.Contains("Existing", StringComparison.OrdinalIgnoreCase));
        bool Now(Element e) => existing == null || e.CreatedPhaseId == existing.Id;
        var names = new Dictionary<ElementId, string?>();
        string? Name(ElementId id)
        {
            if (!names.TryGetValue(id, out var n)) names[id] = n = d.GetElement(id) is Level l ? levelName(l) : null;
            return n;
        }
        Vec2 P(XYZ p) { var q = t.OfPoint(p); return new Vec2(Mm(q.X), Mm(q.Y)); }
        int noLevel = 0;
        foreach (var wall in new FilteredElementCollector(d).OfClass(typeof(Wall)).Cast<Wall>())
        {
            if (!keep(wall) || !Now(wall) || wall.Location is not LocationCurve lc) continue;
            if (wall.CurtainGrid != null && wall.WallType.Kind == WallKind.Curtain) continue;
            if (Name(wall.LevelId) is not string level) { noLevel++; continue; }
            var c = lc.Curve;
            set.Walls.Add(new ScoreWall(level, new Segment2(P(c.GetEndPoint(0)), P(c.GetEndPoint(1))), Mm(wall.Width)));
        }
        foreach (var (cat, list) in new[] { (BuiltInCategory.OST_Windows, set.Windows), (BuiltInCategory.OST_Doors, set.Doors) })
            foreach (var fi in new FilteredElementCollector(d).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                if (!keep(fi) || !Now(fi) || fi.Location is not LocationPoint lp) continue;
                if (Name(fi.LevelId) is not string level) { noLevel++; continue; }
                list.Add(new ScoreOpening(level, P(lp.Point), Width(fi)));
            }
        if (noLevel > 0) notes.Add($"{what}: {noLevel} elements on levels with no matching level in this project (more than 300 mm apart in height) were left out.");
        if (existing == null) notes.Add($"{what}: no Existing phase, so every element was compared.");
        return set;
    }

    static double Width(FamilyInstance fi)
    {
        foreach (var e in new Element[] { fi, fi.Symbol })
            foreach (var p in new[] { BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
                if (e.get_Parameter(p) is Parameter par && par.HasValue && par.AsDouble() > 0) return Mm(par.AsDouble());
        return double.NaN;
    }

    static string Save(string text)
    {
        var dir = UserSettings.Load().Reports;
        try { Directory.CreateDirectory(dir); }
        catch { dir = UserSettings.DefaultReports; Directory.CreateDirectory(dir); }
        var path = Path.Combine(dir, $"survey-score_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, text);
        return path;
    }
}
