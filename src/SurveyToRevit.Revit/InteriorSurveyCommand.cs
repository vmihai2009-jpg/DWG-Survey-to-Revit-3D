using DialogResult = System.Windows.Forms.DialogResult;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Interior;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Revit;

/// <summary>
/// Interior Survey: Build from Survey for a project that already holds the building's exterior (a surveyed façade
/// model, in this project or linked in). The survey is fitted onto the existing façade (the project's coordinates,
/// base points and levels are kept), the survey's façade walls, windows, shopfronts and roofs are left to the
/// existing model, and the interior (walls, doors, floors, ceilings, rooms, stairs) is built to meet it.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class InteriorSurveyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }

        var existing = ExistingReader.Read(doc);
        if (existing.Elements.Count == 0)
        {
            TaskDialog.Show("Interior Survey", "No exterior was found: this project and its linked models have no walls, windows, curtain walls or roofs " +
                "(other than ones Survey to Revit made). Open or link the exterior model first, then run Interior Survey again.");
            return Result.Cancelled;
        }

        IReadOnlyList<string> files;
        using (var start = new StartDialog(doc.Title))
        {
            if (start.ShowDialog() != DialogResult.OK || start.Files.Count == 0) return Result.Cancelled;
            files = start.Files;
        }
        try { var settings = UserSettings.Load(); settings.LastSurveyFolder = Path.GetDirectoryName(files[0]); settings.Save(); }
        catch { /* only a convenience for next time */ }
        var dwgs = files.Where(f => !SiteContextBuilder.IsSiteFile(f)).ToList();
        if (dwgs.Count == 0) { message = "Select the survey DWGs."; return Result.Failed; }

        SurveyModel model;
        InteriorPlacement fit;
        try
        {
            var first = SurveyAnalyzer.Analyze(dwgs);
            if (first.Levels.Count == 0)
            {
                var report = BuildFromSurveyCommand.SaveText("analysis", SurveyReport.Build(first));
                TaskDialog.Show("Interior Survey", "No usable floor plans were found. See the report:\n" + report);
                BuildFromSurveyCommand.OpenFile(report);
                return Result.Cancelled;
            }
            fit = InteriorFit.Fit(first, existing);
            if (!fit.Good)
            {
                var ask = new TaskDialog("Interior Survey")
                {
                    MainInstruction = "The survey does not line up well with the existing model",
                    MainContent = string.Join("\n", fit.Notes) + $"\n\nBest fit: {fit.Score:P0} of the existing façade on the survey's lines (at least {InteriorFit.MinScore:P0} expected). " +
                                  "Is the right exterior model open or linked, and are these the right drawings?",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                };
                ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Build at the best fit anyway", "Check the result against the façade.");
                if (ask.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
            }
            // read again in the project's own coordinates, so walls are squared to the building as it sits here
            model = SurveyAnalyzer.Analyze(dwgs, frame: fit.Frame);
        }
        catch (Exception ex) { message = "Could not analyse the drawings: " + ex.Message; return Result.Failed; }

        var split = InteriorSplit.Apply(model, existing, fit.DatumMm);
        var snapped = InteriorFit.SnapLevels(model, existing.Levels, fit.DatumMm);
        var reportPath = BuildFromSurveyCommand.SaveText("analysis", SurveyReport.Build(model) + "\nINTERIOR SURVEY\n" +
            string.Join("\n", fit.Notes.Concat(split).Concat(snapped).Select(l => "  - " + l)) + "\n");

        BuildOptions options;
        while (true)
        {
            using var dialog = new SurveyDialog(model, model.Skipped.Count);
            dialog.Text = "Interior Survey";
            var result = dialog.ShowDialog();
            if (dialog.OpenReport) { BuildFromSurveyCommand.OpenFile(reportPath); continue; }
            if (result != DialogResult.OK) return Result.Cancelled;
            options = dialog.Options;
            break;
        }
        if (!model.Levels.Any(l => l.Include)) return Result.Cancelled;
        // the exterior model has the roofs; no surroundings either (they belong with the exterior)
        options.Roofs = false;
        options.Site = null;
        options.Interior = true;
        options.InteriorDatumMm = fit.DatumMm;
        options.InteriorSharedPlacement = existing.SharedFrames.Contains(fit.Frame);
        options.InteriorTurnDeg = -fit.Frame.RotationDeg;

        var builder = new ModelBuilder(doc, model);
        builder.Log.AddRange(fit.Notes.Concat(split).Concat(snapped).Select(l => "Interior: " + l));
        try { builder.Run(options); }
        catch (Exception ex)
        {
            var errorPath = BuildFromSurveyCommand.SaveText("error", ex + "\n\nLOG\n" + string.Join("\n", builder.Log));
            message = $"{ex.Message}\nNothing was changed. Details for support: {errorPath}";
            return Result.Failed;
        }

        string? reviewPath = null;
        ReviewList? review = null;
        if (model.Checks.Count > 0)
        {
            review = ReviewList.From(doc.Title, model.Checks, model.Levels);
            try { reviewPath = ReviewList.PathFor(UserSettings.Load().Reports, doc.Title); review.Save(reviewPath); }
            catch
            {
                try { reviewPath = ReviewList.PathFor(UserSettings.DefaultReports, doc.Title); review.Save(reviewPath); }
                catch { reviewPath = null; }
            }
        }
        var logPath = BuildFromSurveyCommand.SaveText("interior", SurveyReport.Build(model) + "\nWHAT WAS DONE IN REVIT\n" + string.Join("\n", builder.Log.Select(l => "  - " + l)));
        var done = new TaskDialog("Interior Survey")
        {
            MainInstruction = "Done",
            MainContent = string.Join("\n", builder.Log.Where(l => l.StartsWith("Interior: Survey placed") || model.Levels.Any(x => l.StartsWith(x.Name + ":")) || l.StartsWith("WARNING")).Take(18)) + "\n\nFull log:\n" + logPath,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open full log");
        if (review != null && reviewPath != null)
            done.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, $"Open the review list ({review.Items.Count} places to look at)");
        var pick = done.Show();
        if (pick == TaskDialogResult.CommandLink1) BuildFromSurveyCommand.OpenFile(logPath);
        else if (pick == TaskDialogResult.CommandLink2 && review != null && reviewPath != null) ReviewListCommand.Open(review, reviewPath);
        return Result.Succeeded;
    }
}

