using System.Diagnostics;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using DialogResult = System.Windows.Forms.DialogResult;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core.Site;
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
            Title = "Select the survey DWGs (floor plans; other sheets are ignored), and LiDAR tiles for the surroundings if wanted",
            Filter = "Survey DWGs, LiDAR tiles, footprints (*.dwg;*.tif;*.tiff;*.asc;*.shp)|*.dwg;*.tif;*.tiff;*.asc;*.shp|AutoCAD drawings (*.dwg)|*.dwg",
            Multiselect = true,
        };
        if (pick.ShowDialog() != DialogResult.OK || pick.FileNames.Length == 0) return Result.Cancelled;

        var lidar = pick.FileNames.Where(SiteContextBuilder.IsSiteFile).ToList();
        var dwgs = pick.FileNames.Where(f => !SiteContextBuilder.IsSiteFile(f)).ToList();
        if (dwgs.Count == 0) { message = "Select the survey DWGs too: the surroundings are placed from the survey's coordinates."; return Result.Failed; }

        SurveyModel model;
        try { model = SurveyAnalyzer.Analyze(dwgs); }
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
            using var dialog = new SurveyDialog(model, model.Skipped.Count, lidar.Count);
            var result = dialog.ShowDialog();
            if (dialog.OpenReport) { OpenFile(reportPath); continue; }
            if (result != DialogResult.OK) return Result.Cancelled;
            options = dialog.Options;
            if (dialog.Site)
            {
                try { options.Site = SiteContextBuilder.Build(lidar, model.Frame, model.Levels.Where(l => l.Outline != null).Select(l => l.Outline!), dialog.SiteRadiusM); }
                catch (Exception ex) { TaskDialog.Show("Survey to Revit", "The LiDAR tiles could not be read, so the surroundings are skipped:\n" + ex.Message); }
            }
            break;
        }
        if (!model.Levels.Any(l => l.Include)) return Result.Cancelled;

        var builder = new ModelBuilder(doc, model);
        try { builder.Run(options); }
        catch (Exception ex)
        {
            var errorPath = SaveText("error", ex + "\n\nLOG\n" + string.Join("\n", builder.Log));
            message = $"{ex.Message}\nNothing was changed. Details for support: {errorPath}";
            return Result.Failed;
        }

        var logPath = SaveText("build", SurveyReport.Build(model) + "\nWHAT WAS DONE IN REVIT\n" + string.Join("\n", builder.Log.Select(l => "  - " + l)));
        var done = new TaskDialog("Survey to Revit")
        {
            MainInstruction = "Done",
            MainContent = string.Join("\n", builder.Log.Where(l => model.Levels.Any(x => l.StartsWith(x.Name + ":")) || l.StartsWith("WARNING") || l.StartsWith("Surroundings: ")).Take(18)) + "\n\nFull log:\n" + logPath,
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
