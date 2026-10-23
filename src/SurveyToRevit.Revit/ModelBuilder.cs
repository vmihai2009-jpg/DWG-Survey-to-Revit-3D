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
    public bool UnclearAsGenericModels { get; set; } = false;
    /// <summary>Stairs from the stair layers.</summary>
    public bool Stairs { get; set; } = true;
    /// <summary>Columns, chimneys, fittings, balconies, rooflights, railings and odd-shaped wall fills as generic models.</summary>
    public bool Details { get; set; } = true;
    /// <summary>Flat concept roofs, only where a roof plan or a roof layer shows them.</summary>
    public bool Roofs { get; set; } = true;
    /// <summary>Section views along the cut lines of the survey sections.</summary>
    public bool Sections { get; set; } = true;
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
    static double Mm(double ft) => ft / UnitConversion.MmToFeet(1);

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
        if (o.Stairs) BuildStairs(levels);
        if (o.Roofs && _model.Roofs.Count > 0) BuildRoofs(levels.Values.Select(v => v.level).ToList());
        if (o.Sections && _model.Sections.Any(x => x.Cut != null)) BuildSections(levels.Values.Select(v => v.level).ToList());
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
        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
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
        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
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
            t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
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
        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
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
        var wallTypes = new Dictionary<double, WallType>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (sl, (level, view)) = (ordered[i].Key, ordered[i].Value);
            var above = i + 1 < ordered.Count ? ordered[i + 1].Value.level : null;
            int removed = 0;
            var done = new List<string>();
            Step($"{sl.Name}: clearing an earlier run", done, () => removed = DeleteGenerated(sl.Code));

            var created = new Dictionary<WallCandidate, Wall>(ReferenceEqualityComparer.Instance);
            if (o.Walls) Step($"{sl.Name}: walls, windows and doors", done, () =>
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
                        WallType type;
                        if (w.Curtain) type = _curtainType ??= CurtainTypeFor();
                        else if (!wallTypes.TryGetValue(w.TypeThickness, out type!)) wallTypes[w.TypeThickness] = type = WallTypeFor(w.TypeThickness);
                        var wall = Wall.Create(_doc, line, type.Id, level.Id, Ft(w.HeightMm ?? sl.WallHeightMm), 0, false, false);
                        // a low wall (balustrade, parapet) keeps its own height; the rest run up to the next level
                        if (above != null && w.HeightMm == null) wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(above.Id);
                        Mark(wall, sl.Code, w.HeightMm != null ? "Parapet" : w.Confidence.ToString());
                        created[w] = wall;
                        made++;
                    }
                    catch (Exception ex) { skipped++; if (skipped <= 5) Log.Add($"{sl.Name}: a wall at ({c.A.X:0}, {c.A.Y:0}) mm was skipped: {ex.Message}"); }
                }
                done.Add($"{made} walls" + (skipped > 0 ? $" ({skipped} skipped)" : ""));
                _placed.Clear();
                if (o.Windows && sl.Windows.Count > 0) done.Add(CreateWindows(sl, level, created));
                if (o.Doors && sl.Doors.Count > 0) done.Add(CreateDoors(sl, level, created));
            });

            var loop = sl.Outline != null ? Loop(sl.Outline, level.Elevation) : null;
            Floor? floor = null;
            if (o.Floors)
            {
                if (loop == null) done.Add("no floor (outline not found)");
                else Step($"{sl.Name}: floor slab", done, () =>
                {
                    floor = Floor.Create(_doc, new List<CurveLoop> { loop }, FloorTypeFor(o.SlabThicknessMm).Id, level.Id);
                    Mark(floor, sl.Code, "Floor");
                    done.Add($"floor {sl.Outline!.Area / 1e6:0} m²");
                });
                if (floor != null && floor.IsValidObject && o.Stairs && sl.StairVoids.Count > 0)
                    Step($"{sl.Name}: stair openings", done, () => done.Add(StairVoids(floor, sl, level)));
            }
            if (o.Ceilings && loop != null) Step($"{sl.Name}: ceiling", done, () =>
            {
                try
                {
                    var ceiling = Ceiling.Create(_doc, new List<CurveLoop> { loop }, CeilingTypeFor().Id, level.Id);
                    ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(Ft(sl.CeilingHeightMm));
                    Mark(ceiling, sl.Code, "Ceiling");
                    done.Add($"ceiling at {sl.CeilingHeightMm:0} mm");
                }
                catch (Exception ex) { done.Add($"no ceiling ({ex.Message})"); }
            });
            if (o.UnclearAsGenericModels && sl.Unclear.Count > 0) Step($"{sl.Name}: areas to check", done, () =>
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
            });
            if (o.Details && sl.Details.Count > 0) Step($"{sl.Name}: generic models", done, () => done.Add(BuildDetails(sl, level)));
            if (_placed.Count > 0) Step($"{sl.Name}: openings check", done, () => done.Add(Unblock(sl.Code, level)));
            if (o.Rooms && o.Walls) Step($"{sl.Name}: rooms", done, () =>
            {
                try { done.Add(CreateRooms(sl, level, view)); }
                catch (Exception ex) { done.Add($"no rooms ({ex.Message})"); }
            });
            Log.Add($"{sl.Name}: {string.Join(", ", done)}" + (removed > 0 ? $" (replacing {removed} elements from an earlier run)." : "."));
        }
    }

    /// <summary>
    /// One part of a floor in its own transaction, so a part Revit refuses (and rolls back)
    /// costs only that part, never the floor or the whole build; the log says which part.
    /// </summary>
    void Step(string what, List<string> done, Action build)
    {
        using var t = new Transaction(_doc, "Survey: " + what);
        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()).SetClearAfterRollback(true));
        t.Start();
        try { build(); }
        catch (Exception ex) { t.RollBack(); done.Add($"{what} failed ({ex.Message})"); Log.Add($"WARNING: {what} failed: {ex.Message}"); return; }
        if (t.Commit() != TransactionStatus.Committed) { done.Add($"{what} refused by Revit"); Log.Add($"WARNING: {what} was refused by Revit and left out."); }
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
    /// <summary>
    /// Stairs, each in its own stairs edit (Revit builds stairs outside the main transaction):
    /// one Revit stair per survey stair, from its floor to the next level, a sketched run per
    /// flight made from the drawn risers (so curved flights stay curved), landings between
    /// flights where Revit can make them. A stair Revit refuses is built as stepped generic
    /// model blocks ("Survey - stair") instead, so nothing is lost.
    /// </summary>
    void BuildStairs(Dictionary<SurveyLevel, (Level level, ViewPlan? view)> levels)
    {
        var ordered = levels.OrderBy(kv => kv.Value.level.Elevation).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (sl, (level, _)) = (ordered[i].Key, ordered[i].Value);
            if (sl.Stairs.Count == 0) continue;
            var above = i + 1 < ordered.Count ? ordered[i + 1].Value.level : null;
            double rise = above != null ? (above.Elevation - level.Elevation) * 304.8 : sl.WallHeightMm;
            int native = 0, blocks = 0, unknown = 0;
            string? firstError = null;
            foreach (var stair in sl.Stairs)
            {
                if (!stair.DirectionKnown) unknown++;
                bool ok = false;
                if (above != null)
                    try { ok = NativeStair(stair, level, above, sl.Code, rise); }
                    catch (Exception ex) { firstError ??= ex.Message; }
                if (ok) { native++; continue; }
                using var t = new Transaction(_doc, "Survey stair (blocks)");
                t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
                t.Start();
                try { StairBlocks(stair, level, sl.Code, rise); blocks++; t.Commit(); }
                catch (Exception ex) { firstError ??= ex.Message; t.RollBack(); }
            }
            Log.Add($"{sl.Name}: {native} stairs" + (blocks > 0 ? $", {blocks} as generic model blocks" + (firstError != null ? $" (Revit refused the stair: {firstError})" : "") : "")
                    + (unknown > 0 ? $"; {unknown} with no UP note: climbing direction guessed, check" : ""));
        }
    }

    bool NativeStair(StairCandidate stair, Level level, Level above, string code, double riseMm)
    {
        int risers = Math.Max(3, stair.Risers);
        double riserMm = riseMm / risers;
        using var scope = new StairsEditScope(_doc, "Survey stair");
        var id = scope.Start(level.Id, above.Id);
        using (var t = new Transaction(_doc, "Survey stair runs"))
        {
            t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
            t.Start();
            var runs = new List<Autodesk.Revit.DB.Architecture.StairsRun>();
            int done = 0;
            foreach (var f in stair.Flights)
            {
                XYZ P(Vec2 v) => new(Ft(v.X), Ft(v.Y), 0);
                var left = f.Risers.Select(r => P(r.A)).ToList();
                var right = f.Risers.Select(r => P(r.B)).ToList();
                var boundaries = new List<Curve>();
                for (int k = 1; k < left.Count; k++) boundaries.Add(Line.CreateBound(left[k - 1], left[k]));
                for (int k = 1; k < right.Count; k++) boundaries.Add(Line.CreateBound(right[k - 1], right[k]));
                var riserCurves = f.Risers.Select(r => (Curve)Line.CreateBound(P(r.A), P(r.B))).ToList();
                var path = new List<Curve>();
                for (int k = 1; k < f.Risers.Count; k++) path.Add(Line.CreateBound(P(f.Risers[k - 1].Mid), P(f.Risers[k].Mid)));
                runs.Add(Autodesk.Revit.DB.Architecture.StairsRun.CreateSketchedRun(_doc, id, Ft(done * riserMm), boundaries, riserCurves, path));
                done += f.Risers.Count;
            }
            for (int k = 1; k < runs.Count; k++)
                try { Autodesk.Revit.DB.Architecture.StairsLanding.CreateAutomaticLanding(_doc, runs[k - 1].Id, runs[k].Id); } catch { /* no landing fits: the runs stand alone */ }
            if (_doc.GetElement(id) is Autodesk.Revit.DB.Architecture.Stairs s)
            {
                try { s.DesiredRisersNumber = risers; } catch { /* Revit keeps its own count */ }
                Mark(s, code, stair.DirectionKnown ? "Stair" : "Stair (direction guessed)");
            }
            t.Commit();
        }
        scope.Commit(new WarningSwallower());
        return _doc.GetElement(id) != null;
    }

    static readonly Dictionary<LayerRole, string> DetailNames = new()
    {
        [LayerRole.Wall] = "Survey - pier", [LayerRole.Column] = "Survey - column", [LayerRole.Chimney] = "Survey - chimney",
        [LayerRole.Fitting] = "Survey - fitting", [LayerRole.Balcony] = "Survey - balcony", [LayerRole.Rooflight] = "Survey - rooflight",
        [LayerRole.Railing] = "Survey - railing",
    };

    /// <summary>Detail from the plan as generic models (DirectShape), one per shape, named by what it is.</summary>
    string BuildDetails(SurveyLevel sl, Level level)
    {
        int made = 0, refused = 0;
        foreach (var d in sl.Details)
        {
            try
            {
                if (Loop(d.Outline, level.Elevation + Ft(d.BaseMm)) is not CurveLoop loop) { refused++; continue; }
                var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, Ft(d.HeightMm));
                var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                ds.SetShape(new List<GeometryObject> { solid });
                ds.Name = DetailNames.TryGetValue(d.Role, out var n) ? n : "Survey - detail";
                Mark(ds, sl.Code, "Detail: " + d.Role);
                made++;
            }
            catch { refused++; /* a self-crossing outline Revit refuses is skipped */ }
        }
        var counts = sl.Details.GroupBy(d => d.Role).Select(g => $"{g.Count()} {(DetailNames.TryGetValue(g.Key, out var n) ? n[9..] : "detail")}");
        return $"{made} detail generic models ({string.Join(", ", counts)})" + (refused > 0 ? $", {refused} refused by Revit" : "");
    }

    /// <summary>Openings in the slab over the stairs arriving from the floor below (the stairs' footprints).</summary>
    string StairVoids(Floor floor, SurveyLevel sl, Level level)
    {
        int made = 0, refused = 0;
        foreach (var v in sl.StairVoids)
        {
            try
            {
                if (Loop(v, level.Elevation) is not CurveLoop loop) { refused++; continue; }
                var curves = new CurveArray();
                foreach (var c in loop) curves.Append(c);
                var opening = _doc.Create.NewOpening(floor, curves, false);
                Mark(opening, sl.Code, "Stair opening");
                made++;
            }
            catch { refused++; }
        }
        return $"{made} stair openings in the slab" + (refused > 0 ? $" ({refused} refused by Revit)" : "");
    }

    /// <summary>
    /// A section view along each survey section's cut line (placed from the key plan on the
    /// section sheet), named "Survey Section A" etc., from below the lowest level to above the
    /// highest. Views of an earlier run with the same names are replaced.
    /// </summary>
    void BuildSections(List<Level> levels)
    {
        var type = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);
        if (type == null) { Log.Add("Sections: the project has no section view type; no section views made."); return; }
        using var t = new Transaction(_doc, "Survey sections");
        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
        t.Start();
        double low = levels.Min(l => l.Elevation) - Ft(2000), high = levels.Max(l => l.Elevation) + Ft(6000);
        var made = new List<string>();
        foreach (var sec in _model.Sections.Where(x => x.Cut != null))
        {
            var name = $"Survey Section {sec.Name}";
            var old = new FilteredElementCollector(_doc).OfClass(typeof(ViewSection)).Cast<ViewSection>().Where(v => !v.IsTemplate && v.Name == name).Select(v => v.Id).ToList();
            try { if (old.Count > 0) _doc.Delete(old); } catch { /* open view: keep it, Revit names the new one */ }
            var c = sec.Cut!;
            var a = new XYZ(Ft(c.A.X), Ft(c.A.Y), 0); var b = new XYZ(Ft(c.B.X), Ft(c.B.Y), 0);
            var dir = (b - a).Normalize();
            var tr = Transform.Identity;
            tr.Origin = (a + b) / 2;
            tr.BasisX = dir;
            tr.BasisY = XYZ.BasisZ;
            tr.BasisZ = dir.CrossProduct(XYZ.BasisZ);
            double half = a.DistanceTo(b) / 2 + Ft(1000);
            var box = new BoundingBoxXYZ { Transform = tr, Min = new XYZ(-half, low, 0), Max = new XYZ(half, high, Ft(30000)) };
            try
            {
                var view = ViewSection.CreateSection(_doc, type.Id, box);
                try { view.Name = name; } catch { /* name taken: keep Revit's */ }
                made.Add(view.Name);
            }
            catch (Exception ex) { Log.Add($"Sections: section {sec.Name} not made ({ex.Message})."); }
        }
        t.Commit();
        if (made.Count > 0) Log.Add($"Sections: {string.Join(", ", made)} along the survey's cut lines. Compare them with the survey section sheet; the direction looked in may need flipping.");
    }

    const string RoofCode = "ROOF";

    /// <summary>
    /// Flat concept roofs (footprint roofs, "Concept Roof 300mm") from the roof plan or roof layers,
    /// each with its top at its surveyed height, hosted on the highest level below it.
    /// A roof Revit refuses is built as a 300 mm generic model slab instead.
    /// </summary>
    void BuildRoofs(List<Level> levels)
    {
        using var t = new Transaction(_doc, "Survey roofs");
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new WarningSwallower());
        t.SetFailureHandlingOptions(options);
        t.Start();
        int removed = DeleteGenerated(RoofCode);
        var type = RoofTypeFor(300);
        double thick = type?.GetCompoundStructure()?.GetWidth() ?? Ft(300);
        int native = 0, blocks = 0;
        string? firstError = null;
        foreach (var r in _model.Roofs)
        {
            double top = Ft(r.ElevationMm);
            var level = levels.Where(l => l.Elevation <= top - thick).OrderByDescending(l => l.Elevation).FirstOrDefault() ?? levels.OrderBy(l => l.Elevation).First();
            try
            {
                if (type == null) throw new InvalidOperationException("the project has no roof type");
                var curves = new CurveArray();
                var loop = Loop(r.Outline, level.Elevation) ?? throw new InvalidOperationException("outline too small");
                foreach (var c in loop) curves.Append(c);
                var roof = _doc.Create.NewFootPrintRoof(curves, level, type, out ModelCurveArray footprint);
                roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(top - thick - level.Elevation);
                Mark(roof, RoofCode, r.Note);
                native++;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message + (ex is ArgumentException ae && ae.ParamName != null ? $" [{ae.ParamName}]" : "") + $" at {ex.TargetSite?.Name}";
                try
                {
                    var loop = Loop(r.Outline, top - Ft(300));
                    if (loop == null) continue;
                    var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    ds.SetShape(new List<GeometryObject> { GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, Ft(300)) });
                    ds.Name = "Survey - roof";
                    Mark(ds, RoofCode, r.Note);
                    blocks++;
                }
                catch { /* a sliver */ }
            }
        }
        t.Commit();
        Log.Add($"Roofs: {native} flat concept roofs" + (blocks > 0 ? $", {blocks} as generic model slabs" + (firstError != null ? $" (Revit refused the roof: {firstError})" : "") : "")
                + (removed > 0 ? $" (replacing {removed} from an earlier run)." : ".") + " Heights are the middle of the spot heights on each roof; slopes are not modelled.");
    }

    RoofType? RoofTypeFor(double thicknessMm)
    {
        var name = $"Concept Roof {thicknessMm:0}mm";
        var all = new FilteredElementCollector(_doc).OfClass(typeof(RoofType)).Cast<RoofType>().ToList();
        var type = all.FirstOrDefault(f => f.Name == name);
        if (type != null) return type;
        var basic = all.FirstOrDefault(f => f.GetCompoundStructure() != null);
        if (basic == null) return all.FirstOrDefault();
        type = (RoofType)basic.Duplicate(name);
        Log.Add($"Roof type created: {name}");
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

    /// <summary>A stair as stepped blocks: each tread a block one riser high, climbing in the stair's order.</summary>
    void StairBlocks(StairCandidate stair, Level level, string code, double riseMm)
    {
        double riser = riseMm / Math.Max(3, stair.Risers);
        var solids = new List<GeometryObject>();
        int step = 0;
        foreach (var f in stair.Flights)
            for (int k = 0; k < f.Risers.Count; k++, step++)
            {
                var a = f.Risers[k];
                // the tread runs from this riser to the next (the last one a typical going on)
                var b = k + 1 < f.Risers.Count ? f.Risers[k + 1]
                      : a with { A = a.A + (a.Mid - f.Risers[Math.Max(0, k - 1)].Mid).Normalized() * f.Going, B = a.B + (a.Mid - f.Risers[Math.Max(0, k - 1)].Mid).Normalized() * f.Going };
                double z = level.Elevation + Ft(step * riser);
                var pts = new[] { a.A, a.B, b.B, b.A }.Select(v => new XYZ(Ft(v.X), Ft(v.Y), z)).ToList();
                var loop = new CurveLoop();
                for (int q = 0; q < 4; q++) loop.Append(Line.CreateBound(pts[q], pts[(q + 1) % 4]));
                try { solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, Ft(riser))); } catch { /* a twisted tread */ }
            }
        var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
        ds.SetShape(solids);
        ds.Name = "Survey - stair";
        Mark(ds, code, "Stair blocks");
    }

    void Mark(Element e, string levelCode, string what)
    {
        e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{Tag}|{levelCode}|{what}");
        var phase = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
        if (_phase != null && phase != null && !phase.IsReadOnly) phase.Set(_phase.Id);
    }

    /// <summary>
    /// Closed loop at the given height; edges too short for Revit are dropped, and edges a hair
    /// off square (under 1°, ends under 30 mm out) are made exactly square, so Revit does not
    /// warn "Line in Sketch is slightly off axis".
    /// </summary>
    static CurveLoop? Loop(Polygon2 poly, double z)
    {
        // drop points on a straight run and spikes (the outline running out and straight back)
        var list = poly.Vertices.Select(v => (X: v.X, Y: v.Y)).ToList();
        for (bool changed = true; changed && list.Count > 3;)
        {
            changed = false;
            for (int i = 0; i < list.Count && list.Count > 3; i++)
            {
                var p = list[(i + list.Count - 1) % list.Count]; var c = list[i]; var q = list[(i + 1) % list.Count];
                double len = Math.Max(1e-6, Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y)));
                double cross = (c.X - p.X) * (q.Y - p.Y) - (c.Y - p.Y) * (q.X - p.X);
                if (Math.Abs(cross) / len < 1) { list.RemoveAt(i); changed = true; i--; }
            }
        }
        var raw = list.ToArray();
        var vs = raw.ToArray();
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < vs.Length; i++)
            {
                int j = (i + 1) % vs.Length;
                double dx = vs[j].X - vs[i].X, dy = vs[j].Y - vs[i].Y;
                if (Math.Abs(dy) > 0 && Math.Abs(dy) < 30 && Math.Abs(dy) < Math.Abs(dx) * 0.0175) { double y = (vs[i].Y + vs[j].Y) / 2; vs[i].Y = y; vs[j].Y = y; }
                else if (Math.Abs(dx) > 0 && Math.Abs(dx) < 30 && Math.Abs(dx) < Math.Abs(dy) * 0.0175) { double x = (vs[i].X + vs[j].X) / 2; vs[i].X = x; vs[j].X = x; }
            }
        // squaring must never make the outline cross itself (narrow notches): then keep it as drawn
        if (SelfCrossing(vs)) vs = raw;
        var pts = new List<XYZ>();
        foreach (var v in vs)
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

    static bool SelfCrossing((double X, double Y)[] v)
    {
        static double Cr((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        int n = v.Length;
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                var a = v[i]; var b = v[(i + 1) % n]; var c = v[j]; var d = v[(j + 1) % n];
                if (Cr(a, b, c) * Cr(a, b, d) <= 0 && Cr(c, d, a) * Cr(c, d, b) <= 0 &&
                    Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) <= Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) &&
                    Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) <= Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y))) return true;
            }
        return false;
    }

    int DeleteGenerated(string levelCode)
    {
        var prefix = $"{Tag}|{levelCode}|";
        var classes = new ElementMulticlassFilter(new List<Type> { typeof(Wall), typeof(Floor), typeof(Ceiling), typeof(DirectShape), typeof(FamilyInstance), typeof(Opening), typeof(SpatialElement), typeof(Toposolid), typeof(CurveElement), typeof(Autodesk.Revit.DB.Architecture.Stairs), typeof(RoofBase) });
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
            sl.Windows.Select(w => (w.HostIndex, w.Center, w.WidthMm, w.SillMm, w.HeadMm, w.Outward, (Vec2?)null, (Vec2?)null)));
        return real ? $"{made} windows" + (failed > 0 ? $" ({failed} did not fit)" : "")
                    : $"{made} window openings (no window family found)";
    }

    /// <summary>
    /// Doors hosted in the generated walls, as windows: a single door family, and a double one for double
    /// doors (the single one if there is none), a type per size ("Concept Door 900x2100"). Each is turned
    /// to open into the side and hang on the jamb the survey's swing shows.
    /// </summary>
    string CreateDoors(SurveyLevel sl, Level level, Dictionary<WallCandidate, Wall> created)
    {
        var (made, failed, real) = PlaceHosted(sl, level, created, BuiltInCategory.OST_Doors, "Door",
            sl.Doors.Where(d => !d.Double).Select(d => (d.HostIndex, d.Center, d.WidthMm, 0.0, d.HeightMm, (Vec2?)null, d.Hinge, d.Swing)));
        var doubles = sl.Doors.Where(d => d.Double).ToList();
        int made2 = 0, failed2 = 0;
        if (doubles.Count > 0)
            (made2, failed2, _) = PlaceHosted(sl, level, created, BuiltInCategory.OST_Doors, "Double Door",
                doubles.Select(d => (d.HostIndex, d.Center, d.WidthMm, 0.0, d.HeightMm, (Vec2?)null, (Vec2?)null, d.Swing)));
        return (real ? $"{made} doors" : $"{made} door openings (no door family found)")
               + (made2 > 0 ? $", {made2} double doors" : "")
               + (failed + failed2 > 0 ? $" ({failed + failed2} did not fit)" : "");
    }

    (int Made, int Failed, bool Real) PlaceHosted(SurveyLevel sl, Level level, Dictionary<WallCandidate, Wall> created,
        BuiltInCategory category, string what,
        IEnumerable<(int Host, Vec2 Center, double Width, double Bottom, double Top, Vec2? Outward, Vec2? Hinge, Vec2? Swing)> items)
    {
        var family = HostedFamily(category, what);
        var symbols = new Dictionary<(double, double), FamilySymbol>();
        int made = 0, failed = 0;
        foreach (var (hostIndex, center, width, bottom, top, outward, hinge, swing) in items)
        {
            if (!created.TryGetValue(sl.Walls.Walls[hostIndex], out var wall) || sl.Walls.Walls[hostIndex].Curtain) continue;
            var c = new XYZ(Ft(center.X), Ft(center.Y), level.Elevation);
            try
            {
                if (family != null)
                {
                    double w = Math.Round(width / 50) * 50, h = Math.Round((top - bottom) / 50) * 50;
                    if (!symbols.TryGetValue((w, h), out var symbol)) symbols[(w, h)] = symbol = HostedType(family, what, w, h);
                    var inst = _doc.Create.NewFamilyInstance(c, symbol, wall, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    if (category == BuiltInCategory.OST_Windows) inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(Ft(bottom));
                    // the window's outer face to the outside of the building
                    if (outward is Vec2 o && inst.CanFlipFacing && inst.FacingOrientation.DotProduct(new XYZ(o.X, o.Y, 0)) < 0) inst.flipFacing();
                    // a door: open to the side, and hang on the jamb, the survey's swing shows
                    if (category == BuiltInCategory.OST_Doors && (hinge != null || swing != null)) TurnDoor(inst, level, c, hinge, swing);
                    Mark(inst, sl.Code, what);
                    _placed.Add(new Placed(inst, wall, c, width, bottom, top, what));
                }
                else Cut(wall, c, width, bottom, top, sl.Code, what + " opening");
                made++;
            }
            catch { failed++; continue; }
            // a lining or second skin drawn as its own wall, or the next wall when the opening runs past its host's end
            foreach (var (k, at, w) in OpeningCuts.Across(sl.Walls.Walls, hostIndex, center, width))
                if (created.TryGetValue(sl.Walls.Walls[k], out var other))
                    try { Cut(other, new XYZ(Ft(at.X), Ft(at.Y), level.Elevation), w, bottom, top, sl.Code, what + " opening"); }
                    catch { /* does not reach this wall */ }
        }
        return (made, failed, family != null);
    }

    readonly record struct Placed(FamilyInstance Inst, Wall Host, XYZ At, double WidthMm, double BottomMm, double TopMm, string What);
    readonly List<Placed> _placed = new();

    /// <summary>
    /// Checks in Revit's own geometry that every window and door on the floor is open right through, once
    /// everything on the floor is built, and clears what still fills it: a parallel wall is cut, our own
    /// generic model (a pier) has the opening carved out, a short wall of ours running across the opening
    /// is removed, and a host the window family did not cut is cut by hand. Anything else is counted.
    /// </summary>
    string Unblock(string code, Level level)
    {
        _doc.Regenerate();
        int cut = 0, carved = 0, removed = 0, hosts = 0, left = 0, clear = 0;
        var cats = new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_GenericModel });
        foreach (var p in _placed)
        {
            if (!p.Inst.IsValidObject || !p.Host.IsValidObject || p.Host.Location is not LocationCurve hostCurve) continue;
            var on = hostCurve.Curve.Project(p.At);
            var u = (on != null ? hostCurve.Curve.ComputeDerivatives(on.Parameter, false).BasisX : hostCurve.Curve.GetEndPoint(1) - hostCurve.Curve.GetEndPoint(0)).Normalize();
            var at = on?.XYZPoint ?? p.At;
            var n = XYZ.BasisZ.CrossProduct(u);
            // a box inside the opening, kept clear of its jambs, sill and head, reaching 400 mm out each side of the wall
            double half = Ft(p.WidthMm / 2 - 60), depth = p.Host.Width / 2 + Ft(400);
            double z0 = level.Elevation + Ft(p.BottomMm + 60), z1 = level.Elevation + Ft(p.TopMm - 60);
            if (half <= 0 || z1 <= z0) continue;
            var b = new XYZ(at.X, at.Y, z0);
            var corners = new[] { b - u * half - n * depth, b + u * half - n * depth, b + u * half + n * depth, b - u * half + n * depth };
            var loop = CurveLoop.Create(Enumerable.Range(0, 4).Select(i => (Curve)Line.CreateBound(corners[i], corners[(i + 1) % 4])).ToList());
            var box = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, z1 - z0);
            var hits = new FilteredElementCollector(_doc).WherePasses(cats).WherePasses(new ElementIntersectsSolidFilter(box))
                .Where(e => e.Id != p.Inst.Id).ToList();
            if (hits.Count == 0) { clear++; continue; }
            foreach (var e in hits)
            {
                try
                {
                    if (e is Wall { WallType.Kind: WallKind.Curtain }) continue; // glazing already
                    if (e.Id == p.Host.Id) { Cut(p.Host, at, p.WidthMm, p.BottomMm, p.TopMm, code, p.What + " opening"); hosts++; }
                    else if (e is Wall w && w.Location is LocationCurve lc && lc.Curve is Line line && Math.Abs(line.Direction.DotProduct(u)) > 0.99)
                    {
                        // the part of the opening that overlaps this wall, on its own line
                        var mid = line.Project(at).XYZPoint;
                        double s0 = (line.GetEndPoint(0) - mid).DotProduct(u), s1 = (line.GetEndPoint(1) - mid).DotProduct(u);
                        double from = Math.Max(-Ft(p.WidthMm / 2), Math.Min(s0, s1)), to = Math.Min(Ft(p.WidthMm / 2), Math.Max(s0, s1));
                        if (to - from < Ft(30)) continue;
                        Cut(w, mid + u * ((from + to) / 2), Mm(to - from), p.BottomMm, p.TopMm, code, p.What + " opening");
                        cut++;
                    }
                    else if (e is Wall across && Ours(across) && across.Location is LocationCurve ac && ac.Curve.Length < Ft(1500)
                             && across.FindInserts(true, false, true, true).Count == 0)
                    { _doc.Delete(across.Id); removed++; }
                    else if (e is DirectShape ds && Ours(ds) && Carve(ds, box)) carved++;
                    else { left++; if (left <= 5) Log.Add($"{code}: {p.What.ToLowerInvariant()} at ({Mm(at.X):0}, {Mm(at.Y):0}) mm still blocked by {e.Category?.Name} '{e.Name}' (id {e.Id.Value})."); }
                }
                catch (Exception ex) { left++; if (left <= 5) Log.Add($"{code}: could not clear {e.Category?.Name} id {e.Id.Value} from a {p.What.ToLowerInvariant()}: {ex.Message}"); }
            }
        }
        return $"openings checked: {clear} clear" + (cut > 0 ? $", {cut} more walls cut" : "") + (carved > 0 ? $", {carved} generic models carved" : "")
               + (removed > 0 ? $", {removed} short walls across openings removed" : "") + (hosts > 0 ? $", {hosts} host walls cut by hand (the family did not cut them)" : "")
               + (left > 0 ? $", {left} still blocked (see above)" : "");
    }

    static bool Ours(Element e) =>
        e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(Tag + "|") == true;

    /// <summary>Takes the opening's box out of a generic model of ours.</summary>
    static bool Carve(DirectShape ds, Solid box)
    {
        var left = new List<GeometryObject>();
        foreach (var o in ds.get_Geometry(new Options()) ?? Enumerable.Empty<GeometryObject>())
        {
            if (o is not Solid s || s.Volume <= 0) continue;
            var rest = BooleanOperationsUtils.ExecuteBooleanOperation(s, box, BooleanOperationsType.Difference);
            if (rest != null && rest.Volume > 1e-6) left.Add(rest);
        }
        if (left.Count == 0) { ds.Document.Delete(ds.Id); return true; }
        ds.SetShape(left);
        return true;
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

    readonly Dictionary<string, Family?> _families = new();
    readonly Dictionary<ElementId, (int Hinge, int Swing, int HingeAfterFacing)?> _swingOf = new();

    /// <summary>
    /// Turns a door to open into the side of the wall the survey's leaf is on, hung on the survey's hinge
    /// jamb. Families differ in which way their swing is drawn, so the first door of each family is
    /// measured from its plan swing arc (and once more flipped) before any are turned.
    /// </summary>
    void TurnDoor(FamilyInstance door, Level level, XYZ at, Vec2? hinge, Vec2? swing)
    {
        if (!_swingOf.TryGetValue(door.Symbol.Family.Id, out var how)) _swingOf[door.Symbol.Family.Id] = how = MeasureSwing(door, level, at);
        if (how is not var (hingeSign, swingSign, hingeAfterFacing)) return;
        // where the hinge and the leaf are now, in plan
        var hingeNow = door.HandOrientation * hingeSign;
        var swingNow = door.FacingOrientation * swingSign;
        if (swing is Vec2 s && door.CanFlipFacing && swingNow.DotProduct(new XYZ(Ft(s.X), Ft(s.Y), at.Z) - at) < 0)
        {
            door.flipFacing();
            hingeNow = hingeNow * hingeAfterFacing;
        }
        if (hinge is Vec2 h && door.CanFlipHand && hingeNow.DotProduct(new XYZ(Ft(h.X), Ft(h.Y), at.Z) - at) < 0) door.flipHand();
    }

    /// <summary>
    /// For a door family: the hinge's side along the door's hand direction and the leaf's side along its facing
    /// (+1 or -1), read from the swing arc in a plan view; and whether flipping the facing also moves the hinge
    /// to the other jamb (-1) or not (+1). Null when the family draws no swing arc.
    /// </summary>
    (int, int, int)? MeasureSwing(FamilyInstance door, Level level, XYZ at)
    {
        var view = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id);
        if (view == null) return null;
        Arc? Swing()
        {
            _doc.Regenerate();
            var found = new List<Arc>();
            void Walk(GeometryElement? g)
            {
                if (g == null) return;
                foreach (var o in g)
                    if (o is Arc a) found.Add(a);
                    else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
            }
            Walk(door.get_Geometry(new Options { View = view }));
            return found.OrderByDescending(a => a.Radius).FirstOrDefault();
        }
        var before = Swing();
        if (before == null || !door.CanFlipFacing) return null;
        int hinge = Math.Sign((before.Center - at).DotProduct(door.HandOrientation));
        int leaf = Math.Sign((before.Evaluate(0.5, true) - at).DotProduct(door.FacingOrientation));
        var hingeWorld = before.Center - at;
        door.flipFacing();
        var after = Swing();
        door.flipFacing();
        int moves = after != null && (after.Center - at).DotProduct(hingeWorld) < 0 ? -1 : 1;
        if (hinge == 0 || leaf == 0) return null;
        Log.Add($"Door family '{door.Symbol.Family.Name}': swing read from its plan symbol.");
        return (hinge, leaf, moves);
    }

    /// <summary>
    /// A wall-hosted family of the category: one in the project (a fixed window or a single
    /// door if there is one), else Revit's own from the library; null if there is none.
    /// </summary>
    Family? HostedFamily(BuiltInCategory category, string what)
    {
        if (_families.TryGetValue(what, out var cached)) return cached;
        bool dbl = what.StartsWith("Double", StringComparison.OrdinalIgnoreCase);
        string prefer = category == BuiltInCategory.OST_Windows ? "Fixed" : dbl ? "Double" : "Single";
        var inProject = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => f.FamilyCategory?.Id.Value == (long)category && f.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted)
            .Where(f => dbl || !f.Name.Contains("Double", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var family = inProject.FirstOrDefault(f => f.Name.Contains(prefer, StringComparison.OrdinalIgnoreCase)) ?? (dbl ? null : inProject.FirstOrDefault());
        if (family == null)
            foreach (var root in _doc.Application.GetLibraryPaths().Values.Where(Directory.Exists))
            {
                List<string> files = new();
                try
                {
                    files = Directory.EnumerateFiles(root, $"*{prefer}*.rfa", SearchOption.AllDirectories)
                        .Where(f => f.Contains(dbl ? "Door" : what, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
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
        else if (dbl)
        {
            Log.Add("No double door family in the project or Revit library: double doors use the single door family. Load one and run again for real double doors.");
            family = HostedFamily(category, "Door");
        }
        else Log.Add($"No wall-hosted {what.ToLowerInvariant()} family in the project or Revit library: {what.ToLowerInvariant()}s are cut as plain openings. Load one and run again for real {what.ToLowerInvariant()}s.");
        return _families[what] = family;
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
    string CreateRooms(SurveyLevel sl, Level level, ViewPlan? view)
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
        // a space with no label stays a room where the drawing shows a room there (and it is not a sliver)
        var unnamed = placed.Where(r => !names.ContainsKey(r)).ToList();
        var keep = unnamed.Where(r => r.Area >= Ft(1000) * Ft(2000) && r.Location is LocationPoint lp
            && sl.IsRoom?.Invoke(new Vec2(lp.Point.X * 304.8, lp.Point.Y * 304.8)) == true).ToList();
        var drop = unnamed.Except(keep).Select(r => r.Id).ToList();
        if (drop.Count > 0) _doc.Delete(drop);
        foreach (var (room, list) in names)
        {
            room.Name = string.Join(" / ", list);
            room.UpperLimit = level;
            room.LimitOffset = Ft(sl.CeilingHeightMm);
            Mark(room, sl.Code, "Room");
        }
        foreach (var room in keep)
        {
            room.Name = "Room";
            room.UpperLimit = level;
            room.LimitOffset = Ft(sl.CeilingHeightMm);
            Mark(room, sl.Code, "Room (no label)");
        }
        return $"{names.Count} named rooms, {keep.Count} unnamed" + (outside > 0 ? $" ({outside} labels not inside a closed space)" : "")
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

    WallType? _curtainType;

    /// <summary>"Concept Curtain Wall": a copy of the project's first curtain wall type (glazed screens and partitions).</summary>
    WallType CurtainTypeFor()
    {
        const string name = "Concept Curtain Wall";
        var all = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        var found = all.FirstOrDefault(w => w.Name == name);
        if (found != null) return found;
        var curtain = all.FirstOrDefault(w => w.Kind == WallKind.Curtain);
        if (curtain == null) { Log.Add("No curtain wall type in the project: glazed screens are built as thin walls."); return WallTypeFor(60); }
        var type = (WallType)curtain.Duplicate(name);
        Log.Add($"Wall type created: {name} (from {curtain.Name})");
        return type;
    }

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
    internal sealed class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            bool resolved = false;
            foreach (var f in a.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                else if (f.GetSeverity() == FailureSeverity.Error && f.HasResolutions()) { a.ResolveFailure(f); resolved = true; }
                else if (f.GetSeverity() == FailureSeverity.Error && f.GetFailingElementIds().Count > 0)
                {
                    // no fix offered: drop the elements Revit cannot make rather than lose the whole step
                    try { a.DeleteElements(f.GetFailingElementIds().ToList()); resolved = true; } catch { }
                }
            }
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }
}
