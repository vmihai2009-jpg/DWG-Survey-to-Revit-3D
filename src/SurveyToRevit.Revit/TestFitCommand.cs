using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Revit;

/// <summary>
/// Office Test-Fit: opens the workplace test-fit page on the floor of the active floor plan. The floor's
/// outline (its slabs, or failing that its rooms or walls), existing walls, columns and rooms are read and
/// handed to the page, which lays out desks, meeting rooms and breakout inside them. Nothing in the model
/// is changed.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class TestFitCommand : IExternalCommand
{
    const string Caption = "Office Test-Fit";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }
        if (uidoc!.ActiveView is not ViewPlan { ViewType: ViewType.FloorPlan, GenLevel: { } level })
        {
            TaskDialog.Show(Caption, "Open the floor plan of the floor you want to test-fit, then click Office Test-Fit again.");
            return Result.Cancelled;
        }
        TestFitSite site;
        try { site = TestFitReader.Read(doc, level); }
        catch (Exception ex)
        {
            TaskDialog.Show(Caption, $"Could not read {level.Name}: {ex.Message}");
            return Result.Failed;
        }
        if (site.Faces.Count == 0)
        {
            TaskDialog.Show(Caption, $"There is no floor slab, room or wall on {level.Name} to lay out. " +
                                     "Build the floor (Build from Survey) or draw its slab first.");
            return Result.Cancelled;
        }
        try { TestFitWindow.Open(site, data.Application.MainWindowHandle); }
        catch (Exception ex)
        {
            TaskDialog.Show(Caption, "The test-fit window could not open: " + ex.Message);
            return Result.Failed;
        }
        return Result.Succeeded;
    }
}

/// <summary>Reads a floor of the model as the test-fit page needs it (mm, model plan coordinates).</summary>
internal static class TestFitReader
{
    static double Mm(double ft) => UnitConversion.FeetToMm(ft);
    static double Ft(double mm) => UnitConversion.MmToFeet(mm);
    static Vec2 P(XYZ p) => new(Mm(p.X), Mm(p.Y));

    // Objects that stand on the floor and must stay clear of furniture.
    static readonly BuiltInCategory[] Solid =
    [
        BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_Stairs, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_SpecialityEquipment,
    ];

    public static TestFitSite Read(Document doc, Level level)
    {
        var z0 = level.ProjectElevation;
        var above = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Select(l => l.ProjectElevation).Where(e => e > z0 + Ft(1500)).DefaultIfEmpty(z0 + Ft(3000)).Min();
        var site = new TestFitSite
        {
            Title = $"{doc.Title} · {level.Name}",
            Level = level.Name,
            StoreyHeightMm = Math.Clamp(Mm(above - z0), 2400, 6000),
            NorthDegrees = North(doc),
            Source = "floor slabs",
        };

        // Rooms on this floor: names for the plan, and stairs/lifts/WCs become core spaces.
        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
            .OfType<Room>().Where(r => r.LevelId == level.Id && r.Area > 0).ToList();
        var boundary = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
        var roomLoops = new List<(string Name, List<IReadOnlyList<Vec2>> Loops)>();
        foreach (var room in rooms)
        {
            try
            {
                var loops = room.GetBoundarySegments(boundary)?
                    .Select(l => (IReadOnlyList<Vec2>)Loop(l.Select(s => s.GetCurve()))).Where(l => l.Count >= 3).ToList();
                if (loops is not { Count: > 0 }) continue;
                var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                roomLoops.Add((name, loops));
                site.Rooms.Add(new SiteRoom(name, loops[0]));
            }
            catch { /* a room Revit cannot bound is left out */ }
        }

        // The floor outline: its slabs, else its rooms, else the box round its walls.
        foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
                     .Where(f => f.LevelId == level.Id && !Demolished(f)))
        {
            foreach (var r in HostObjectUtils.GetTopFaces(floor))
            {
                if (floor.GetGeometryObjectFromReference(r) is not Face face) continue;
                var loops = face.GetEdgesAsCurveLoops().Select(l => Loop(l)).Where(l => l.Count >= 3)
                    .OrderByDescending(l => Math.Abs(Area(l))).Cast<IReadOnlyList<Vec2>>().ToList();
                if (loops.Count > 0 && Math.Abs(Area(loops[0])) > 1e6) site.Faces.Add(loops);
            }
        }
        if (site.Faces.Count == 0 && roomLoops.Count > 0)
        {
            site = With(site, "rooms");
            foreach (var (_, loops) in roomLoops) site.Faces.Add(loops);
        }

