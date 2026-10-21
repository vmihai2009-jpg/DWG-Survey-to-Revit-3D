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
/// show what was found in a window where levels and options can be adjusted →
/// set coordinates, levels, links, walls, floors, ceilings and generic models.
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

        BuildOptions options;
        while (true)
        {
            using var dialog = new SurveyDialog(model, model.Skipped.Count);
            var result = dialog.ShowDialog();
            if (dialog.OpenReport) { OpenFile(reportPath); continue; }
            if (result != DialogResult.OK) return Result.Cancelled;
            options = dialog.Options;
            break;
        }
        if (!model.Levels.Any(l => l.Include)) return Result.Cancelled;

        var builder = new ModelBuilder(doc, model);
        try { builder.Run(options); }
        catch (Exception ex) { message = ex.Message; return Result.Failed; }

        var logPath = SaveText("build", SurveyReport.Build(model) + "\nWHAT WAS DONE IN REVIT\n" + string.Join("\n", builder.Log.Select(l => "  - " + l)));
        var done = new TaskDialog("Survey to Revit")
        {
            MainInstruction = "Done",
            MainContent = string.Join("\n", builder.Log.Where(l => model.Levels.Any(x => l.StartsWith(x.Name + ":")) || l.StartsWith("WARNING")).Take(15)) + "\n\nFull log:\n" + logPath,
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
