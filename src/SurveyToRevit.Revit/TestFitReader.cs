using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Revit;

/// <summary>
/// Reads a floor of the model as the test-fit page needs it (mm, model plan coordinates), the way its floor
/// plan shows it: only what that view shows (its phase, worksets, hidden elements), cut at the view's cut
/// plane, and including linked models (a survey or an exterior model is often a link).
/// The floor outline is, in order: the rooms selected before clicking; the rooms placed on the floor; the
/// spaces its walls enclose (found by Revit's own room tool in a transaction that is undone); its slabs;
/// the box round its walls.
/// </summary>
internal static class TestFitReader
{
    static double Mm(double ft) => UnitConversion.FeetToMm(ft);
    static double Ft(double mm) => UnitConversion.MmToFeet(mm);

    // Objects that stand on the floor and must stay clear of furniture.
    static readonly BuiltInCategory[] Solid =
    [
        BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_Stairs, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_SpecialityEquipment,
    ];

    /// <summary>A model to read: the project itself, or a link with its placement.</summary>
    sealed record Source(Document Doc, Transform Place, string Name, bool Host);

    public static TestFitSite Read(Document doc, Level level, ViewPlan? view, ICollection<ElementId> selected)
    {
        view ??= PlanOf(doc, level, null);
        var z0 = level.ProjectElevation;
        var above = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Select(l => l.ProjectElevation).Where(e => e > z0 + Ft(1500)).DefaultIfEmpty(z0 + Ft(3000)).Min();
        var cut = CutHeight(doc, view, z0);
        var notes = new List<string>();
        var sources = Sources(doc, view);
        string source;

        // ---- rooms: names for the plan; stairs/lifts/WCs become core spaces ----
        var placed = Rooms(doc, view, level).ToList();
        var picked = placed.Where(r => selected.Contains(r.Id)).ToList();
        var rooms = placed.Select(r => (Name: RoomName(r), Loops: Loops(r))).Where(r => r.Loops.Count > 0).ToList();

        // ---- the floor outline ----
        var faces = new List<List<IReadOnlyList<Vec2>>>();
        if (picked.Count > 0)
        {
            foreach (var r in picked) if (Loops(r) is { Count: > 0 } l) faces.Add(l);
            source = picked.Count == 1 ? $"the selected room '{RoomName(picked[0])}'" : $"{picked.Count} selected rooms";
        }
        else
        {
            var slabs = Slabs(sources, level, z0);
            var enclosed = Enclosed(doc, level, view, notes);
            double slabArea = slabs.Sum(f => Math.Abs(Area(f[0]))), roomArea = rooms.Sum(r => Math.Abs(Area(r.Loops[0])));
            double wallArea = enclosed.Sum(f => Math.Abs(Area(f[0])));
            // the inside of the walls wins: it is what the plan shows and what people fit out (a slab is often drawn
            // to the outside face of the façade); existing rooms only when the walls do not close
            bool Covers(double a) => a > 5e6 && (slabArea == 0 || a > 0.45 * slabArea);
            if (Covers(wallArea))
            {
                faces.AddRange(enclosed);
                source = "the inside of its walls";
            }
            else if (Covers(roomArea))
            {
                faces.AddRange(rooms.Select(r => r.Loops));
                source = "the rooms on this floor";
            }
            else if (slabs.Count > 0) { faces.AddRange(slabs); source = "the floor slabs"; }
            else { source = "the box round its walls"; }
        }

        // ---- existing walls at the cut plane, then columns and other solid things ----
        var walls = new List<SiteWall>();
        var poche = new List<SitePoche>();
        var columns = new List<(Vec2, Vec2)>();
        int cutWalls = 0;
        int linkedWalls = 0;
        foreach (var src in sources)
        {
            var band = Band(src, cut);
            IEnumerable<Element> Of(FilteredElementCollector c) => c.WherePasses(band).WhereElementIsNotElementType();
            var wallCollector = src.Host && view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(src.Doc);
            foreach (var wall in Of(wallCollector.OfClass(typeof(Wall))).Cast<Wall>())
            {
                if (Demolished(wall) || Context(src.Doc, wall) || wall.Location is not LocationCurve { Curve: { } curve }) continue;
                if (!src.Host) linkedWalls++;
                // as the plan draws it: the wall cut at the cut plane (openings, joins and all); else its centre line
                var loops = Section(wall, src.Place, cut);
                if (loops.Count > 0) { poche.Add(new SitePoche(Mm(wall.Width), loops)); cutWalls++; continue; }
                var pts = curve.Tessellate().Select(p => P(src.Place.OfPoint(p))).ToList();
                for (var i = 0; i + 1 < pts.Count; i++) walls.Add(new SiteWall(pts[i], pts[i + 1], Mm(wall.Width)));
            }
            var solidCollector = src.Host && view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(src.Doc);
            foreach (var e in Of(solidCollector.WherePasses(new ElementMulticategoryFilter(Solid))))
            {
                if (Demolished(e) || Context(src.Doc, e) || e.get_BoundingBox(null) is not { } box) continue;
                // a block taller than two storeys is massing (LiDAR context, a site model), not something on this floor
                if (box.Max.Z - box.Min.Z > Ft(8000) && e.Category?.Id.Value is not ((long)BuiltInCategory.OST_Columns or (long)BuiltInCategory.OST_StructuralColumns)) continue;
                var (a, b) = PlanBox(src.Place, box);
                if (b.X - a.X > 15000 || b.Y - a.Y > 15000) continue;
                var loops = Section(e, src.Place, cut);
                if (loops.Count > 0) poche.Add(new SitePoche(0, loops));
                else columns.Add((a, b));
            }
        }
        if (linkedWalls > 0) notes.Add($"{linkedWalls} walls from linked models");
        if (cutWalls > 0) notes.Add($"{cutWalls} walls cut at {Mm(cut - z0):0} mm as the plan draws them");

        var wallPts = walls.SelectMany(w => new[] { w.A, w.B }).Concat(poche.Where(p => p.ThicknessMm > 0).SelectMany(p => p.Loops.SelectMany(l => l))).ToList();
        if (faces.Count == 0 && wallPts.Count > 0)
        {
            double x0 = wallPts.Min(p => p.X), x1 = wallPts.Max(p => p.X), y0 = wallPts.Min(p => p.Y), y1 = wallPts.Max(p => p.Y);
            faces.Add([new Vec2[] { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) }]);
        }

