using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SurveyToRevit.Launcher;

// Ribbon buttons for the engine's tools. Each click hands over to the installed engine, so these never change.

[Transaction(TransactionMode.Manual)]
public class BuildFromSurvey : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        Engine.Run("SurveyToRevit.Revit.BuildFromSurveyCommand", data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class WallFromLines : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        Engine.Run("SurveyToRevit.Revit.WallFromLinesCommand", data, ref message, elements);
}
