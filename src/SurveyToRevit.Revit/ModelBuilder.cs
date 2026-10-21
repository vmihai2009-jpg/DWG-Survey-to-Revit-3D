using Autodesk.Revit.DB;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>What to build.</summary>
public sealed class BuildOptions
{
    public bool LinkPlans { get; set; } = true;
    public bool Walls { get; set; } = true;
    public bool Floors { get; set; } = true;
    public bool Ceilings { get; set; } = true;
    /// <summary>Windows in the walls (needs walls).</summary>
    public bool Windows { get; set; } = true;
    /// <summary>Doors in the walls (needs walls).</summary>
    public bool Doors { get; set; } = true;
    /// <summary>Rooms named from the survey's room labels (needs walls).</summary>
    public bool Rooms { get; set; } = true;
    /// <summary>Areas the engine could not read become generic models (DirectShape) for review.</summary>
    public bool UnclearAsGenericModels { get; set; } = true;
    public double SlabThicknessMm { get; set; } = 300;
    /// <summary>Surroundings from LiDAR (ground and neighbouring blocks); null for none.</summary>
    public SiteContext? Site { get; set; }
}

/// <summary>
/// Applies a <see cref="SurveyModel"/> to a Revit document: shared coordinates
/// and true north, levels, DWG links in each level's plan view, walls, floor
/// slabs, ceilings, windows, doors, named rooms and generic models for unclear areas.
/// Everything generated is on the Existing phase (it is a survey of what is there)
/// and tagged in Comments so a re-run replaces only it.
/// </summary>
public sealed class ModelBuilder
{
    public const string Tag = "SurveyToRevit";
    readonly Document _doc;
    readonly SurveyModel _model;
    public List<string> Log { get; } = new();

    public ModelBuilder(Document doc, SurveyModel model)
    {
        _doc = doc; _model = model;
        var phases = doc.Phases.Cast<Phase>().ToList();
        _phase = phases.FirstOrDefault(p => p.Name.Contains("Existing", StringComparison.OrdinalIgnoreCase)) ?? phases.FirstOrDefault();
    }

    readonly Phase? _phase;

    static double Ft(double mm) => UnitConversion.MmToFeet(mm);

    public void Run(BuildOptions o)
    {
        Log.Add(_phase == null ? "WARNING: the project has no phases; elements are on Revit's default phase."
                               : $"All elements are created in the '{_phase.Name}' phase.");
        using var group = new TransactionGroup(_doc, "Build from survey");
        group.Start();
        SetCoordinates();
        var levels = SetUpLevels();
        if (o.LinkPlans) LinkPlans(levels);
        BuildElements(levels, o);
        if (o.Site != null) BuildSite(o.Site, levels.Values.Select(v => v.level).ToList());
        group.Assimilate();
    }

    /// <summary>
    /// Internal origin = survey base point; project north = the building's main
    /// walls; true north = survey grid north; heights stay above survey datum.
    /// The direction convention is checked after setting and flipped if needed.
    /// </summary>
    void SetCoordinates()
    {
        var f = _model.Frame;
        double e = Ft(f.BasePoint.X), n = Ft(f.BasePoint.Y), angle = f.RotationDeg * Math.PI / 180;
        using var t = new Transaction(_doc, "Survey coordinates");
        t.Start();
        var location = _doc.ActiveProjectLocation;
        foreach (var sign in new[] { 1.0, -1.0 })
        {
            location.SetProjectPosition(XYZ.Zero, new ProjectPosition(e, n, 0, sign * angle));
            if (CheckInternalToShared(location, e, n, angle)) { t.Commit(); Log.Add($"Shared coordinates set: internal origin = E {f.BasePoint.X / 1000:0.000} N {f.BasePoint.Y / 1000:0.000}, project north {f.RotationDeg:0.00}° from true north."); return; }
        }
        t.RollBack();
        throw new InvalidOperationException("Could not set the project coordinates consistently; nothing was changed.");
    }

    // Survey = base point + rotation(+angle) × internal. Accept either way Revit reports its transform.
    static bool CheckInternalToShared(ProjectLocation location, double e, double n, double angle)
    {
        var total = location.GetTotalTransform();
        foreach (var m in new[] { total, total.Inverse })
        {
            var o = m.OfPoint(XYZ.Zero);
            if (Math.Abs(o.X - e) > 0.01 || Math.Abs(o.Y - n) > 0.01) continue;
            var x = m.OfVector(XYZ.BasisX);
            return Math.Abs(Math.Atan2(x.Y, x.X) - angle) < 1e-4;
        }
        return false;
    }

