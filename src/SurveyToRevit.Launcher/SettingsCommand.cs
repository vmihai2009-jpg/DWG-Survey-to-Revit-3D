using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SurveyToRevit.Launcher;

/// <summary>Opens the Survey to Revit settings window.</summary>
[Transaction(TransactionMode.ReadOnly)]
public class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => Show();

    internal static Result Show()
    {
        using var form = new SettingsForm();
        if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
        App.RefreshUpdater(form.Saved!);
        return Result.Succeeded;
    }
}