        var site = new TestFitSite
        {
            Title = $"{doc.Title} · {level.Name}",
            Level = level.Name,
            StoreyHeightMm = Math.Clamp(Mm(above - z0), 2400, 6000),
            NorthDegrees = North(doc),
            Source = source + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : ""),
        };
        site.Faces.AddRange(faces);
        foreach (var r in rooms) site.Rooms.Add(new SiteRoom(r.Name, r.Loops[0]));
        if (faces.Count == 0) return site;

        // keep what stands on this floor: walls and objects over the outline (not neighbours on a context workset)
        var all = faces.SelectMany(f => f[0]).ToList();
        double fx0 = all.Min(p => p.X) - 600, fx1 = all.Max(p => p.X) + 600, fy0 = all.Min(p => p.Y) - 600, fy1 = all.Max(p => p.Y) + 600;
        bool Over(Vec2 p) => p.X >= fx0 && p.X <= fx1 && p.Y >= fy0 && p.Y <= fy1;
        // what the plan hides under opaque filled or masking regions drawn in it is not there for the test-fit either
        var masks = Masks(doc, view);
        bool Hidden(IEnumerable<Vec2> pts) => masks.Count > 0 && masks.Any(m => pts.All(p => PlanMask.Inside(m, p)));
        site.Walls.AddRange(walls.Where(w => (Over(w.A) || Over(w.B)) && !Hidden(new[] { w.A, w.B, (w.A + w.B) * 0.5 })));
        site.Poche.AddRange(poche.Where(p => p.Loops.Any(l => l.Any(Over)) && !Hidden(p.Loops.SelectMany(l => l))));
        site.Columns.AddRange(columns.Where(c => c.Item2.X >= fx0 && c.Item1.X <= fx1 && c.Item2.Y >= fy0 && c.Item1.Y <= fy1 && !Hidden(new[] { (c.Item1 + c.Item2) * 0.5 })));
        try { site.Cad.AddRange(Cad(doc, view, new Vec2(fx0 - 3000, fy0 - 3000), new Vec2(fx1 + 3000, fy1 + 3000), masks)); }
        catch { /* no linework: the page shows the model only */ }
        return site;
    }

    /// <summary>
    /// Filled regions drawn in the plan that hide what is under them (a solid fill or a masking region; a hatch
    /// shows through), each as its boundary loops in plan mm.
    /// </summary>
    static List<IReadOnlyList<IReadOnlyList<Vec2>>> Masks(Document doc, ViewPlan? view)
    {
        var masks = new List<IReadOnlyList<IReadOnlyList<Vec2>>>();
        if (view == null) return masks;
        bool Solid(ElementId id) => id != ElementId.InvalidElementId && doc.GetElement(id) is FillPatternElement f && f.GetFillPattern().IsSolidFill;
        foreach (var region in new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)).Cast<FilledRegion>().Where(r => r.OwnerViewId == view.Id))
        {
            try
            {
                if (doc.GetElement(region.GetTypeId()) is not FilledRegionType type) continue;
                if (!type.IsMasking && !Solid(type.ForegroundPatternId) && !Solid(type.BackgroundPatternId)) continue;
                var loops = region.GetBoundaries().Select(loop => (IReadOnlyList<Vec2>)loop.SelectMany(c => { var t = c.Tessellate(); return t.Take(t.Count - 1); }).Select(P).ToList())
                    .Where(l => l.Count >= 3).ToList();
                if (loops.Count > 0) masks.Add(loops);
            }
            catch { /* a region Revit cannot hand back */ }
        }
        return masks;
    }

    /// <summary>
    /// The 2D linework the plan shows over the floor: linked and imported CAD (on the layers the view shows) and
    /// the view's own detail lines, as plan polylines (mm) for the page to draw spaces over.
    /// </summary>
    static List<IReadOnlyList<Vec2>> Cad(Document doc, ViewPlan? view, Vec2 min, Vec2 max, List<IReadOnlyList<IReadOnlyList<Vec2>>> masks)
    {
        var lines = new List<IReadOnlyList<Vec2>>();
        if (view == null) return lines;
        IReadOnlyList<IReadOnlyList<Vec2>>? crop = null;
        try
        {
            if (view.CropBoxActive && view.GetCropRegionShapeManager()?.GetCropShape() is { Count: > 0 } shape)
                crop = shape.Select(loop => (IReadOnlyList<Vec2>)loop.SelectMany(c => { var t = c.Tessellate(); return t.Take(t.Count - 1); }).Select(P).ToList()).ToList();
        }
        catch { /* no crop: the whole level */ }
        var points = 0;
        bool In(Vec2 p) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
        void Add(IList<XYZ> pts)
        {
            if (points > 150000 || pts.Count < 2) return;
            var l = pts.Select(P).ToList();
            if (!l.Any(In)) return;
            // only what the plan shows: inside its crop, not under a filled or masking region
            if (masks.Count == 0 && crop == null) { lines.Add(l); points += l.Count; return; }
            foreach (var piece in PlanMask.Visible(l, masks, crop)) { lines.Add(piece); points += piece.Count; }
        }
        bool Shown(GeometryObject g) =>
            doc.GetElement(g.GraphicsStyleId) is not GraphicsStyle { GraphicsStyleCategory: { } c } || !view.GetCategoryHidden(c.Id);
        void Walk(GeometryElement? geo)
        {
            if (geo == null) return;
            foreach (var g in geo)
            {
                switch (g)
                {
                    case GeometryInstance gi: Walk(gi.GetInstanceGeometry()); break;
                    case PolyLine pl when Shown(pl): Add(pl.GetCoordinates()); break;
                    case Curve c when Shown(c): Add(c is Line ? new[] { c.GetEndPoint(0), c.GetEndPoint(1) } : c.Tessellate()); break;
                }
            }
        }
        var options = new Options { View = view };
        foreach (var cad in new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            try { Walk(cad.get_Geometry(options)); } catch { /* a CAD file Revit cannot read back */ }
        foreach (var line in new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                     .Where(c => c.OwnerViewId == view.Id && c.CurveElementType == CurveElementType.DetailCurve))
            try { Add(line.GeometryCurve is Line l ? new[] { l.GetEndPoint(0), l.GetEndPoint(1) } : line.GeometryCurve.Tessellate()); } catch { }
        return lines;
    }

    /// <summary>The floors that have a floor plan, lowest first, to browse in the test-fit window.</summary>
    public static List<Level> Floors(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null)
            .Select(v => v.GenLevel).GroupBy(l => l.Id).Select(g => g.First()).OrderBy(l => l.ProjectElevation).ToList();

    /// <summary>A floor plan of this level, the most like the one open (same phase, then same view template).</summary>
    public static ViewPlan? PlanOf(Document doc, Level level, ViewPlan? like)
    {
        if (like != null && like.GenLevel?.Id == level.Id) return like;
        ElementId Phase(View v) => v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id)
            .OrderBy(v => like != null && Phase(v) == Phase(like) ? 0 : 1)
            .ThenBy(v => like != null && v.ViewTemplateId == like.ViewTemplateId ? 0 : 1)
            .ThenBy(v => v.Name.Length).FirstOrDefault();
    }

    static Vec2 P(XYZ p) => new(Mm(p.X), Mm(p.Y));

    /// <summary>
    /// An element's section where the plan cuts it, as the plan's poché shows it: its solids cut by the cut plane,
    /// the loops of the cut face, in host plan millimetres. Empty if it has no solid there.
    /// </summary>
    static List<IReadOnlyList<Vec2>> Section(Element e, Transform place, double zHost)
    {
        var loops = new List<IReadOnlyList<Vec2>>();
        try
        {
            var z = place.Inverse.OfPoint(new XYZ(0, 0, zHost)).Z;
            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z));
            foreach (var solid in Solids(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })))
            {
                var bb = solid.GetBoundingBox();
                var tf = bb.Transform;
                double lo = tf.OfPoint(bb.Min).Z, hi = tf.OfPoint(bb.Max).Z;
                if (z <= Math.Min(lo, hi) + 1e-4 || z >= Math.Max(lo, hi) - 1e-4) continue;
                Solid above;
                try { above = BooleanOperationsUtils.CutWithHalfSpace(solid, plane); } catch { continue; }
                if (above == null) continue;
                foreach (Face f in above.Faces)
                {
                    if (f is not PlanarFace pf || pf.FaceNormal.Z > -0.999 || Math.Abs(pf.Origin.Z - z) > 1e-3) continue;
                    foreach (var cl in pf.GetEdgesAsCurveLoops())
                    {
                        var l = Loop(cl, place);
                        if (l.Count >= 3 && Math.Abs(Area(l)) > 100) loops.Add(l); // over 1 cm²
                    }
                }
            }
        }
        catch { /* geometry Revit cannot cut: the caller falls back */ }
        return loops;
    }

    static IEnumerable<Solid> Solids(GeometryElement? g)
    {
        if (g == null) yield break;
        foreach (var o in g)
        {
            if (o is Solid { Volume: > 1e-9 } s) yield return s;
            else if (o is GeometryInstance gi) foreach (var t in Solids(gi.GetInstanceGeometry())) yield return t;
        }
    }

    /// <summary>The height the plan cuts at (its view range), kept between 0.3 m and 2.5 m above the floor.</summary>
    static double CutHeight(Document doc, ViewPlan? view, double z0)
    {
        if (view != null) try
        {
            var range = view.GetViewRange();
            var at = doc.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) as Level;
            if (at != null)
            {
                var z = at.ProjectElevation + range.GetOffset(PlanViewPlane.CutPlane);
                if (z >= z0 + Ft(300) && z <= z0 + Ft(2500)) return z;
            }
        }
        catch { /* a view without a usable range */ }
        return z0 + Ft(1200);
    }

    /// <summary>The project, and every loaded link that the plan shows.</summary>
    static List<Source> Sources(Document doc, View? view)
    {
        var list = new List<Source> { new(doc, Transform.Identity, doc.Title, true) };
        foreach (var link in (view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc)).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            try
            {
                if (link.GetLinkDocument() is not { } linked) continue;
                list.Add(new Source(linked, link.GetTotalTransform(), linked.Title, false));
            }
            catch { /* an unloaded link */ }
        }
        return list;
    }

    /// <summary>Elements of a model crossing the cut plane (in that model's own heights).</summary>
    static BoundingBoxIntersectsFilter Band(Source src, double zHost)
    {
        var z = src.Place.Inverse.OfPoint(new XYZ(0, 0, zHost)).Z;
        return new(new Outline(new XYZ(-1e6, -1e6, z - Ft(20)), new XYZ(1e6, 1e6, z + Ft(20))));
    }

    static (Vec2, Vec2) PlanBox(Transform t, BoundingBoxXYZ box)
    {
        var corners = new[] { box.Min, new XYZ(box.Max.X, box.Min.Y, box.Min.Z), new XYZ(box.Min.X, box.Max.Y, box.Min.Z), box.Max }
            .Select(c => P(t.OfPoint(c))).ToList();
        return (new Vec2(corners.Min(c => c.X), corners.Min(c => c.Y)), new Vec2(corners.Max(c => c.X), corners.Max(c => c.Y)));
    }

    /// <summary>Rooms on this level that the plan shows (its phase).</summary>
    static IEnumerable<Room> Rooms(Document doc, View? view, Level level) =>
        (view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc)).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
            .OfType<Room>().Where(r => r.LevelId == level.Id && r.Area > 0);

    static string RoomName(Room r) => r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";

    static readonly SpatialElementBoundaryOptions Finish = new() { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };

    /// <summary>A room's outline (outer loop first, then islands), or nothing if Revit cannot bound it.</summary>
    static List<IReadOnlyList<Vec2>> Loops(SpatialElement room)
    {
        try
        {
            return room.GetBoundarySegments(Finish)?
                .Select(l => (IReadOnlyList<Vec2>)Loop(l.Select(s => s.GetCurve()), Transform.Identity)).Where(l => l.Count >= 3)
                .OrderByDescending(l => Math.Abs(Area(l))).ToList() ?? new();
        }
        catch { return new(); }
    }

    /// <summary>
    /// The spaces the walls enclose that have no room yet: Revit's own room tool fills each closed space with a
    /// room (linked walls count too) inside a transaction that is then undone, so nothing in the model changes.
    /// </summary>
    static List<List<IReadOnlyList<Vec2>>> Enclosed(Document doc, Level level, View? view, List<string> notes)
    {
        var faces = new List<List<IReadOnlyList<Vec2>>>();
        if (doc.IsReadOnly || doc.IsModifiable) return faces;
        using var t = new Transaction(doc, "Test-fit: find the spaces");
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new ModelBuilder.WarningSwallower());
        options.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(options);
        try
        {
            t.Start();
            int links = 0;
            foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)))
            {
                var p = type.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                if (p is { IsReadOnly: false } && p.AsInteger() == 0) { try { p.Set(1); links++; } catch { } }
            }
            // the whole inside of the walls: rooms already placed and room separation lines would leave parts of
            // the floor out (they are put back when the transaction is undone)
            var clear = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.LevelId == level.Id).Select(r => r.Id)
                .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomSeparationLines).WhereElementIsNotElementType()
                    .OfType<CurveElement>().Where(c => c.LevelId == level.Id || c.SketchPlane?.GetPlane() is { } pl && Math.Abs(pl.Origin.Z - level.Elevation) < Ft(50)).Select(c => c.Id))
                .ToList();
            foreach (var id in clear) { try { doc.Delete(id); } catch { /* pinned or owned by someone else: left in */ } }
            if (links > 0 || clear.Count > 0) doc.Regenerate();
            var phase = doc.GetElement(view?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase;
            var made = phase != null ? doc.Create.NewRooms2(level, phase) : doc.Create.NewRooms2(level);
            doc.Regenerate();
            foreach (var id in made)
            {
                if (doc.GetElement(id) is not Room r || r.Area < 1.5 * 10.7639) continue; // under 1.5 m² (Area is in square feet)
                if (Loops(r) is { Count: > 0 } l) faces.Add(l);
            }
        }
        catch (Exception ex) { notes.Add("walled spaces not read: " + ex.Message); }
        finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
        return faces;
    }

    /// <summary>Top faces of the slabs on this floor (the project's and its links'), one face per slab.</summary>
    static List<List<IReadOnlyList<Vec2>>> Slabs(List<Source> sources, Level level, double z0)
    {
        var faces = new List<List<IReadOnlyList<Vec2>>>();
        foreach (var src in sources)
        {
            var floors = new FilteredElementCollector(src.Doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => !Demolished(f));
            // in the project: slabs on this level; in a link: slabs whose top is within 400 mm of this floor
            floors = src.Host ? floors.Where(f => f.LevelId == level.Id)
                : floors.Where(f => f.get_BoundingBox(null) is { } b && Math.Abs(src.Place.OfPoint(b.Max).Z - z0) < Ft(400));
            foreach (var floor in floors)
            {
                IList<Reference> tops;
                try { tops = HostObjectUtils.GetTopFaces(floor); } catch { continue; }
                foreach (var r in tops)
                {
                    if (floor.GetGeometryObjectFromReference(r) is not Face face) continue;
                    var loops = face.GetEdgesAsCurveLoops().Select(l => Loop(l, src.Place)).Where(l => l.Count >= 3)
                        .OrderByDescending(l => Math.Abs(Area(l))).Cast<IReadOnlyList<Vec2>>().ToList();
                    if (loops.Count > 0 && Math.Abs(Area(loops[0])) > 1e6) faces.Add(loops);
                }
            }
        }
        return faces;
    }

    static List<Vec2> Loop(IEnumerable<Curve> curves, Transform place)
    {
        var pts = new List<Vec2>();
        foreach (var c in curves)
        {
            var t = c.Tessellate();
            for (var i = 0; i + 1 < t.Count; i++) pts.Add(P(place.OfPoint(t[i])));
        }
        return pts;
    }

    static double Area(IReadOnlyList<Vec2> l)
    {
        double a = 0;
        for (var i = 0; i < l.Count; i++) a += l[i].Cross(l[(i + 1) % l.Count]);
        return a / 2;
    }

    /// <summary>Surroundings (Build from Survey's LiDAR blocks, anything on a Context workset): not part of the floor.</summary>
    static bool Context(Document doc, Element e)
    {
        if (e.Name.StartsWith("Context", StringComparison.OrdinalIgnoreCase)) return true;
        if (!doc.IsWorkshared) return false;
        try { return doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name.Contains("Context", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
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
