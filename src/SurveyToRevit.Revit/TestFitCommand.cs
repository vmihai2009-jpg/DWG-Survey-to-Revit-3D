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
        var picked = uidoc!.Selection.GetElementIds();
        // on a sheet: the floor plan placed on it (the selected viewport's, else the largest)
        var active = uidoc.ActiveView is ViewSheet onSheet ? PlanOnSheet(doc, onSheet, picked) : uidoc.ActiveView;
        if (uidoc.ActiveView is ViewSheet) picked = new List<ElementId>();
        if (active is not ViewPlan { ViewType: ViewType.FloorPlan, GenLevel: { } level } plan)
        {
            TaskDialog.Show(Caption, "Open the floor plan of the floor you want to test-fit, or a sheet with that plan on it, then click Office Test-Fit again.");
            return Result.Cancelled;
        }
        // the floor list: sheets with a floor plan on them (read as that plan, with what is drawn over it), then levels
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder && PlanOnSheet(doc, s, Array.Empty<ElementId>()) != null).OrderBy(s => s.SheetNumber).ToList();
        var floors = sheets.Select(s => ("s:" + s.Id.Value, $"{s.SheetNumber} – {s.Name}"))
            .Concat(TestFitReader.Floors(doc).Select(f => (f.Id.Value.ToString(), f.Name))).ToList();
        TestFitSite Read(ViewPlan v, ICollection<ElementId> selected, string pick)
        {
            var l = v.GenLevel;
            var s = TestFitReader.Read(doc, l, v, selected);
            s.LevelId = l.Id.Value.ToString();
            s.Pick = pick;
            s.ViewId = v.Id.Value.ToString();
            s.Floors.AddRange(floors);
            try { TestFitAssets.Catalog(doc, s); } catch { /* the Assets tab stays empty: the add-in picks the families */ }
            return s;
        }
        // started on a sheet, or on a plan that is on one: the list shows that sheet
        var onSheetId = uidoc.ActiveView is ViewSheet vs ? vs.Id
            : new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(vp => vp.ViewId == plan.Id)?.SheetId;
        var first = onSheetId != null && sheets.Any(s => s.Id == onSheetId) ? "s:" + onSheetId.Value : level.Id.Value.ToString();
        TestFitSite site;
        try { site = Read(plan, picked, first); }
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
        TestFitSite? Load(string id)
        {
            if (id.StartsWith("s:") && long.TryParse(id[2..], out var sv) && doc.GetElement(new ElementId(sv)) is ViewSheet sh)
                return PlanOnSheet(doc, sh, Array.Empty<ElementId>()) is ViewPlan sp ? Read(sp, Array.Empty<ElementId>(), id) : null;
            return long.TryParse(id, out var v) && doc.GetElement(new ElementId(v)) is Level l && TestFitReader.PlanOf(doc, l, plan) is { } lp
                ? Read(lp, Array.Empty<ElementId>(), id) : null;
        }
        (string Layout, TestFitSite Site)? sent;
        string Measure(string id) => TestFitAssets.Measure(doc, level, id);
        try { sent = TestFitWindow.Open(site, Load, Measure, data.Application.MainWindowHandle); }
        catch (Exception ex)
        {
            TaskDialog.Show(Caption, "The test-fit window could not open: " + ex.Message);
            return Result.Failed;
        }
        if (sent == null) return Result.Succeeded; // looked at, nothing sent

        site = sent.Value.Site;
        if (doc.GetElement(new ElementId(long.Parse(site.LevelId))) is Level sentLevel) level = sentLevel;
        if (long.TryParse(site.ViewId, out var sentView) && doc.GetElement(new ElementId(sentView)) is ViewPlan sentPlan) plan = sentPlan;
        TestFitLayout layout;
        try { layout = TestFitLayout.Parse(sent.Value.Layout, site.Frame().OriginMm); }
        catch (Exception ex) { TaskDialog.Show(Caption, "The layout could not be read: " + ex.Message); return Result.Failed; }
        var builder = new TestFitBuilder(doc, level, site.StoreyHeightMm, plan);
        builder.Build(layout);
        var sheet = layout.Sheet ? TestFitSheet.Make(doc, level, layout, site, builder.OptionName, builder.PhaseId, builder.Log) : null;
        TaskDialog.Show(Caption, Summary(builder, layout, uidoc.ActiveView, sheet));
        return Result.Succeeded;
    }

    /// <summary>The floor plan on a sheet to test-fit: the viewport selected, else the largest floor plan on it.</summary>
    static View? PlanOnSheet(Document doc, ViewSheet sheet, ICollection<ElementId> selected)
    {
        var plans = sheet.GetAllViewports().Select(doc.GetElement).OfType<Viewport>()
            .Select(vp => (vp, view: doc.GetElement(vp.ViewId) as ViewPlan)).Where(x => x.view is { ViewType: ViewType.FloorPlan }).ToList();
        var chosen = plans.FirstOrDefault(x => selected.Contains(x.vp.Id));
        if (chosen.view != null) return chosen.view;
        return plans.OrderByDescending(x => { var o = x.vp.GetBoxOutline(); return (o.MaximumPoint.X - o.MinimumPoint.X) * (o.MaximumPoint.Y - o.MinimumPoint.Y); })
            .Select(x => x.view).FirstOrDefault();
    }

    static string Summary(TestFitBuilder b, TestFitLayout layout, View view, string? sheet)
    {
        var lines = new List<string>
        {
            $"{layout.Desks} workstations on {layout.NiaM2:0} m² NIA placed on this floor.",
            $"Furniture: {b.Placed.Values.Sum()} family instances" + (b.Blocks > 0 ? $", {b.Blocks} plain blocks (no family found)" : "") + ".",
            $"Partitions: {b.Walls} walls, {b.Glazed} curtain walls, {b.Doors} doors; {b.Rooms} rooms.",
        };
        if (b.Grouped > 0) lines.Add($"Grouped: {b.Grouped} groups (one per workstation, meeting room, breakout and reception set).");
        if (sheet != null) lines.Add($"Study sheet: {sheet}, with its zoning plan, 3D view and summary.");
        if (sheet != null && b.OptionName != null) lines.Add("Revit shows the primary option in new views: to see this option on the sheet, set Visibility/Graphics > Design Options in its plan and 3D view.");
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