        // Existing walls cut 1 m above the floor.
        var cut = Band(z0 + Ft(1000));
        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).WherePasses(cut).Cast<Wall>()
            .Where(w => !Demolished(w)).ToList();
        foreach (var wall in walls)
        {
            if (wall.Location is not LocationCurve { Curve: { } curve }) continue;
            var pts = curve.Tessellate().Select(P).ToList();
            for (var i = 0; i + 1 < pts.Count; i++) site.Walls.Add(new SiteWall(pts[i], pts[i + 1], Mm(wall.Width)));
        }
        if (site.Faces.Count == 0 && site.Walls.Count > 0)
        {
            site = With(site, "walls");
            double x0 = site.Walls.Min(w => Math.Min(w.A.X, w.B.X)), x1 = site.Walls.Max(w => Math.Max(w.A.X, w.B.X));
            double y0 = site.Walls.Min(w => Math.Min(w.A.Y, w.B.Y)), y1 = site.Walls.Max(w => Math.Max(w.A.Y, w.B.Y));
            site.Faces.Add([new Vec2[] { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) }]);
        }
        if (site.Faces.Count == 0) return site;

        // Keep what stands on this floor: walls and objects over the outline (not neighbours on a context workset).
        var all = site.Faces.SelectMany(f => f[0]).ToList();
        double fx0 = all.Min(p => p.X) - 1000, fx1 = all.Max(p => p.X) + 1000, fy0 = all.Min(p => p.Y) - 1000, fy1 = all.Max(p => p.Y) + 1000;
        bool Over(Vec2 p) => p.X >= fx0 && p.X <= fx1 && p.Y >= fy0 && p.Y <= fy1;
        site.Walls.RemoveAll(w => !Over(w.A) && !Over(w.B));
        foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(Solid)).WhereElementIsNotElementType().WherePasses(cut))
        {
            if (Demolished(e) || e.get_BoundingBox(null) is not { } box) continue;
            Vec2 a = P(box.Min), b = P(box.Max);
            if (b.X - a.X > 15000 || b.Y - a.Y > 15000 || b.X < fx0 || a.X > fx1 || b.Y < fy0 || a.Y > fy1) continue;
            site.Columns.Add((a, b));
        }
        return site;
    }

    static TestFitSite With(TestFitSite s, string source)
    {
        var n = new TestFitSite { Title = s.Title, Level = s.Level, StoreyHeightMm = s.StoreyHeightMm, NorthDegrees = s.NorthDegrees, Source = source };
        n.Rooms.AddRange(s.Rooms);
        n.Walls.AddRange(s.Walls);
        return n;
    }

    /// <summary>Elements crossing a horizontal plane at this height (internal feet).</summary>
    static BoundingBoxIntersectsFilter Band(double z) =>
        new(new Outline(new XYZ(-1e6, -1e6, z - Ft(20)), new XYZ(1e6, 1e6, z + Ft(20))));

    static List<Vec2> Loop(IEnumerable<Curve> curves)
    {
        var pts = new List<Vec2>();
        foreach (var c in curves)
        {
            var t = c.Tessellate();
            for (var i = 0; i + 1 < t.Count; i++) pts.Add(P(t[i]));
        }
        return pts;
    }

    static double Area(IReadOnlyList<Vec2> l)
    {
        double a = 0;
        for (var i = 0; i < l.Count; i++) a += l[i].Cross(l[(i + 1) % l.Count]);
        return a / 2;
    }

    static bool Demolished(Element e) =>
        e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId() is { } id && id != ElementId.InvalidElementId;

    /// <summary>True north on the plan, in degrees clockwise from the top (the model's +Y).</summary>
    static double North(Document doc)
    {
        try
        {
            var n = doc.ActiveProjectLocation.GetTotalTransform().Inverse.OfVector(XYZ.BasisY);
            return Math.Atan2(n.X, n.Y) * 180 / Math.PI;
        }
        catch { return 0; }
    }
}
