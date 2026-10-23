using System.Reflection;
using Autodesk.Revit.UI;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Launcher;

/// <summary>
/// What Revit loads at start-up: the "Survey to Revit" panel on the shared Barr Gazetas tab.
/// Build from Survey, Wall from Lines and Office Test-Fit run the newest installed engine on each click; the Updater
/// installs a new engine from a release zip and Settings holds the user's options.
/// </summary>
public class App : IExternalApplication
{
    // Shared with other Barr Gazetas add-ins; the name must match exactly (case-sensitive).
    const string TabName = "Barr Gazetas";
    static PushButton? _updater;

    public Result OnStartup(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already created by another add-in */ }

        var me = Assembly.GetExecutingAssembly().Location;
        var panel = app.CreateRibbonPanel(TabName, "Survey to Revit");
        panel.AddItem(new PushButtonData("BuildFromSurvey", "Build from\nSurvey", me, typeof(BuildFromSurvey).FullName)
        {
            ToolTip = "Select survey DWGs. Sets project coordinates and north from the survey, creates levels from the " +
                      "spot heights, links each plan to its level and builds a concept model: walls, windows, floors, ceilings. " +
                      "You choose floors and options in a window before anything changes.",
            Image = Icon("icon16.png"),
            LargeImage = Icon("icon32.png"),
        });
        panel.AddItem(new PushButtonData("WallFromLines", "Wall from\nLines", me, typeof(WallFromLines).FullName)
        {
            ToolTip = "For walls the survey reading missed: in a plan view, pick the two face lines of a wall in the linked survey " +
                      "(straight lines, arcs, or an arc and a straight line). A Concept Wall is built between them on this level, " +
                      "up to the next level, in the Existing phase. Repeat for more; Esc to finish.",
            Image = Icon("icon16.png"),
            LargeImage = Icon("icon32.png"),
        });
        panel.AddStackedItems(
            new PushButtonData("SurveyReviewList", "Review List", me, typeof(ReviewList).FullName)
            {
                ToolTip = "The places the last Build from Survey was unsure about (rooms with no way in, door symbols with no door, " +
                          "gaps in a column of windows, walls stopping short...). Double-click one to go to it on its floor plan; " +
                          "tick it off once looked at.",
                Image = Icon("icon16.png"), LargeImage = Icon("icon32.png"),
            },
            new PushButtonData("SurveyScore", "Score", me, typeof(Score).FullName)
            {
                ToolTip = "With a reference model of the same building linked in (modelled by hand): counts the generated walls, " +
                          "windows and doors that are right, off, missed or invented, level by level. Nothing is changed.",
                Image = Icon("icon16.png"), LargeImage = Icon("icon32.png"),
            });
        panel.AddSeparator();
        panel.AddItem(new PushButtonData("OfficeTestFit", "Office\nTest-Fit", me, typeof(OfficeTestFit).FullName)
        {
            ToolTip = "Open the floor plan of a floor, then click: the workplace test-fit opens on that floor's real outline, " +
                      "walls, columns and rooms. Set the density, lay out workspace, meeting rooms and breakout, and read " +
                      "headcount and NIA. Nothing in the model is changed.",
            Image = Icon("testfit16.png"),
            LargeImage = Icon("testfit32.png"),
        });
        var stacked = panel.AddStackedItems(
            new PushButtonData("SurveyUpdater", "Updater", me, typeof(UpdaterCommand).FullName)
            {
                Image = Icon("updater16.png"), LargeImage = Icon("updater32.png"),
            },
            new PushButtonData("SurveySettings", "Settings", me, typeof(SettingsCommand).FullName)
            {
                ToolTip = "Where the Updater looks for release zips, which installed version runs, default slab thickness " +
                          "and LiDAR radius, where reports are saved, and the installed versions.",
                Image = Icon("settings16.png"), LargeImage = Icon("settings32.png"),
            });
        _updater = stacked.OfType<PushButton>().FirstOrDefault();

        var settings = UserSettings.Load();
        try { Installer.Tidy(Releases.Root, settings.KeepVersions, Engine.Current(settings)?.Name, settings.RunVersion, Engine.LauncherFolder); }
        catch { /* tidying is never worth failing start-up for */ }
        RefreshUpdater(settings);
        return Result.Succeeded;
    }

    /// <summary>Shows on the Updater button whether a newer release zip is waiting.</summary>
    internal static void RefreshUpdater(UserSettings settings)
    {
        if (_updater == null) return;
        try
        {
            var running = Engine.Current(settings);
            var waiting = settings.CheckAtStartup ? Releases.NewestZip(settings.Updates) : null;
            var newer = waiting is { } w && (running == null || w.Version > running.Version);
            _updater.ItemText = newer ? "Update ready" : "Updater";
            _updater.ToolTip = $"Running: {(running?.ToString() ?? "none")}. " +
                               (newer ? $"v{Releases.Short(waiting!.Value.Version)} is waiting in {settings.Updates}. " : "") +
                               "Installs a release from its zip (no need to unzip). It takes effect straight away: no Revit restart.";
        }
        catch { /* the button text is a nicety */ }
    }

    static System.Windows.Media.ImageSource? Icon(string name)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return null;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; } // a missing icon must never stop the add-in loading
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
}
