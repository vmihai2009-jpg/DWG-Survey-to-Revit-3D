using DialogResult = System.Windows.Forms.DialogResult;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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

        var confirm = new TaskDialog("Overlay Survey")
        {
            MainInstruction = "Set up the project from the survey (nothing is built)",
            MainContent =
                $"Coordinates: E {model.Frame.BasePoint.X / 1000:0.000} N {model.Frame.BasePoint.Y / 1000:0.000}, project north {model.Frame.RotationDeg:0.00}°; " +
                "base points on the survey base point, Ground Floor = 0.\n\n" +
                "Levels (above survey datum):\n" + string.Join("\n", floors.Select(l => $"  {l.Name}  {l.ElevationMm / 1000:0.000} m  ({Path.GetFileName(l.Plan.Path)})")) +
                $"\n\nEach plan linked into its floor plan and lined up" +
                (model.Sections.Any(s => s.Cut != null) ? "; section views on the survey's cut lines" : "") +
                (others.Count > 0 ? $"; {others.Count} other drawing(s) linked into drafting views of their own." : "."),
            CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
        };
        if (confirm.Show() != TaskDialogResult.Ok) return Result.Cancelled;

        var builder = new ModelBuilder(doc, model);
        try { builder.Run(BuildOptions.Overlay(others)); }
        catch (Exception ex)
        {
            var errorPath = BuildFromSurveyCommand.SaveText("error", ex + "\n\nLOG\n" + string.Join("\n", builder.Log));
            message = $"{ex.Message}\nNothing was changed. Details for support: {errorPath}";
            return Result.Failed;
        }
        var logPath = BuildFromSurveyCommand.SaveText("overlay", "OVERLAY SURVEY\n" + string.Join("\n", builder.Log.Select(l => "  - " + l)));
        var done = new TaskDialog("Overlay Survey")
        {
            MainInstruction = "Done",
            MainContent = string.Join("\n", builder.Log.Where(l => l.StartsWith("Project 0") || l.StartsWith("Shared") || l.StartsWith("Base points") ||
                                                                    l.StartsWith("Linked") || l.StartsWith("WARNING") || l.Contains("lined up") || l.Contains("moved onto")).Take(20))
                          + "\n\nFull log:\n" + logPath,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open full log");
        if (done.Show() == TaskDialogResult.CommandLink1) BuildFromSurveyCommand.OpenFile(logPath);
        return Result.Succeeded;
    }
}