/// <summary>
/// Reads the exterior already in a project, for <see cref="InteriorFit"/> and <see cref="InteriorSplit"/>: the walls,
/// windows, doors, curtain panels and mullions, roofs and columns of this model and of its loaded links, as triangles
/// in this project's internal coordinates (mm); its levels; and its shared coordinates.
/// </summary>
internal static class ExistingReader
{
    static readonly (BuiltInCategory Category, string Name)[] Categories =
    {
        (BuiltInCategory.OST_Walls, "Walls"), (BuiltInCategory.OST_Windows, "Windows"), (BuiltInCategory.OST_Doors, "Doors"),
        (BuiltInCategory.OST_CurtainWallPanels, "CurtainWallPanels"), (BuiltInCategory.OST_CurtainWallMullions, "CurtainWallMullions"),
        (BuiltInCategory.OST_Roofs, "Roofs"), (BuiltInCategory.OST_Columns, "Columns"), (BuiltInCategory.OST_StructuralColumns, "Columns"),
    };

    static double Mm(double ft) => UnitConversion.FeetToMm(ft);

    public static ExistingBuilding Read(Document doc)
    {
        var elements = new List<ExistingElement>();
        var levels = new List<ExistingLevel>();
        Collect(doc, Transform.Identity, elements, ours: true);
        foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()) levels.Add(new ExistingLevel(l.Name, Mm(l.Elevation), true));
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var linked = link.GetLinkDocument();
            if (linked == null) continue;
            var t = link.GetTotalTransform();
            Collect(linked, t, elements, ours: false);
            foreach (var l in new FilteredElementCollector(linked).OfClass(typeof(Level)).Cast<Level>())
                levels.Add(new ExistingLevel(linked.Title + ": " + l.Name, Mm(t.OfPoint(new XYZ(0, 0, l.Elevation)).Z), false));
        }

        // the project's shared coordinates, read as a survey placement (both turn directions: the fit keeps whichever agrees)
        var frames = new List<SurveyFrame>();
        double? datum = null;
        try
        {
            var pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            var at = new Vec2(Mm(pp.EastWest), Mm(pp.NorthSouth));
            double deg = pp.Angle * 180 / Math.PI;
            frames.Add(new SurveyFrame(at, deg));
            if (Math.Abs(deg) > 1e-9) frames.Add(new SurveyFrame(at, -deg));
            datum = Mm(pp.Elevation);
        }
        catch { /* no shared coordinates: the fit alone places the survey */ }
        return new ExistingBuilding(elements, levels) { SharedFrames = frames, SharedDatumMm = datum };
    }

    static void Collect(Document d, Transform t, List<ExistingElement> into, bool ours)
    {
        var options = new Options { DetailLevel = ViewDetailLevel.Medium, ComputeReferences = false, IncludeNonVisibleObjects = false };
        foreach (var (category, name) in Categories)
            foreach (var e in new FilteredElementCollector(d).OfCategory(category).WhereElementIsNotElementType())
            {
                // what Survey to Revit built in an earlier run is not the exterior
                if (ours && e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(ModelBuilder.Tag + "|") == true) continue;
                var tris = new List<double>();
                try { if (e.get_Geometry(options) is GeometryElement g) Add(g, t, tris); }
                catch { continue; }
                if (tris.Count >= 9) into.Add(new ExistingElement(name, e.Name, tris.ToArray()));
            }
    }

    static void Add(GeometryElement g, Transform t, List<double> tris)
    {
        foreach (var o in g)
            switch (o)
            {
                case Solid s when s.Faces.Size > 0:
                    foreach (Face f in s.Faces)
                    {
                        Mesh m;
                        try { m = f.Triangulate(); } catch { continue; }
                        if (m != null) Add(m, t, tris);
                    }
                    break;
                case Mesh m:
                    Add(m, t, tris);
                    break;
                case GeometryInstance gi:
                    Add(gi.GetInstanceGeometry(), t, tris);
                    break;
            }
    }

    static void Add(Mesh m, Transform t, List<double> tris)
    {
        for (int i = 0; i < m.NumTriangles; i++)
        {
            var tri = m.get_Triangle(i);
            for (int k = 0; k < 3; k++)
            {
                var p = t.OfPoint(tri.get_Vertex(k));
                tris.Add(Mm(p.X)); tris.Add(Mm(p.Y)); tris.Add(Mm(p.Z));
            }
        }
    }
}
