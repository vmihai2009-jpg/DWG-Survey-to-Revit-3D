using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Shared;
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;

namespace SurveyToRevit.Launcher;

/// <summary>
/// Installs a release from its zip, on the spot: the newest SurveyToRevit-x.y.z.zip in the folder set in
/// Settings (Downloads unless changed), or one picked by hand. The next click on Build from Survey or
/// Wall from Lines runs it; Revit does not need restarting. Current user only, no admin rights.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class UpdaterCommand : IExternalCommand
{
    const string Title = "Survey to Revit - Updater";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var settings = UserSettings.Load();
        var running = Engine.Current(settings);
        var found = Releases.NewestZip(settings.Updates);
        var newer = found is { } f && (running == null || f.Version > running.Version);

        var ask = new TaskDialog(Title)
        {
            MainInstruction = newer ? $"v{Releases.Short(found!.Value.Version)} is ready to install" : "No newer release found",
            MainContent = $"Running now: {running?.ToString() ?? "none"}\nLooking in: {settings.Updates}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        if (newer)
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Install v{Releases.Short(found!.Value.Version)} now",
                Path.GetFileName(found.Value.File) + ". It takes effect straight away: no need to restart Revit.");
        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Choose a zip...",
            "Any Survey to Revit release zip, including an older one to go back to.");
        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Settings...",
            "Change where the Updater looks, or which installed version runs.");

        string zip;
        switch (ask.Show())
        {
            case TaskDialogResult.CommandLink1: zip = found!.Value.File; break;
            case TaskDialogResult.CommandLink2:
                using (var pick = new OpenFileDialog
                {
                    Title = $"Pick the Survey to Revit release zip (running: {running?.ToString() ?? "none"})",
                    Filter = "Survey to Revit release (SurveyToRevit-*.zip)|SurveyToRevit-*.zip|Zip files (*.zip)|*.zip",
                    InitialDirectory = Directory.Exists(settings.Updates) ? settings.Updates : "",
                })
                {
                    if (pick.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    zip = pick.FileName;
                }
                break;
            case TaskDialogResult.CommandLink3: return SettingsCommand.Show();
            default: return Result.Cancelled;
        }

        Installed done;
        try { done = Installer.FromZip(zip, Engine.LauncherVersion, DateTime.Now); }
        catch (Exception ex) { message = "The update did not install: " + ex.Message; return Result.Failed; }

        // Whatever was chosen in Settings before, the version just installed is the one wanted now.
        settings = UserSettings.Load();
        settings.RunVersion = null;
        settings.Save();
        try { Installer.Tidy(Releases.Root, settings.KeepVersions, Path.GetFileName(done.EngineFolder), null, Engine.LauncherFolder); }
        catch { /* old copies are tidied next time */ }
        App.RefreshUpdater(settings);

        TaskDialog.Show(Title,
            $"v{Releases.Short(done.Engine)} is installed and ready. Build from Survey and Wall from Lines use it from now on; " +
            "no need to restart Revit." +
            (done.Launcher != null
                ? "\n\nThis release also changes the ribbon buttons, the Updater or Settings themselves. Those changes show the next time Revit starts; everything else works now."
                : ""));
        return Result.Succeeded;
    }
}
