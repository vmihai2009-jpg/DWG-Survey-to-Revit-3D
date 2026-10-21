using Autodesk.Revit.DB;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>
/// Applies a <see cref="SurveyModel"/> to a Revit document: shared coordinates
/// and true north, levels, DWG links in each level's plan view, and walls.
/// Generated walls are tagged in Comments so a re-run replaces only them.
/// </summary>
public sealed class ModelBuilder
{
    public const string Tag = "SurveyToRevit";
    readonly Document _doc;
    readonly SurveyModel _model;
    public List<string> Log { get; } = new();

    public ModelBuilder(Document doc, SurveyModel model) { _doc = doc; _model = model; }

    static double Ft(double mm) => UnitConversion.MmToFeet(mm);

    public void Run(bool linkDrawings, bool createWalls)
    {
        using var group = new TransactionGroup(_doc, "Build from survey");
        group.Start();
        SetCoordinates();
        var levels = SetUpLevels();
        if (linkDrawings) LinkPlans(levels);
        if (createWalls) CreateWalls(levels);
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
        foreach (var sl in _model.Levels.Where(l => !double.IsNaN(l.ElevationMm)))
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
            if (_doc.Link(sl.Plan.Path, options, view, out ElementId id) && _doc.GetElement(id) is ImportInstance inst)
            {
                inst.Pinned = true;
                t.Commit();
                Log.Add($"Linked {file} into '{view.Name}' using shared coordinates.");
                CheckLinkPosition(inst, sl);
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

    // The link should land where the analysed walls go: compare one wall-line end point.
    void CheckLinkPosition(ImportInstance inst, SurveyLevel sl)
    {
        var p = sl.Plan.WallLines.FirstOrDefault();
        if (p == null) return;
        var dwgPoint = new XYZ(Ft(p.A.X), Ft(p.A.Y), 0);
        var placed = inst.GetTotalTransform().OfPoint(dwgPoint);
        var expected = _model.Frame.ToModel(p.A);
        var error = Math.Sqrt(Math.Pow(UnitConversion.FeetToMm(placed.X) - expected.X, 2) + Math.Pow(UnitConversion.FeetToMm(placed.Y) - expected.Y, 2));
        if (error > 10) Log.Add($"WARNING: {System.IO.Path.GetFileName(sl.Plan.Path)} landed {error:0} mm away from where the walls are created. Check its units.");
    }

    void CreateWalls(Dictionary<SurveyLevel, (Level level, ViewPlan? view)> levels)
    {
        var ordered = levels.OrderBy(kv => kv.Value.level.Elevation).ToList();
        using var t = new Transaction(_doc, "Survey walls");
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new WarningSwallower());
        t.SetFailureHandlingOptions(options);
        t.Start();
        var types = new Dictionary<double, WallType>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (sl, (level, _)) = (ordered[i].Key, ordered[i].Value);
            var above = i + 1 < ordered.Count ? ordered[i + 1].Value.level : null;
            int removed = DeleteGenerated(sl.Code);
            int made = 0;
            foreach (var w in sl.Walls.Walls)
            {
                var c = _model.Frame.ToModel(w.Centerline);
                if (c.Length < 10) continue;
                var line = Line.CreateBound(new XYZ(Ft(c.A.X), Ft(c.A.Y), level.Elevation), new XYZ(Ft(c.B.X), Ft(c.B.Y), level.Elevation));
                if (!types.TryGetValue(w.TypeThickness, out var type)) types[w.TypeThickness] = type = WallTypeFor(w.TypeThickness);
                var wall = Wall.Create(_doc, line, type.Id, level.Id, Ft(sl.WallHeightMm), 0, false, false);
                if (above != null) wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(above.Id);
                wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{Tag}|{sl.Code}|{w.Confidence}");
                made++;
            }
            Log.Add($"{sl.Name}: {made} walls created" + (removed > 0 ? $" (replacing {removed} from an earlier run)." : "."));
        }
        t.Commit();
    }

    int DeleteGenerated(string levelCode)
    {
        var prefix = $"{Tag}|{levelCode}|";
        var ids = new FilteredElementCollector(_doc).OfClass(typeof(Wall)).WhereElementIsNotElementType()
            .Where(w => w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(prefix) == true)
            .Select(w => w.Id).ToList();
        if (ids.Count > 0) _doc.Delete(ids);
        return ids.Count;
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

    /// <summary>Dismisses warnings (e.g. overlapping walls) so a large run is not interrupted; errors still stop it.</summary>
    sealed class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (var f in a.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}
