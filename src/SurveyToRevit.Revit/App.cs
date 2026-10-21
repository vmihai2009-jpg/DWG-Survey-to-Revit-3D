using System.Reflection;
using Autodesk.Revit.UI;

namespace SurveyToRevit.Revit;

/// <summary>Adds the "Survey to Revit" ribbon tab when Revit starts.</summary>
public class App : IExternalApplication
{
    const string TabName = "Survey to Revit";

    public Result OnStartup(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

        var panel = app.CreateRibbonPanel(TabName, "Survey");
        var button = new PushButtonData("AnalyseDwg", "Analyse\nDWG", Assembly.GetExecutingAssembly().Location,
            typeof(AnalyseDwgCommand).FullName)
        {
            ToolTip = "Reads the linework of a linked or imported DWG and reports the walls it recognises. " +
                      "Read-only: nothing is created or changed in the model.",
        };
        panel.AddItem(button);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
}
