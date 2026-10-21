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
                      "spot heights, links each plan to its level and creates concept walls. You see a summary before anything changes.",
        };
        panel.AddItem(button);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
}
