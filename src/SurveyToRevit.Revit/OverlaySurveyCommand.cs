using DialogResult = System.Windows.Forms.DialogResult;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>
/// Overlay Survey: the project set up from the survey with nothing built. Shared coordinates and base points as
/// Build from Survey sets them, a level per floor (Ground Floor at 0), each floor plan DWG linked into its floor
/// plan and lined up, section views on the survey's cut lines, and every other drawing (elevations, sections,
/// roof plans) linked into a drafting view of its own.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class OverlaySurveyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }

        IReadOnlyList<string> files;
        using (var start = new StartDialog(doc.Title, overlay: true))
        {
            if (start.ShowDialog() != DialogResult.OK || start.Files.Count == 0) return Result.Cancelled;
            files = start.Files;
        }
        var dwgs = files.Where(f => !SiteContextBuilder.IsSiteFile(f)).ToList();
        if (dwgs.Count == 0) { message = "Select the survey DWGs."; return Result.Failed; }

        SurveyModel model;
        try { model = SurveyAnalyzer.Analyze(dwgs); }
        catch (Exception ex) { message = "Could not analyse the drawings: " + ex.Message; return Result.Failed; }
        var floors = model.Levels.Where(l => l.Include && !double.IsNaN(l.ElevationMm)).OrderBy(l => l.ElevationMm).ToList();
        if (floors.Count == 0)
        {
            var report = BuildFromSurveyCommand.SaveText("analysis", SurveyReport.Build(model));
            TaskDialog.Show("Overlay Survey", "No floor plans with a height were found, so there is nothing to set levels from. See the report:\n" + report);
            BuildFromSurveyCommand.OpenFile(report);
            return Result.Cancelled;
        }
        var plans = floors.Select(l => l.Plan.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = dwgs.Where(d => !plans.Contains(d)).ToList();

        // models already here (linked, or this model's own walls) that are drawn somewhere else: where they sit on the survey
        var surveyWalls = floors.SelectMany(l => l.Walls.Walls).Select(w => w.Centerline).ToList();
        var matches = ModelMatch.Find(doc, surveyWalls);
        var movable = matches.Where(m => m.Link != null && m.Good && m.Off).ToList();
        string matchText = matches.Count == 0 ? "" : "\n\nModels already here, against the survey:\n" + string.Join("\n", matches.Select(m => "  " + m.Describe()));

        var confirm = new TaskDialog("Overlay Survey")
        {
            MainInstruction = "Set up the project from the survey (nothing is built)",
            MainContent =
                $"Coordinates: E {model.Frame.BasePoint.X / 1000:0.000} N {model.Frame.BasePoint.Y / 1000:0.000}, project north {model.Frame.RotationDeg:0.00}°; " +
                "base points on the survey base point, Ground Floor = 0.\n\n" +
                "Levels (above survey datum):\n" + string.Join("\n", floors.Select(l => $"  {l.Name}  {l.ElevationMm / 1000:0.000} m  ({Path.GetFileName(l.Plan.Path)})")) +
                $"\n\nEach plan linked into its floor plan and lined up" +
                (model.Sections.Any(s => s.Cut != null) ? "; section views on the survey's cut lines" : "") +
                (others.Count > 0 ? $"; {others.Count} other drawing(s) linked into drafting views of their own." : ".") + matchText,
            CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
        };
        if (movable.Count > 0)
            confirm.VerificationText = movable.Count == 1 ? $"Commit: move the linked model '{movable[0].Name}' onto the survey" : $"Commit: move the {movable.Count} linked models onto the survey";
        if (confirm.Show() != TaskDialogResult.Ok) return Result.Cancelled;
        bool commit = movable.Count > 0 && confirm.WasVerificationChecked();

        var builder = new ModelBuilder(doc, model);
        try { builder.Run(BuildOptions.Overlay(others)); }
        catch (Exception ex)
        {
            var errorPath = BuildFromSurveyCommand.SaveText("error", ex + "\n\nLOG\n" + string.Join("\n", builder.Log));
            message = $"{ex.Message}\nNothing was changed. Details for support: {errorPath}";
            return Result.Failed;
        }
        foreach (var m in matches) builder.Log.Add("Model check: " + m.Describe());
        if (commit) ModelMatch.MoveLinks(doc, movable, builder.Log);
        var logPath = BuildFromSurveyCommand.SaveText("overlay", "OVERLAY SURVEY\n" + string.Join("\n", builder.Log.Select(l => "  - " + l)));
        var done = new TaskDialog("Overlay Survey")
        {
            MainInstruction = "Done",
            MainContent = string.Join("\n", builder.Log.Where(l => l.StartsWith("Project 0") || l.StartsWith("Shared") || l.StartsWith("Base points") ||
                                                                    l.StartsWith("Linked") || l.StartsWith("WARNING") || l.StartsWith("Model") || l.StartsWith("Moved") || l.Contains("lined up") || l.Contains("moved onto")).Take(20))
                          + "\n\nFull log:\n" + logPath,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open full log");
        if (done.Show() == TaskDialogResult.CommandLink1) BuildFromSurveyCommand.OpenFile(logPath);
        return Result.Succeeded;
    }
}

