using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SurveyToRevit.Core;

namespace SurveyToRevit.Revit;

/// <summary>
/// For anything the automatic reading missed: in a plan view, pick the two face lines of
/// a wall in the linked survey (two straight lines, two arcs, or an arc and a straight line
/// for a curved façade with straight window recesses). A wall is built between them at the
/// measured thickness (snapped to the Concept Wall types), on this view's level, up to the
/// next level, in the Existing phase. Repeat for more walls; Esc to finish. Picked walls are
/// kept when Build from Survey is run again.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class WallFromLinesCommand : IExternalCommand
{
    public const string PickedTag = ModelBuilder.Tag + "|PICKED|";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }
        if (doc.ActiveView is not ViewPlan { GenLevel: Level level } view) { message = "Open a floor plan with the linked survey first."; return Result.Failed; }

        int made = 0;
        while (true)
        {
            var first = Pick(uidoc!, view, "Wall from lines: pick the first face of the wall in the survey (Esc to finish)");
            if (first == null) break;
            var second = Pick(uidoc!, view, "Wall from lines: pick the other face of the same wall (Esc to finish)");
            if (second == null) break;
            try
            {
                if (Build(doc, level, first, second) is string problem) TaskDialog.Show("Wall from lines", problem);
                else made++;
            }
            catch (Exception ex) { TaskDialog.Show("Wall from lines", "That wall could not be made: " + ex.Message); }
        }
        return made > 0 ? Result.Succeeded : Result.Cancelled;
    }

    sealed class LinkFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is ImportInstance;
        public bool AllowReference(Reference r, XYZ p) => true;
    }

    /// <summary>The survey line under the pick: the nearest curve of the linked DWG to the picked point.</summary>
    static Curve? Pick(UIDocument uidoc, View view, string prompt)
    {
        Reference r;
        try { r = uidoc.Selection.PickObject(ObjectType.PointOnElement, new LinkFilter(), prompt); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
        var inst = (ImportInstance)uidoc.Document.GetElement(r);
        var p = r.GlobalPoint;
        Curve? best = null; double bestD = double.MaxValue;
        void Visit(GeometryElement g)
        {
            foreach (var o in g)
            {
                if (o is GeometryInstance gi) { Visit(gi.GetInstanceGeometry()); continue; }
                var curves = o switch
                {
                    Curve c => new List<Curve> { c },
                    PolyLine pl => pl.GetCoordinates().Zip(pl.GetCoordinates().Skip(1), (a, b) => a.DistanceTo(b) > 1e-6 ? (Curve)Line.CreateBound(a, b) : null).OfType<Curve>().ToList(),
                    _ => new List<Curve>(),
                };
                foreach (var c in curves)
                {
                    if (!c.IsBound) continue;
                    var flat = Flatten(c, p.Z);
                    var hit = flat?.Project(p);
                    if (hit != null && hit.Distance < bestD) { bestD = hit.Distance; best = flat; }
                }
            }
        }
        var geom = inst.get_Geometry(new Options { View = view });
        if (geom != null) Visit(geom);
        return bestD < UnitConversion.MmToFeet(300) ? best : null;
    }

    static Curve? Flatten(Curve c, double z)
    {
        XYZ F(XYZ q) => new(q.X, q.Y, z);
        try
        {
            return c switch
            {
                Line l => Line.CreateBound(F(l.GetEndPoint(0)), F(l.GetEndPoint(1))),
                Arc a => Arc.Create(F(a.GetEndPoint(0)), F(a.GetEndPoint(1)), F(a.Evaluate(0.5, true))),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>Builds the wall; returns a reason if the two lines do not make one.</summary>
    static string? Build(Document doc, Level level, Curve a, Curve b)
    {
        double Mm(double ft) => UnitConversion.FeetToMm(ft);
        double Ft(double mm) => UnitConversion.MmToFeet(mm);
        var tol = Tolerances.Default;
        Curve centre;
        double thickness;
        if (a is Line la && b is Line lb)
        {
            var u = la.Direction;
            if (Math.Abs(u.CrossProduct(lb.Direction).Z) > Math.Sin(3 * Math.PI / 180)) return "Those two lines are not parallel, so they are not the two faces of one wall.";
            var n = new XYZ(-u.Y, u.X, 0);
            double off = (lb.GetEndPoint(0) - la.GetEndPoint(0)).DotProduct(n);
            thickness = Math.Abs(Mm(off));
            // the stretch where the two faces run side by side
            var o = la.GetEndPoint(0);
            double a0 = 0, a1 = la.Length;
            double b0 = (lb.GetEndPoint(0) - o).DotProduct(u), b1 = (lb.GetEndPoint(1) - o).DotProduct(u);
            double s = Math.Max(a0, Math.Min(b0, b1)), e = Math.Min(a1, Math.Max(b0, b1));
            if (e - s < Ft(100)) { s = a0; e = a1; }
            centre = Line.CreateBound(o + u * s + n * (off / 2), o + u * e + n * (off / 2));
        }
        else if (a is Arc || b is Arc)
        {
            var arc = (Arc)(a is Arc ? a : b);
            var other = a is Arc ? b : a;
            double side;
            if (other is Arc oa)
            {
                if (oa.Center.DistanceTo(arc.Center) > Ft(300)) return "Those two curves do not share a centre, so they are not the two faces of one curved wall.";
                side = oa.Radius - arc.Radius;
            }
            else
            {
                // a straight inner face (window recess): the wall reaches from the arc to that line
                var mid = arc.Evaluate(0.5, true);
                var hit = other.Project(mid);
                side = (hit.XYZPoint - arc.Center).GetLength() - arc.Radius;
                if (Math.Abs(side) < 1e-6) return "That line touches the curve; pick the wall's other face.";
                side = Math.Sign(side) * hit.Distance;
            }
            thickness = Math.Abs(Mm(side));
            var ends = new[] { arc.GetEndPoint(0), arc.GetEndPoint(1), arc.Evaluate(0.5, true) }
                .Select(q => arc.Center + (q - arc.Center).Normalize() * (arc.Radius + side / 2)).ToArray();
            centre = Arc.Create(ends[0], ends[1], ends[2]);
        }
        else return "Pick straight lines or arcs from the linked survey.";

        if (thickness < tol.MinWallThickness || thickness > tol.MaxWallThickness + 200)
            return $"The lines are {thickness:0} mm apart: too far apart or too close for one wall. Pick the two faces of the same wall.";
        double type = WallDetector.SnapThickness(thickness, tol.WallTypeSizes);

        using var t = new Transaction(doc, "Wall from survey lines");
        t.Start();
        var wallType = ModelBuilder.WallTypeFor(doc, type);
        var above = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Where(l => l.Elevation > level.Elevation + 1e-6).OrderBy(l => l.Elevation).FirstOrDefault();
        var wall = Wall.Create(doc, centre, wallType.Id, level.Id, above != null ? above.Elevation - level.Elevation : Ft(3000), 0, false, false);
        if (above != null) wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(above.Id);
        wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{PickedTag}{thickness:0} mm measured");
        var existing = doc.Phases.Cast<Phase>().FirstOrDefault(p => p.Name.Contains("Existing", StringComparison.OrdinalIgnoreCase)) ?? doc.Phases.Cast<Phase>().FirstOrDefault();
        var phase = wall.get_Parameter(BuiltInParameter.PHASE_CREATED);
        if (existing != null && phase != null && !phase.IsReadOnly) phase.Set(existing.Id);
        t.Commit();
        return null;
    }
}
