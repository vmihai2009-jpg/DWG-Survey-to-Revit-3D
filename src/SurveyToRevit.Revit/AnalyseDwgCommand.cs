using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SurveyToRevit.Core;

namespace SurveyToRevit.Revit;

/// <summary>
/// Milestone 1/2 command: pick a DWG in the model, read its linework, run wall
/// recognition and write a text report. Read-only, so it cannot alter the model.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class AnalyseDwgCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
        var doc = uidoc.Document;

        var import = PickImport(uidoc);
        if (import == null) return Result.Cancelled;

        var name = (doc.GetElement(import.GetTypeId()) as CADLinkType)?.Name
                   ?? doc.GetElement(import.GetTypeId())?.Name ?? "DWG";

        var reader = CadGeometryReader.Read(doc, import);
        if (reader.Segments.Count == 0)
        {
            TaskDialog.Show("Survey to Revit", $"No straight linework was found in '{name}'.\n\n" +
                $"Arcs: {reader.Arcs}, other objects: {reader.Other}.\nCheck that the DWG is visible in this view and not empty.");
            return Result.Succeeded;
        }

        var walls = WallDetector.Detect(reader.Segments);
        var t = import.GetTotalTransform();
        var notes = new List<string>
        {
            $"DWG placed with origin at ({UnitConversion.FeetToMm(t.Origin.X):0}, {UnitConversion.FeetToMm(t.Origin.Y):0}, {UnitConversion.FeetToMm(t.Origin.Z):0}) mm, " +
            $"rotation {Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180 / Math.PI:0.##} deg, scale {t.Scale:0.####}.",
            $"Linework elevation range: {UnitConversion.FeetToMm(reader.MinZ):0} to {UnitConversion.FeetToMm(reader.MaxZ):0} mm (Z is ignored; plan only).",
            $"Import is {(import.IsLinked ? "linked" : "imported")}. Units come from the DWG import settings; check the extents above look right.",
            $"Other objects not read: {reader.Other}. Arcs are counted but not yet used for walls.",
        };
        var text = Report.Build(name, reader.Segments, reader.Arcs, walls, notes);
        var path = SaveReport(name, text);

        var dialog = new TaskDialog("Survey to Revit")
        {
            MainInstruction = $"{walls.Walls.Count} wall candidates found in '{name}'",
            MainContent = $"Segments read: {reader.Segments.Count}\n" +
                          $"Wall types: {string.Join(", ", walls.Walls.Select(w => w.TypeName).Distinct().OrderBy(n => n))}\n" +
                          $"Lines not explained: {walls.UnpairedSegments.Count}\n\n" +
                          "Nothing was created or changed in the model.\nReport saved to:\n" + path,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open report");
        if (dialog.Show() == TaskDialogResult.CommandLink1)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Result.Succeeded;
    }

    static ImportInstance? PickImport(UIDocument uidoc)
    {
        var doc = uidoc.Document;
        var selected = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<ImportInstance>().FirstOrDefault();
        if (selected != null) return selected;

        var all = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        if (all.Count == 0)
        {
            TaskDialog.Show("Survey to Revit", "No linked or imported DWG found. Use Insert > Link CAD first.");
            return null;
        }
        if (all.Count == 1) return all[0];

        try
        {
            var r = uidoc.Selection.PickObject(ObjectType.Element, new ImportFilter(), "Select the DWG survey to analyse");
            return doc.GetElement(r) as ImportInstance;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
    }

    static string SaveReport(string name, string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SurveyToRevit");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(dir, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    sealed class ImportFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is ImportInstance;
        public bool AllowReference(Reference r, XYZ p) => false;
    }
}