/// <summary>
/// Where a model already in the project (a linked model, or this model's own walls) sits on the survey: its walls are
/// matched to the survey's walls (<see cref="PlanMatch"/>), in plan, by a turn and a shift. A linked model can then
/// be moved onto the survey (its link instance only: the linked file itself is not changed).
/// </summary>
internal sealed record ModelMatch(string Name, RevitLinkInstance? Link, PlanMove Move, int Walls)
{
    public bool Good => Move.Score >= 0.5;
    public bool Off => Move.ShiftMm > 20 || Math.Abs(Move.AngleDeg) > 0.05;

    public string Describe() =>
        Walls < 3 ? $"{Name}: too few walls to compare."
        : !Good ? $"{Name}: does not match the survey's walls (best {Move.Score:P0} of its walls on them); not moved."
        : !Off ? $"{Name}: lines up with the survey ({Move.Score:P0} of its walls on the survey's)."
        : $"{Name}: {Move.ShiftMm / 1000:0.000} m and {Move.AngleDeg:0.00}° off the survey ({Move.Score:P0} of its walls match once moved)" +
          (Link == null ? "; this model's own walls are not moved (link this model into a new project and run Overlay there to move it)." : ".");

    static double Ft(double mm) => UnitConversion.MmToFeet(mm);
    static double Mm(double ft) => UnitConversion.FeetToMm(ft);

    public static List<ModelMatch> Find(Document doc, List<Segment2> survey)
    {
        var result = new List<ModelMatch>();
        if (survey.Count == 0) return result;
        List<Segment2> Walls(Document d, Transform t, Func<Element, bool> keep) =>
            new FilteredElementCollector(d).OfClass(typeof(Wall)).Where(keep)
                .Select(w => (w.Location as LocationCurve)?.Curve).Where(c => c != null && c.IsBound)
                .Select(c => { var a = t.OfPoint(c!.GetEndPoint(0)); var b = t.OfPoint(c.GetEndPoint(1)); return new Segment2(new Vec2(Mm(a.X), Mm(a.Y)), new Vec2(Mm(b.X), Mm(b.Y))); })
                .Where(s => s.Length > 300).ToList();
        bool Ours(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(ModelBuilder.Tag + "|") == true;

        var own = Walls(doc, Transform.Identity, e => !Ours(e));
        if (own.Count >= 3) result.Add(new ModelMatch("This model", null, PlanMatch.Fit(survey, own), own.Count));
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var linked = link.GetLinkDocument();
            if (linked == null) continue;
            var walls = Walls(linked, link.GetTotalTransform(), _ => true);
            result.Add(new ModelMatch(linked.Title, link, walls.Count >= 3 ? PlanMatch.Fit(survey, walls) : default, walls.Count));
        }
        return result;
    }

    /// <summary>Turns and shifts each linked model onto the survey, and up or down so its Ground Floor is at the project's 0.</summary>
    public static void MoveLinks(Document doc, List<ModelMatch> matches, List<string> log)
    {
        using var t = new Transaction(doc, "Survey: move linked models onto the survey");
        t.Start();
        foreach (var m in matches.Where(x => x.Link != null))
        {
            var link = m.Link!;
            try
            {
                bool pinned = link.Pinned;
                link.Pinned = false;
                if (Math.Abs(m.Move.Angle) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, link.Id, Line.CreateBound(XYZ.Zero, XYZ.BasisZ), m.Move.Angle);
                double dz = 0;
                var linked = link.GetLinkDocument();
                var ground = linked == null ? null : new FilteredElementCollector(linked).OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => l.Name.Contains("ground", StringComparison.OrdinalIgnoreCase) &&
                                !l.Name.Contains("lower", StringComparison.OrdinalIgnoreCase) && !l.Name.Contains("upper", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(l => l.Name.Length).FirstOrDefault();
                if (ground != null) dz = -link.GetTotalTransform().OfPoint(new XYZ(0, 0, ground.Elevation)).Z;
                ElementTransformUtils.MoveElement(doc, link.Id, new XYZ(Ft(m.Move.Dx), Ft(m.Move.Dy), Math.Abs(dz) > Ft(5) ? dz : 0));
                link.Pinned = pinned;
                log.Add($"Moved the linked model '{m.Name}' onto the survey: {m.Move.ShiftMm / 1000:0.000} m, {m.Move.AngleDeg:0.00}°" +
                        (ground == null ? " (no Ground Floor level found in it, so its height was left)" :
                         Math.Abs(dz) > Ft(5) ? $", {Mm(dz) / 1000:0.000} m up/down so its {ground.Name} is at the project's 0." : $"; its {ground.Name} was already at 0."));
            }
            catch (Exception ex) { log.Add($"WARNING: could not move the linked model '{m.Name}' ({ex.Message})."); }
        }
        t.Commit();
    }
}
