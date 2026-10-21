using Autodesk.Revit.DB;
using SurveyToRevit.Core;
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
    /// <summary>Areas the engine could not read become generic models (DirectShape) for review.</summary>
    public bool UnclearAsGenericModels { get; set; } = true;
    public double SlabThicknessMm { get; set; } = 300;
}

/// <summary>
/// Applies a <see cref="SurveyModel"/> to a Revit document: shared coordinates
/// and true north, levels, DWG links in each level's plan view, walls, floor
/// slabs, ceilings and generic models for unclear areas.
/// Everything generated is tagged in Comments so a re-run replaces only it.
/// </summary>
public sealed class ModelBuilder
{
    public const string Tag = "SurveyToRevit";
    readonly Document _doc;
    readonly SurveyModel _model;
    public List<string> Log { get; } = new();

    public ModelBuilder(Document doc, SurveyModel model) { _doc = doc; _model = model; }

    static double Ft(double mm) => UnitConversion.MmToFeet(mm);

    public void Run(BuildOptions o)
    {
        using var group = new TransactionGroup(_doc, "Build from survey");
        group.Start();
        SetCoordinates();
        var levels = SetUpLevels();
        if (o.LinkPlans) LinkPlans(levels);
        BuildElements(levels, o);
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
                        var line = Line.CreateBound(new XYZ(Ft(c.A.X), Ft(c.A.Y), level.Elevation), new XYZ(Ft(c.B.X), Ft(c.B.Y), level.Elevation));
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
            Log.Add($"{sl.Name}: {string.Join(", ", done)}" + (removed > 0 ? $" (replacing {removed} elements from an earlier run)." : "."));
        }
        t.Commit();
    }

    static void Mark(Element e, string levelCode, string what) =>
        e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{Tag}|{levelCode}|{what}");

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
        var classes = new ElementMulticlassFilter(new List<Type> { typeof(Wall), typeof(Floor), typeof(Ceiling), typeof(DirectShape), typeof(FamilyInstance), typeof(Opening) });
        var ids = new FilteredElementCollector(_doc).WherePasses(classes).WhereElementIsNotElementType()
            .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(prefix) == true)
            .Select(e => e.Id).ToList();
        if (ids.Count > 0) _doc.Delete(ids);
        return ids.Count;
    }

    /// <summary>
    /// Windows hosted in the generated walls. Uses a window family already in the project
    /// (a fixed window if there is one), else loads Revit's own fixed window from the library;
    /// a type per size is made ("Concept Window 1200x1500"). With no window family at all,
    /// plain rectangular openings are cut instead.
    /// </summary>
    string CreateWindows(SurveyLevel sl, Level level, Dictionary<WallCandidate, Wall> created)
    {
        var family = WindowFamily();
        var symbols = new Dictionary<(double, double), FamilySymbol>();
        int made = 0, failed = 0;
        foreach (var win in sl.Windows)
        {
            var host = sl.Walls.Walls[win.HostIndex];
            if (!created.TryGetValue(host, out var wall)) continue;
            var c = new XYZ(Ft(win.Center.X), Ft(win.Center.Y), level.Elevation);
            try
            {
                if (family != null)
                {
                    double w = Math.Round(win.WidthMm / 50) * 50, h = Math.Round(win.HeightMm / 50) * 50;
                    if (!symbols.TryGetValue((w, h), out var symbol)) symbols[(w, h)] = symbol = WindowType(family, w, h);
                    var inst = _doc.Create.NewFamilyInstance(c, symbol, wall, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(Ft(win.SillMm));
                    Mark(inst, sl.Code, "Window");
                }
                else
                {
                    var u = ((Line)((LocationCurve)wall.Location).Curve).Direction * Ft(win.WidthMm / 2);
                    var opening = _doc.Create.NewOpening(wall, c - u + XYZ.BasisZ * Ft(win.SillMm), c + u + XYZ.BasisZ * Ft(win.HeadMm));
                    Mark(opening, sl.Code, "Window opening");
                }
                made++;
            }
            catch { failed++; }
        }
        return family != null ? $"{made} windows" + (failed > 0 ? $" ({failed} did not fit)" : "")
                              : $"{made} window openings (no window family found)";
    }

    Family? _windowFamily;
    bool _windowFamilySearched;

    Family? WindowFamily()
    {
        if (_windowFamilySearched) return _windowFamily;
        _windowFamilySearched = true;
        var windows = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => f.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_Windows).ToList();
        _windowFamily = windows.FirstOrDefault(f => f.Name.Contains("Fixed", StringComparison.OrdinalIgnoreCase)) ?? windows.FirstOrDefault();
        if (_windowFamily != null) return _windowFamily;
        foreach (var root in _doc.Application.GetLibraryPaths().Values.Where(Directory.Exists))
        {
            string? file = null;
            try
            {
                file = Directory.EnumerateFiles(root, "*Fixed*.rfa", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Contains("Window", StringComparison.OrdinalIgnoreCase));
            }
            catch { /* unreadable folder */ }
            if (file != null && _doc.LoadFamily(file, out Family loaded))
            {
                Log.Add($"Window family loaded: {Path.GetFileName(file)}");
                return _windowFamily = loaded;
            }
        }
        Log.Add("No window family in the project or Revit library: windows are cut as plain openings. Load a window family and run again for real windows.");
        return null;
    }

    FamilySymbol WindowType(Family family, double widthMm, double heightMm)
    {
        var name = $"Concept Window {widthMm:0}x{heightMm:0}";
        var ids = family.GetFamilySymbolIds();
        var all = ids.Select(id => (FamilySymbol)_doc.GetElement(id)).ToList();
        var symbol = all.FirstOrDefault(s => s.Name == name);
        if (symbol == null)
        {
            symbol = (FamilySymbol)all.First().Duplicate(name);
            SetLength(symbol, Ft(widthMm), BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, "Width");
            SetLength(symbol, Ft(heightMm), BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, "Height");
        }
        if (!symbol.IsActive) { symbol.Activate(); _doc.Regenerate(); }
        return symbol;
    }

    static void SetLength(Element e, double value, BuiltInParameter first, BuiltInParameter second, string name)
    {
        var p = e.get_Parameter(first) ?? e.get_Parameter(second) ?? e.LookupParameter(name);
        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double) p.Set(value);
    }

    FloorType FloorTypeFor(double thicknessMm)
    {
        var name = $"Concept Slab {thicknessMm:0}mm";
        var all = new FilteredElementCollector(_doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        var found = all.FirstOrDefault(f => f.Name == name);
        if (found != null) return found;
        var basic = all.First(f => !f.IsFoundationSlab && f.GetCompoundStructure() != null);
        var type = (FloorType)basic.Duplicate(name);
        type.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(
            MaterialFunctionAssignment.Structure, Ft(thicknessMm), ElementId.InvalidElementId));
        Log.Add($"Floor type created: {name}");
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

    WallType WallTypeFor(double thicknessMm)
    {
        var name = WallDetector.TypeNameFor(thicknessMm);
        var all = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        var found = all.FirstOrDefault(w => w.Name == name);
        if (found != null) return found;
        var basic = all.First(w => w.Kind == WallKind.Basic);
        var type = (WallType)basic.Duplicate(name);
        type.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(
            MaterialFunctionAssignment.Structure, Ft(thicknessMm), ElementId.InvalidElementId));
        Log.Add($"Wall type created: {name}");
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
