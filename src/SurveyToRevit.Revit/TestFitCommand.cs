using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Revit;

/// <summary>
/// Office Test-Fit: opens the workplace test-fit page on the floor of the active floor plan (or on the rooms
/// selected in it). The floor's outline, existing walls, columns and rooms are read as the plan shows them
/// (TestFitReader) and handed to the page, which lays out desks, meeting rooms and breakout inside them. Send to Revit on the page
/// closes the window and places the layout (TestFitBuilder); closing it without sending changes nothing.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class TestFitCommand : IExternalCommand
{
    const string Caption = "Office Test-Fit";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }
        if (uidoc!.ActiveView is not ViewPlan { ViewType: ViewType.FloorPlan, GenLevel: { } level } plan)
        {
            TaskDialog.Show(Caption, "Open the floor plan of the floor you want to test-fit, then click Office Test-Fit again.");
            return Result.Cancelled;
        }
        var floors = TestFitReader.Floors(doc);
        TestFitSite Read(Level l, ICollection<ElementId> selected)
        {
            var s = TestFitReader.Read(doc, l, TestFitReader.PlanOf(doc, l, plan), selected);
            s.LevelId = l.Id.Value.ToString();
            s.Floors.AddRange(floors.Select(f => (f.Id.Value.ToString(), f.Name)));
            return s;
        }
        TestFitSite site;
        try { site = Read(level, uidoc.Selection.GetElementIds()); }
        catch (Exception ex)
        {
            TaskDialog.Show(Caption, $"Could not read {level.Name}: {ex.Message}");
            return Result.Failed;
        }
        if (site.Faces.Count == 0)
        {
            TaskDialog.Show(Caption, $"There is no floor slab, room or wall on {level.Name} to lay out, in this model or its links. " +
                                     "Build the floor (Build from Survey) or draw its slab first.");
            return Result.Cancelled;
        }
        TestFitSite? Load(string id) =>
            long.TryParse(id, out var v) && doc.GetElement(new ElementId(v)) is Level l ? Read(l, Array.Empty<ElementId>()) : null;
        (string Layout, TestFitSite Site)? sent;
        try { sent = TestFitWindow.Open(site, Load, data.Application.MainWindowHandle); }
        catch (Exception ex)
        {
            TaskDialog.Show(Caption, "The test-fit window could not open: " + ex.Message);
            return Result.Failed;
        }
        if (sent == null) return Result.Succeeded; // looked at, nothing sent

        site = sent.Value.Site;
        if (doc.GetElement(new ElementId(long.Parse(site.LevelId))) is Level sentLevel) level = sentLevel;
        TestFitLayout layout;
        try { layout = TestFitLayout.Parse(sent.Value.Layout, site.Frame().OriginMm); }
        catch (Exception ex) { TaskDialog.Show(Caption, "The layout could not be read: " + ex.Message); return Result.Failed; }
        var builder = new TestFitBuilder(doc, level, site.StoreyHeightMm);
        builder.Build(layout);
        TaskDialog.Show(Caption, Summary(builder, layout, uidoc.ActiveView));
        return Result.Succeeded;
    }

    static string Summary(TestFitBuilder b, TestFitLayout layout, View view)
    {
        var lines = new List<string>
        {
            $"{layout.Desks} workstations on {layout.NiaM2:0} m² NIA placed on this floor.",
            $"Furniture: {b.Placed.Values.Sum()} family instances" + (b.Blocks > 0 ? $", {b.Blocks} plain blocks (no family found)" : "") + ".",
            $"Partitions: {b.Walls} walls, {b.Glazed} curtain walls, {b.Doors} doors; {b.Rooms} rooms.",
        };
        if (b.Placed.Count > 0) lines.Add("Families: " + string.Join(", ", b.Placed.Select(p => $"{p.Key} ({p.Value})")) + ".");
        if (b.OptionName != null) lines.Add($"It is in the design option '{b.OptionName}'. Make another option active and send again to keep both side by side.");
        else lines.Add("Tip: to keep several layouts side by side, add a 'Furniture Layout' set with Option 1, Option 2… in Manage > Design Options, make an option active and send again. Revit lets add-ins fill design options but not create them.");
        if (b.Removed > 0) lines.Add($"The last test-fit on this floor{(b.OptionName != null ? " in this option" : "")} was replaced ({b.Removed} elements).");
        var viewPhase = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsValueString();
        if (b.PhaseName != null && viewPhase != null && viewPhase != b.PhaseName)
            lines.Add($"It is on the '{b.PhaseName}' phase; this view shows '{viewPhase}', so set the view's phase to '{b.PhaseName}' to see it.");
        lines.Add("Undo removes it all in one step. Send to Revit again from Office Test-Fit to replace it.");
        if (b.Log.Count > 0) lines.Add("\n" + string.Join("\n", b.Log.Distinct().Take(14)));
        return string.Join("\n", lines);
    }
}