    Dictionary<SurveyLevel, (Level level, ViewPlan? view)> SetUpLevels()
    {
        var result = new Dictionary<SurveyLevel, (Level, ViewPlan?)>();
        using var t = new Transaction(_doc, "Survey levels");
        t.Start();
        var existing = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var planType = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .First(v => v.ViewFamily == ViewFamily.FloorPlan);
        foreach (var sl in _model.Levels.Where(l => l.Include && !double.IsNaN(l.ElevationMm)))
        {
            var level = existing.FirstOrDefault(l => l.Name == sl.Name);
            if (level == null)
            {
                level = Level.Create(_doc, Ft(sl.ElevationMm));
                level.Name = sl.Name;
                Log.Add($"Level created: {sl.Name} at {sl.ElevationMm / 1000:0.00} m");
            }
            else if (Math.Abs(level.Elevation - Ft(sl.ElevationMm)) > 1e-6)
            {
                Log.Add($"Level moved: {sl.Name} from {UnitConversion.FeetToMm(level.Elevation) / 1000:0.00} to {sl.ElevationMm / 1000:0.00} m");
                level.Elevation = Ft(sl.ElevationMm);
            }
            var view = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id);
            view ??= ViewPlan.Create(_doc, planType.Id, level.Id);
            result[sl] = (level, view);
        }
        t.Commit();
        return result;
    }

    void LinkPlans(Dictionary<SurveyLevel, (Level level, ViewPlan? view)> levels)
    {
        var linked = new FilteredElementCollector(_doc).OfClass(typeof(CADLinkType)).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (sl, (_, view)) in levels)
        {
            var file = System.IO.Path.GetFileName(sl.Plan.Path);
            if (view == null || linked.Contains(file)) { Log.Add($"Already linked: {file}"); continue; }
            using var t = new Transaction(_doc, "Link survey plan");
            t.Start();
            var options = new DWGImportOptions
            {
                Placement = ImportPlacement.Shared,
                Unit = ImportUnitFor(sl.Plan.MmPerUnit),
                ThisViewOnly = true,
                ColorMode = ImportColorMode.Preserved,
            };
            bool ok;
            ElementId id;
            try { ok = _doc.Link(sl.Plan.Path, options, view, out id); }
            catch (Exception ex) { t.RollBack(); Log.Add($"WARNING: could not link {file} ({ex.Message})."); continue; }
            if (ok && _doc.GetElement(id) is ImportInstance inst)
            {
                inst.Pinned = true;
                t.Commit();
                Log.Add($"Linked {file} into '{view.Name}' using shared coordinates.");
                try { CheckLinkPosition(inst, sl); }
                catch (Exception ex) { Log.Add($"Could not check where {file} landed ({ex.Message}); check it by eye."); }
            }
            else { t.RollBack(); Log.Add($"WARNING: could not link {file}."); }
        }
    }

    static ImportUnit ImportUnitFor(double mmPerUnit) => mmPerUnit switch
    {
        1 => ImportUnit.Millimeter,
        10 => ImportUnit.Centimeter,
        1000 => ImportUnit.Meter,
        25.4 => ImportUnit.Inch,
        _ => ImportUnit.Foot,
    };

    /// <summary>
    /// The link should land where the walls are built. Compare the drawn wall lines as
    /// Revit placed them with where the engine put them; if they are off, move the link.
    /// </summary>
    void CheckLinkPosition(ImportInstance inst, SurveyLevel sl)
    {
        var file = System.IO.Path.GetFileName(sl.Plan.Path);
        var placed = WallLayerExtents(inst);
        var lines = sl.Plan.WallLines.Select(_model.Frame.ToModel).ToList();
        if (placed == null || lines.Count == 0) { Log.Add($"Could not check where {file} landed."); return; }
        var expected = (Min: new XYZ(Ft(lines.Min(l => Math.Min(l.A.X, l.B.X))), Ft(lines.Min(l => Math.Min(l.A.Y, l.B.Y))), 0),
                        Max: new XYZ(Ft(lines.Max(l => Math.Max(l.A.X, l.B.X))), Ft(lines.Max(l => Math.Max(l.A.Y, l.B.Y))), 0));
        var off = (expected.Min + expected.Max) / 2 - (placed.Value.Min + placed.Value.Max) / 2;
        off = new XYZ(off.X, off.Y, 0);
        double sizeError = Math.Max(Math.Abs((expected.Max - expected.Min).X - (placed.Value.Max - placed.Value.Min).X),
                                    Math.Abs((expected.Max - expected.Min).Y - (placed.Value.Max - placed.Value.Min).Y));
        double offMm = UnitConversion.FeetToMm(off.GetLength());
        if (UnitConversion.FeetToMm(sizeError) > 100)
        {
            Log.Add($"WARNING: {file} is a different size or turned compared with the walls ({UnitConversion.FeetToMm(sizeError):0} mm). Check its units and rotation.");
            return;
        }
        if (offMm <= 10) { Log.Add($"{file} lines up with the walls."); return; }
        using var t = new Transaction(_doc, "Align survey link");
        t.Start();
        inst.Pinned = false;
        ElementTransformUtils.MoveElement(_doc, inst.Id, off);
        inst.Pinned = true;
        t.Commit();
        Log.Add($"{file} landed {offMm:0} mm away from the walls and was moved onto them.");
    }

    /// <summary>Extents of the link's wall-layer lines, in model coordinates.</summary>
    (XYZ Min, XYZ Max)? WallLayerExtents(ImportInstance inst)
    {
        var rules = LayerRules.Default;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(XYZ p) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        void Visit(GeometryElement g)
        {
            foreach (var o in g)
            {
                if (o is GeometryInstance gi) { Visit(gi.GetInstanceGeometry()); continue; }
                var layer = (_doc.GetElement(o.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
                if (rules.Classify(layer) != LayerRole.Wall) continue;
                if (o is Curve c) { foreach (var p in c.Tessellate()) Add(p); } // circles are unbound: no end points
                else if (o is PolyLine pl) foreach (var p in pl.GetCoordinates()) Add(p);
            }
        }
        var geom = inst.get_Geometry(new Options());
        if (geom == null) return null;
        Visit(geom);
        return minX == double.MaxValue ? null : (new XYZ(minX, minY, 0), new XYZ(maxX, maxY, 0));
    }

    void BuildElements(Dictionary<SurveyLevel, (Level level, ViewPlan? view)> levels, BuildOptions o)
    {
        var ordered = levels.OrderBy(kv => kv.Value.level.Elevation).ToList();
        using var t = new Transaction(_doc, "Survey model");
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new WarningSwallower());
        t.SetFailureHandlingOptions(options);
        t.Start();
        var wallTypes = new Dictionary<double, WallType>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (sl, (level, _)) = (ordered[i].Key, ordered[i].Value);
            var above = i + 1 < ordered.Count ? ordered[i + 1].Value.level : null;
            int removed = DeleteGenerated(sl.Code);
            var done = new List<string>();

            var created = new Dictionary<WallCandidate, Wall>(ReferenceEqualityComparer.Instance);
            if (o.Walls)
            {
                int made = 0, skipped = 0;
                foreach (var w in sl.Walls.Walls)
                {
                    var c = w.Centerline; // already in model (internal) coordinates
                    if (c.Length < 10) continue;
                    try
                    {
                        Curve line = w.Arc is Arc2 a
                            ? Arc.Create(new XYZ(Ft(a.Center.X), Ft(a.Center.Y), level.Elevation), Ft(a.Radius), Arc2.Norm(a.Start), Arc2.Norm(a.Start) + a.Sweep, XYZ.BasisX, XYZ.BasisY)
                            : Line.CreateBound(new XYZ(Ft(c.A.X), Ft(c.A.Y), level.Elevation), new XYZ(Ft(c.B.X), Ft(c.B.Y), level.Elevation));
                        if (!wallTypes.TryGetValue(w.TypeThickness, out var type)) wallTypes[w.TypeThickness] = type = WallTypeFor(w.TypeThickness);
                        var wall = Wall.Create(_doc, line, type.Id, level.Id, Ft(sl.WallHeightMm), 0, false, false);
                        if (above != null) wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(above.Id);
                        Mark(wall, sl.Code, w.Confidence.ToString());
                        created[w] = wall;
                        made++;
                    }
                    catch (Exception ex) { skipped++; if (skipped <= 5) Log.Add($"{sl.Name}: a wall at ({c.A.X:0}, {c.A.Y:0}) mm was skipped: {ex.Message}"); }
                }
                done.Add($"{made} walls" + (skipped > 0 ? $" ({skipped} skipped)" : ""));
                if (o.Windows && sl.Windows.Count > 0) done.Add(CreateWindows(sl, level, created));
                if (o.Doors && sl.Doors.Count > 0) done.Add(CreateDoors(sl, level, created));
            }

            var loop = sl.Outline != null ? Loop(sl.Outline, level.Elevation) : null;
            if (o.Floors)
            {
                if (loop == null) done.Add("no floor (outline not found)");
                else try
                {
                    var floor = Floor.Create(_doc, new List<CurveLoop> { loop }, FloorTypeFor(o.SlabThicknessMm).Id, level.Id);
                    Mark(floor, sl.Code, "Floor");
                    done.Add($"floor {sl.Outline!.Area / 1e6:0} m²");
                }
                catch (Exception ex) { done.Add($"no floor ({ex.Message})"); }
            }
            if (o.Ceilings && loop != null)
            {
                try
                {
                    var ceiling = Ceiling.Create(_doc, new List<CurveLoop> { loop }, CeilingTypeFor().Id, level.Id);
                    ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(Ft(sl.CeilingHeightMm));
                    Mark(ceiling, sl.Code, "Ceiling");
                    done.Add($"ceiling at {sl.CeilingHeightMm:0} mm");
                }
                catch (Exception ex) { done.Add($"no ceiling ({ex.Message})"); }
            }
            if (o.UnclearAsGenericModels && sl.Unclear.Count > 0)
            {
                int made = 0;
                foreach (var u in sl.Unclear)
                {
                    try
                    {
                        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(
                            new List<CurveLoop> { Loop(u.Outline, level.Elevation)! }, XYZ.BasisZ, Ft(sl.WallHeightMm));
                        var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                        ds.SetShape(new List<GeometryObject> { solid });
                        ds.Name = "Survey - check";
                        Mark(ds, sl.Code, "Unclear: " + u.Reason);
                        made++;
                    }
                    catch { /* a sliver Revit refuses is skipped */ }
                }
                done.Add($"{made} generic models for areas to check");
            }
            if (o.Rooms && o.Walls && sl.RoomNames.Count > 0)
            {
                try { done.Add(CreateRooms(sl, level)); }
                catch (Exception ex) { done.Add($"no rooms ({ex.Message})"); }
            }
            Log.Add($"{sl.Name}: {string.Join(", ", done)}" + (removed > 0 ? $" (replacing {removed} elements from an earlier run)." : "."));
        }
        t.Commit();
    }

    /// <summary>
    /// Surroundings from LiDAR: a toposolid for the ground and a mass per neighbouring roof.
    /// Replaces the surroundings of an earlier run; survey elements are not touched.
    /// </summary>
    void BuildSite(SiteContext site, List<Level> levels)
    {
        foreach (var note in site.Notes) Log.Add("Surroundings: " + note);
        using var t = new Transaction(_doc, "Survey surroundings");
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new WarningSwallower());
        t.SetFailureHandlingOptions(options);
        t.Start();
        int removed = DeleteGenerated(SiteCode);
        var done = new List<string>();
        var workset = ContextWorkset();
        void OnWorkset(Element e)
        {
            if (workset != null) e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.Set(workset.IntegerValue);
        }

        if (site.Ground.Count >= 3)
        {
            try
            {
                var level = levels.Count > 0 ? levels.OrderBy(l => l.Elevation).First()
                          : new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
                var points = site.Ground.Select(g => new XYZ(Ft(g.Position.X), Ft(g.Position.Y), Ft(g.ZMm))).ToList();
                var type = new FilteredElementCollector(_doc).OfClass(typeof(ToposolidType)).FirstElementId();
                var topo = Toposolid.Create(_doc, points, type, level.Id);
                _doc.Regenerate();
                // the points are meant above datum; if Revit took them as above the level, lift the surface back onto the LiDAR
                var box = topo.get_BoundingBox(null);
                double expectedTop = points.Max(p => p.Z);
                if (box != null && Math.Abs(box.Max.Z - expectedTop) > Ft(100))
                    ElementTransformUtils.MoveElement(_doc, topo.Id, new XYZ(0, 0, expectedTop - box.Max.Z));
                Mark(topo, SiteCode, "Ground");
                OnWorkset(topo);
                done.Add($"ground surface from {points.Count} LiDAR points");
            }
            catch (Exception ex) { done.Add($"no ground surface ({ex.Message})"); }
        }

        var category = DirectShape.IsValidCategoryId(new ElementId(BuiltInCategory.OST_Mass), _doc) ? BuiltInCategory.OST_Mass : BuiltInCategory.OST_GenericModel;
        int made = 0, failed = 0;
        foreach (var b in site.Blocks)
        {
            double z = Ft(b.BaseMm), h = Ft(b.TopMm - b.BaseMm);
            var outer = Loop(b.Outline, z);
            if (outer == null) { failed++; continue; }
            Solid? solid = null;
            var holes = b.Holes.Select(x => Loop(x, z)).Where(x => x != null).Cast<CurveLoop>().ToList();
            foreach (var loops in new[] { holes.Prepend(outer).ToList(), new List<CurveLoop> { outer } })
            {
                try { solid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, h); break; }
                catch { /* retry without courtyards */ }
            }
            if (solid == null) { failed++; continue; }
            var ds = DirectShape.CreateElement(_doc, new ElementId(category));
            ds.SetShape(new List<GeometryObject> { solid });
            ds.Name = "Context - LiDAR";
            Mark(ds, SiteCode, $"Context block {b.TopMm / 1000:0.0} m");
            OnWorkset(ds);
            made++;
        }
        done.Add($"{made} neighbouring blocks" + (failed > 0 ? $" ({failed} could not be made)" : ""));
        t.Commit();
        Log.Add($"Surroundings: {string.Join(", ", done)}" + (removed > 0 ? $" (replacing {removed} elements from an earlier run)." : "."));
    }

    const string SiteCode = "SITE";
    const string ContextWorksetName = "Context";

    /// <summary>The "Context" workset (made if missing) in a workshared project; null otherwise (worksharing is never turned on by the add-in).</summary>
    WorksetId? ContextWorkset()
    {
        if (!_doc.IsWorkshared)
        {
            Log.Add("Surroundings: the project is not workshared, so they cannot go on their own workset; turn on worksharing and run again to separate them.");
            return null;
        }
        var existing = new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset)
            .FirstOrDefault(w => w.Name.Equals(ContextWorksetName, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.Id;
        try
        {
            var made = Workset.Create(_doc, ContextWorksetName);
            Log.Add($"Surroundings: workset '{ContextWorksetName}' created.");
            return made.Id;
        }
        catch (Exception ex) { Log.Add($"Surroundings: could not create the '{ContextWorksetName}' workset ({ex.Message}); they stay on the active workset."); return null; }
    }

    /// <summary>Tags a generated element (so a re-run replaces it) and puts it on the Existing phase.</summary>
    void Mark(Element e, string levelCode, string what)
    {
        e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{Tag}|{levelCode}|{what}");
        var phase = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
        if (_phase != null && phase != null && !phase.IsReadOnly) phase.Set(_phase.Id);
    }

    /// <summary>Closed loop at the given height; edges too short for Revit are dropped.</summary>
    static CurveLoop? Loop(Polygon2 poly, double z)
    {
        var pts = new List<XYZ>();
        foreach (var v in poly.Vertices)
        {
            var p = new XYZ(Ft(v.X), Ft(v.Y), z);
            if (pts.Count == 0 || pts[^1].DistanceTo(p) > 0.01) pts.Add(p); // ~3 mm
        }
        while (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) <= 0.01) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return null;
        var loop = new CurveLoop();
        for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        return loop;
    }

    int DeleteGenerated(string levelCode)
    {
        var prefix = $"{Tag}|{levelCode}|";
        var classes = new ElementMulticlassFilter(new List<Type> { typeof(Wall), typeof(Floor), typeof(Ceiling), typeof(DirectShape), typeof(FamilyInstance), typeof(Opening), typeof(SpatialElement), typeof(Toposolid) });
        var ids = new FilteredElementCollector(_doc).WherePasses(classes).WhereElementIsNotElementType()
            .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(prefix) == true)
            .Select(e => e.Id).ToList();
        if (ids.Count > 0) _doc.Delete(ids);
        return ids.Count;
    }

    /// <summary>
    /// Windows hosted in the generated walls. Uses a wall-hosted window family already in the
    /// project (a fixed window if there is one), else Revit's own fixed window from the library;
    /// a type per size is made ("Concept Window 1200x1500"). With no window family at all,
    /// plain rectangular openings are cut instead. Lining walls in front of the host are cut too.
    /// </summary>
    string CreateWindows(SurveyLevel sl, Level level, Dictionary<WallCandidate, Wall> created)
    {
        var (made, failed, real) = PlaceHosted(sl, level, created, BuiltInCategory.OST_Windows, "Window",
            sl.Windows.Select(w => (w.HostIndex, w.Center, w.WidthMm, w.SillMm, w.HeadMm)));
        return real ? $"{made} windows" + (failed > 0 ? $" ({failed} did not fit)" : "")
                    : $"{made} window openings (no window family found)";
    }

    /// <summary>Doors hosted in the generated walls, as windows: a single door family, a type per size ("Concept Door 900x2100").</summary>
    string CreateDoors(SurveyLevel sl, Level level, Dictionary<WallCandidate, Wall> created)
    {
        var (made, failed, real) = PlaceHosted(sl, level, created, BuiltInCategory.OST_Doors, "Door",
            sl.Doors.Select(d => (d.HostIndex, d.Center, d.WidthMm, 0.0, d.HeightMm)));
        return real ? $"{made} doors" + (failed > 0 ? $" ({failed} did not fit)" : "")
                    : $"{made} door openings (no door family found)";
    }

    (int Made, int Failed, bool Real) PlaceHosted(SurveyLevel sl, Level level, Dictionary<WallCandidate, Wall> created,
        BuiltInCategory category, string what, IEnumerable<(int Host, Vec2 Center, double Width, double Bottom, double Top)> items)
    {
        var family = HostedFamily(category, what);
        var symbols = new Dictionary<(double, double), FamilySymbol>();
        int made = 0, failed = 0;
        foreach (var (hostIndex, center, width, bottom, top) in items)
        {
            if (!created.TryGetValue(sl.Walls.Walls[hostIndex], out var wall)) continue;
            var c = new XYZ(Ft(center.X), Ft(center.Y), level.Elevation);
            try
            {
                if (family != null)
                {
                    double w = Math.Round(width / 50) * 50, h = Math.Round((top - bottom) / 50) * 50;
                    if (!symbols.TryGetValue((w, h), out var symbol)) symbols[(w, h)] = symbol = HostedType(family, what, w, h);
                    var inst = _doc.Create.NewFamilyInstance(c, symbol, wall, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    if (category == BuiltInCategory.OST_Windows) inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(Ft(bottom));
                    Mark(inst, sl.Code, what);
                }
                else Cut(wall, c, width, bottom, top, sl.Code, what + " opening");
                made++;
            }
            catch { failed++; continue; }
            // a lining or second skin drawn as its own wall in front of the host is cut through too
            foreach (var k in WallsAcross(sl.Walls.Walls, hostIndex, center, width))
                if (created.TryGetValue(sl.Walls.Walls[k], out var other))
                    try { Cut(other, c, width, bottom, top, sl.Code, what + " opening"); } catch { /* does not reach this wall */ }
        }
        return (made, failed, family != null);
    }

    void Cut(Wall wall, XYZ center, double widthMm, double bottomMm, double topMm, string code, string what)
    {
        // along the wall at the opening (the tangent, on a curved wall)
        var curve = ((LocationCurve)wall.Location).Curve;
        var at = curve.Project(center);
        var u = (at != null ? curve.ComputeDerivatives(at.Parameter, false).BasisX.Normalize() : (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize()) * Ft(widthMm / 2);
        var opening = _doc.Create.NewOpening(wall, center - u + XYZ.BasisZ * Ft(bottomMm), center + u + XYZ.BasisZ * Ft(topMm));
        Mark(opening, code, what);
    }

    /// <summary>Other walls parallel to the host, touching or overlapping it, that the opening runs through.</summary>
    static IEnumerable<int> WallsAcross(IReadOnlyList<WallCandidate> walls, int host, Vec2 center, double width)
    {
        var h = walls[host];
        if (h.Arc != null) yield break;
        for (int k = 0; k < walls.Count; k++)
        {
            if (k == host || walls[k].Arc != null) continue;
            var c = walls[k].Centerline;
            if (SegmentCleaner.AngleDiffDeg(c.AngleDeg, h.Centerline.AngleDeg) > 5) continue;
            if (Math.Abs(SegmentCleaner.Offset(c, center)) > (h.TypeThickness + walls[k].TypeThickness) / 2 + 100) continue;
            double along = SegmentCleaner.Along(c, center);
            if (along < width / 2 || along > c.Length - width / 2) continue;
            yield return k;
        }
    }

    readonly Dictionary<BuiltInCategory, Family?> _families = new();

    /// <summary>
    /// A wall-hosted family of the category: one in the project (a fixed window or a single
    /// door if there is one), else Revit's own from the library; null if there is none.
    /// </summary>
    Family? HostedFamily(BuiltInCategory category, string what)
    {
        if (_families.TryGetValue(category, out var cached)) return cached;
        string prefer = category == BuiltInCategory.OST_Windows ? "Fixed" : "Single";
        var inProject = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => f.FamilyCategory?.Id.Value == (long)category && f.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted)
            .ToList();
        var family = inProject.FirstOrDefault(f => f.Name.Contains(prefer, StringComparison.OrdinalIgnoreCase)) ?? inProject.FirstOrDefault();
        if (family == null)
            foreach (var root in _doc.Application.GetLibraryPaths().Values.Where(Directory.Exists))
            {
                List<string> files = new();
                try
                {
                    files = Directory.EnumerateFiles(root, $"*{prefer}*.rfa", SearchOption.AllDirectories)
                        .Where(f => f.Contains(what, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
                }
                catch { /* unreadable folder */ }
                foreach (var file in files)
                    if (_doc.LoadFamily(file, out Family loaded) && loaded.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted)
                    {
                        Log.Add($"{what} family loaded: {Path.GetFileName(file)}");
                        family = loaded;
                        break;
                    }
                if (family != null) break;
            }
        if (family != null) Log.Add($"{what}s use the family '{family.Name}'.");
        else Log.Add($"No wall-hosted {what.ToLowerInvariant()} family in the project or Revit library: {what.ToLowerInvariant()}s are cut as plain openings. Load one and run again for real {what.ToLowerInvariant()}s.");
        return _families[category] = family;
    }

    FamilySymbol HostedType(Family family, string what, double widthMm, double heightMm)
    {
        var name = $"Concept {what} {widthMm:0}x{heightMm:0}";
        var all = family.GetFamilySymbolIds().Select(id => (FamilySymbol)_doc.GetElement(id)).ToList();
        var symbol = all.FirstOrDefault(s => s.Name == name);
        if (symbol == null)
        {
            symbol = (FamilySymbol)all.First().Duplicate(name);
            SetLength(symbol, Ft(widthMm), BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, "Width");
            SetLength(symbol, Ft(heightMm), BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, "Height");
        }
        if (!symbol.IsActive) { symbol.Activate(); _doc.Regenerate(); }
        return symbol;
    }

    /// <summary>
    /// Rooms where the survey names them: Revit finds the spaces the walls enclose on this
    /// level (in the Existing phase); each one holding a survey room label becomes a room with
    /// that name. Spaces with no label get no room.
    /// </summary>
    string CreateRooms(SurveyLevel sl, Level level)
    {
        if (_phase == null) return "no rooms (no phase)";
        _doc.Regenerate();
        var placed = new List<Autodesk.Revit.DB.Architecture.Room>();
        int circuits = 0, refused = 0;
        string? firstError = null;
        foreach (PlanCircuit circuit in _doc.get_PlanTopology(level, _phase).Circuits)
        {
            circuits++;
            if (circuit.IsRoomLocated) continue;
            try { placed.Add(_doc.Create.NewRoom(_doc.Create.NewRoom(_phase), circuit)); }
            catch (Exception ex) { refused++; firstError ??= ex.Message; }
        }
        // a new room has no shape until the model regenerates; without this every label looks outside every room
        _doc.Regenerate();
        double z = level.Elevation + Ft(1000);
        var names = new Dictionary<Autodesk.Revit.DB.Architecture.Room, List<string>>();
        int outside = 0;
        foreach (var (pos, name) in sl.RoomNames)
        {
            var room = placed.FirstOrDefault(r => r.IsPointInRoom(new XYZ(Ft(pos.X), Ft(pos.Y), z)));
            if (room == null) { outside++; continue; }
            if (!names.TryGetValue(room, out var list)) names[room] = list = new List<string>();
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        }
        var unnamed = placed.Where(r => !names.ContainsKey(r)).Select(r => r.Id).ToList();
        if (unnamed.Count > 0) _doc.Delete(unnamed);
        foreach (var (room, list) in names)
        {
            room.Name = string.Join(" / ", list);
            room.UpperLimit = level;
            room.LimitOffset = Ft(sl.CeilingHeightMm);
            Mark(room, sl.Code, "Room");
        }
        return $"{names.Count} rooms" + (outside > 0 ? $" ({outside} labels not inside a closed space)" : "")
             + $" [{circuits} enclosed spaces found, {placed.Count} rooms tried" + (refused > 0 ? $", {refused} refused: {firstError}" : "") + "]";
    }

    static void SetLength(Element e, double value, BuiltInParameter first, BuiltInParameter second, BuiltInParameter third, string name)
    {
        var p = e.get_Parameter(first) ?? e.get_Parameter(second) ?? e.get_Parameter(third) ?? e.LookupParameter(name);
        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double) p.Set(value);
    }

    FloorType FloorTypeFor(double thicknessMm)
    {
        var name = $"Concept Slab {thicknessMm:0}mm";
        var all = new FilteredElementCollector(_doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        var type = all.FirstOrDefault(f => f.Name == name);
        var structure = type?.GetCompoundStructure();
        if (structure != null && Math.Abs(structure.GetWidth() - Ft(thicknessMm)) < 1e-6) return type!;
        // Copy a floor type's own layer set and make it one layer of the slab thickness: a structure
        // built from scratch has the wrong edge (end cap) settings for floors and Revit refuses it.
        var basic = all.First(f => !f.IsFoundationSlab && f.GetCompoundStructure() != null);
        if (type == null) { type = (FloorType)basic.Duplicate(name); Log.Add($"Floor type created: {name}"); }
        try
        {
            var cs = basic.GetCompoundStructure();
            var core = cs.GetLayers()[Math.Max(0, cs.GetFirstCoreLayerIndex())];
            cs.SetLayers(new List<CompoundStructureLayer> { new(Ft(thicknessMm), MaterialFunctionAssignment.Structure, core.MaterialId) });
            cs.SetNumberOfShellLayers(ShellLayerType.Exterior, 0);
            cs.SetNumberOfShellLayers(ShellLayerType.Interior, 0);
            cs.StructuralMaterialIndex = 0;
            type.SetCompoundStructure(cs);
        }
        catch (Exception ex) { Log.Add($"WARNING: could not set {name} to {thicknessMm:0} mm ({ex.Message}); edit the type's structure by hand."); }
        return type;
    }

    CeilingType CeilingTypeFor()
    {
        const string name = "Concept Ceiling";
        var all = new FilteredElementCollector(_doc).OfClass(typeof(CeilingType)).Cast<CeilingType>().ToList();
        var found = all.FirstOrDefault(c => c.Name == name);
        if (found != null) return found;
        var basic = all.FirstOrDefault(c => c.GetCompoundStructure() != null) ?? all.First();
        var type = (CeilingType)basic.Duplicate(name);
        Log.Add($"Ceiling type created: {name}");
        return type;
    }

    WallType WallTypeFor(double thicknessMm) => WallTypeFor(_doc, thicknessMm, Log);

    /// <summary>The "Concept Wall 150mm" type for a thickness, made from a basic wall type if missing.</summary>
    public static WallType WallTypeFor(Document doc, double thicknessMm, List<string>? log = null)
    {
        var name = WallDetector.TypeNameFor(thicknessMm);
        var all = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        var found = all.FirstOrDefault(w => w.Name == name);
        if (found != null) return found;
        var basic = all.First(w => w.Kind == WallKind.Basic);
        var type = (WallType)basic.Duplicate(name);
        type.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(
            MaterialFunctionAssignment.Structure, Ft(thicknessMm), ElementId.InvalidElementId));
        log?.Add($"Wall type created: {name}");
        return type;
    }

    /// <summary>
    /// Dismisses warnings (e.g. overlapping walls) so a large run is not interrupted, and lets
    /// Revit apply its own fix for errors on single elements (usually deleting the element that
    /// cannot be made, e.g. a window that does not fit), so one bad element never cancels the run.
    /// </summary>
    sealed class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            bool resolved = false;
            foreach (var f in a.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                else if (f.GetSeverity() == FailureSeverity.Error && f.HasResolutions()) { a.ResolveFailure(f); resolved = true; }
            }
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }
}
