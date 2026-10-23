using System.Diagnostics;
using DialogResult = System.Windows.Forms.DialogResult;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;
using SurveyToRevit.Shared;

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

        IReadOnlyList<string> files;
        using (var start = new StartDialog(doc.Title))
        {
            if (start.ShowDialog() != DialogResult.OK || start.Files.Count == 0) return Result.Cancelled;
            files = start.Files;
        }
        try { var settings = UserSettings.Load(); settings.LastSurveyFolder = Path.GetDirectoryName(files[0]); settings.Save(); }
        catch { /* only a convenience for next time */ }

        var lidar = files.Where(SiteContextBuilder.IsSiteFile).ToList();
        var dwgs = files.Where(f => !SiteContextBuilder.IsSiteFile(f)).ToList();
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
        var downloaded = new LidarChoice();
        var checks = model.Checks.ToList();
        while (true)
        {
            using var dialog = new SurveyDialog(model, model.Skipped.Count, lidar.Count, downloaded);
            var result = dialog.ShowDialog();
            if (dialog.OpenReport) { OpenFile(reportPath); continue; }
            if (result != DialogResult.OK) return Result.Cancelled;
            options = dialog.Options;
            if (dialog.Site)
            {
                try { options.Site = SiteContextBuilder.Build(lidar.Concat(downloaded.Files), model.Frame,
                    model.Levels.Where(l => l.Outline != null).Select(l => l.Outline!), dialog.SiteRadiusM, centreM: downloaded.Files.Count > 0 ? downloaded.Centre : null); }
                catch (Exception ex) { TaskDialog.Show("Survey to Revit", "The LiDAR tiles could not be read, so the surroundings are skipped:\n" + ex.Message); }
                // outside walls standing where the LiDAR sees nothing built go on the review list
                try { checks.AddRange(LidarEnvelope.Check(lidar.Concat(downloaded.Files), model.Frame, model.Levels)); }
                catch { /* the check is a help, never a reason to stop */ }
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

        string? reviewPath = null;
        ReviewList? review = null;
        if (checks.Count > 0)
        {
            review = ReviewList.From(doc.Title, checks, model.Levels);
            try { reviewPath = ReviewList.PathFor(UserSettings.Load().Reports, doc.Title); review.Save(reviewPath); }
            catch
            {
                try { reviewPath = ReviewList.PathFor(UserSettings.DefaultReports, doc.Title); review.Save(reviewPath); }
                catch { reviewPath = null; }
            }
        }
        var logPath = SaveText("build", SurveyReport.Build(model)
            + (checks.Count > model.Checks.Count ? "\nLIDAR CHECK\n" + string.Join("\n", checks.Skip(model.Checks.Count).Select(c => "  - " + c.Message)) + "\n" : "")
            + "\nWHAT WAS DONE IN REVIT\n" + string.Join("\n", builder.Log.Select(l => "  - " + l)));
        var done = new TaskDialog("Survey to Revit")
        {
            MainInstruction = "Done",
            MainContent = string.Join("\n", builder.Log.Where(l => model.Levels.Any(x => l.StartsWith(x.Name + ":")) || l.StartsWith("WARNING") || l.StartsWith("Surroundings: ")).Take(18)) + "\n\nFull log:\n" + logPath,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open full log");
        if (review != null && reviewPath != null)
            done.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, $"Open the review list ({review.Items.Count} places to look at)",
                "Rooms with no way in, door symbols with no door, gaps in a column of windows and the like. Also on the ribbon: Review List.");
        var pick = done.Show();
        if (pick == TaskDialogResult.CommandLink1) OpenFile(logPath);
        else if (pick == TaskDialogResult.CommandLink2 && review != null && reviewPath != null) ReviewListCommand.Open(review, reviewPath);
        return Result.Succeeded;
    }

    internal static string SaveText(string kind, string text)
    {
        var dir = UserSettings.Load().Reports;
        try { Directory.CreateDirectory(dir); }
        catch { dir = UserSettings.DefaultReports; Directory.CreateDirectory(dir); } // e.g. a network drive that is not connected
        var path = Path.Combine(dir, $"survey-{kind}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    internal static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* no viewer: path is shown */ }
    }
}
