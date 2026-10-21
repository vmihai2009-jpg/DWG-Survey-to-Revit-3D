using System.Reflection;
using Autodesk.Revit.UI;

namespace SurveyToRevit.Revit;

/// <summary>Adds a "Survey to Revit" panel to the shared Barr Gazetas ribbon tab when Revit starts.</summary>
public class App : IExternalApplication
{
    // Shared with other Barr Gazetas add-ins; the name must match exactly (case-sensitive).
    const string TabName = "Barr Gazetas";

    public Result OnStartup(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already created by another add-in */ }

        var panel = app.CreateRibbonPanel(TabName, "Survey to Revit");
        var button = new PushButtonData("BuildFromSurvey", "Build from\nSurvey", Assembly.GetExecutingAssembly().Location,
            typeof(BuildFromSurveyCommand).FullName)
        {
            ToolTip = "Select survey DWGs. Sets project coordinates and north from the survey, creates levels from the " +
                      "spot heights, links each plan to its level and builds a concept model: walls, windows, floors, ceilings. " +
                      "You choose floors and options in a window before anything changes.",
            Image = Icon("icon16.png"),
            LargeImage = Icon("icon32.png"),
        };
        panel.AddItem(button);
        return Result.Succeeded;
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
