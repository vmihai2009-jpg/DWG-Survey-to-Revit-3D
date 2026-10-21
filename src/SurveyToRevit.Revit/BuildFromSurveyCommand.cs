using System.Diagnostics;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using DialogResult = System.Windows.Forms.DialogResult;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>
/// Pick survey DWGs → analyse them (outside Revit, so spot heights are readable) →
/// show what was found → on confirmation set coordinates, levels, links and walls.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class BuildFromSurveyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }

        using var pick = new OpenFileDialog
        {
            Title = "Select the survey DWGs (floor plans; other sheets are ignored)",
            Filter = "AutoCAD drawings (*.dwg)|*.dwg",
            Multiselect = true,
        };
        if (pick.ShowDialog() != DialogResult.OK || pick.FileNames.Length == 0) return Result.Cancelled;

        SurveyModel model;
        try { model = SurveyAnalyzer.Analyze(pick.FileNames); }
        catch (Exception ex) { message = "Could not analyse the drawings: " + ex.Message; return Result.Failed; }

        var reportPath = SaveText("analysis", SurveyReport.Build(model));
        if (model.Levels.Count == 0)
        {
            TaskDialog.Show("Survey to Revit", "No usable floor plans were found. See the report:\n" + reportPath);
            OpenFile(reportPath);
            return Result.Cancelled;
        }

        var summary = string.Join("\n", model.Levels.Select(l =>
            $"{l.Name}: {(double.IsNaN(l.ElevationMm) ? "height unknown" : $"{l.ElevationMm / 1000:0.00} m")}, {l.Walls.Walls.Count} walls"));
        var dialog = new TaskDialog("Survey to Revit")
        {
            MainInstruction = $"{model.Levels.Count} floor plans, {model.Levels.Sum(l => l.Walls.Walls.Count)} walls found",
            MainContent = summary + $"\n\nProject north turned {model.Frame.RotationDeg:0.00}° from grid north." +
                          (model.Skipped.Count > 0 ? $"\n{model.Skipped.Count} files not used (see report)." : ""),
            FooterText = "Walls from an earlier run on these floors are replaced. Nothing else in the model is touched. Undo reverses the whole run.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Build model", "Set coordinates and north, create levels, link the plans and create walls");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Set up only", "Coordinates, north, levels and linked plans; no walls");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Open the report only");
        var choice = dialog.Show();
        if (choice == TaskDialogResult.CommandLink3) { OpenFile(reportPath); return Result.Succeeded; }
        if (choice is not (TaskDialogResult.CommandLink1 or TaskDialogResult.CommandLink2)) return Result.Cancelled;

        var builder = new ModelBuilder(doc, model);
        try { builder.Run(linkDrawings: true, createWalls: choice == TaskDialogResult.CommandLink1); }
        catch (Exception ex) { message = ex.Message; return Result.Failed; }

        var logPath = SaveText("build", SurveyReport.Build(model) + "\nWHAT WAS DONE IN REVIT\n" + string.Join("\n", builder.Log.Select(l => "  - " + l)));
        var done = new TaskDialog("Survey to Revit")
        {
            MainInstruction = "Done",
            MainContent = string.Join("\n", builder.Log.Where(l => l.Contains("walls created") || l.StartsWith("WARNING")).Take(15)) + "\n\nFull log:\n" + logPath,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open full log");
        if (done.Show() == TaskDialogResult.CommandLink1) OpenFile(logPath);
        return Result.Succeeded;
    }

    static string SaveText(string kind, string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SurveyToRevit");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"survey-{kind}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* no viewer: path is shown */ }
    }
}
