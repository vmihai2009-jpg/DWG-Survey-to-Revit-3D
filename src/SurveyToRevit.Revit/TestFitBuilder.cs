using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Revit;

/// <summary>
/// Puts a test-fit layout into the model on its level: furniture as Autodesk families (from the project, else
/// Revit's library; a plain furniture block if neither has one), solid partitions as walls, glazed ones as
/// curtain walls, doors in them, and meeting and focus rooms as rooms. Everything is tagged so the next Send to
/// Revit on the same level replaces it, goes on a "Test-Fit" workset when the project is workshared, and on
/// the newest phase (it is proposed work, not existing).
/// </summary>
internal sealed class TestFitBuilder
{
    const string TagPrefix = ModelBuilder.Tag + "|TESTFIT|";
    const string WorksetName = "Test-Fit";
    readonly Document _doc;
    readonly Level _level;
    readonly double _heightMm;
    readonly string _tag;
    readonly Phase? _phase;
    WorksetId? _workset;
    readonly Dictionary<string, FamilySymbol?> _families = new();
    readonly Dictionary<ElementId, XYZ> _offsets = new();
    static List<string>? _library;
    public List<string> Log { get; } = new();
    public Dictionary<string, int> Placed { get; } = new();
    public int Blocks, Walls, Glazed, Doors, Rooms, Removed;

    public TestFitBuilder(Document doc, Level level, double storeyHeightMm)
    {
        _doc = doc;
        _level = level;
        _heightMm = storeyHeightMm;
        _tag = TagPrefix + level.Name;
        _phase = doc.Phases.Size > 0 ? doc.Phases.get_Item(doc.Phases.Size - 1) : null;
    }

    public string? PhaseName => _phase?.Name;

    static double Ft(double mm) => UnitConversion.MmToFeet(mm);
    static double Mm(double ft) => UnitConversion.FeetToMm(ft);
    XYZ At(Vec2 p) => new(Ft(p.X), Ft(p.Y), _level.ProjectElevation);

    public void Build(TestFitLayout layout)
    {
        using var group = new TransactionGroup(_doc, "Office Test-Fit");
        group.Start();
        Step("Test-fit: clear the last one", () =>
        {
            var old = new FilteredElementCollector(_doc).WhereElementIsNotElementType()
                .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == _tag).Select(e => e.Id).ToList();
            if (old.Count > 0) Removed = _doc.Delete(old).Count;
            _workset = Workset();
        });
        Step("Test-fit: partitions and doors", () => Partitions(layout));
        Step("Test-fit: furniture", () => Furniture(layout));
        Step("Test-fit: rooms", () => MakeRooms(layout));
        group.Assimilate();
    }

    void Step(string name, Action work)
    {
        using var t = new Transaction(_doc, name);
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new ModelBuilder.WarningSwallower());
        t.SetFailureHandlingOptions(options);
        t.Start();
        try { work(); t.Commit(); }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            Log.Add($"{name} failed: {ex.Message}");
        }
    }

    void Mark(Element e)
    {
        e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(_tag);
        if (_phase != null)
        {
            var p = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (p is { IsReadOnly: false }) p.Set(_phase.Id);
        }
        if (_workset != null) e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.Set(_workset.IntegerValue);
    }

    WorksetId? Workset()
    {
        if (!_doc.IsWorkshared) return null;
        var existing = new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset)
            .FirstOrDefault(w => w.Name.Equals(WorksetName, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.Id;
        try { return Autodesk.Revit.DB.Workset.Create(_doc, WorksetName).Id; }
        catch (Exception ex) { Log.Add($"Could not create the '{WorksetName}' workset ({ex.Message}); the test-fit stays on the active workset."); return null; }
    }

    // ---------- partitions and doors ----------

    void Partitions(TestFitLayout layout)
    {
        var solid = ModelBuilder.WallTypeFor(_doc, 100, Log);
        var glazed = CurtainType();
        var height = Ft(_heightMm);
        Wall? Make(Vec2 a, Vec2 b, bool glass)
        {
            if (a.DistanceTo(b) < 50) return null;
            var type = glass && glazed != null ? glazed : solid;
            var wall = Wall.Create(_doc, Line.CreateBound(At(a), At(b)), type.Id, _level.Id, height, 0, false, false);
            Mark(wall);
            if (type.Kind == WallKind.Curtain) Glazed++; else Walls++;
            return wall;
        }
        foreach (var w in layout.Walls) Make(w.A, w.B, w.Glazed);

        FamilySymbol? door = null, panelDoor = null;
        foreach (var d in layout.Doors)
        {
            var host = Make(d.Hinge, d.Hinge + d.Along * d.WidthMm, d.Glazed);
            if (host == null) continue;
            if (host.WallType.Kind == WallKind.Curtain)
            {
                panelDoor ??= CurtainDoor();
                if (panelDoor == null) { _doc.Delete(host.Id); Glazed--; continue; } // leave the opening clear
                _doc.Regenerate();
                var panel = host.CurtainGrid?.GetPanelIds().Select(_doc.GetElement).FirstOrDefault();
                if (panel == null) continue;
                panel.Pinned = false;
                panel.ChangeTypeId(panelDoor.Id);
                Doors++;
                continue;
            }
            door ??= DoorType(d.WidthMm);
            if (door == null) continue;
            var symbol = Sized(door, "Test-Fit Door", d.WidthMm, 2100, true);
            var inst = _doc.Create.NewFamilyInstance(At(d.Center), symbol, host, _level, StructuralType.NonStructural);
            Mark(inst);
            _doc.Regenerate();
            // open into the room (the family's facing side is taken as the side it opens to)
            var into = new XYZ(d.Into.X, d.Into.Y, 0);
            if (inst.CanFlipFacing && inst.FacingOrientation.DotProduct(into) < 0) inst.flipFacing();
            Doors++;
        }
    }

    WallType? CurtainType()
    {
        const string name = "Test-Fit Glazed Partition";
        var all = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        var found = all.FirstOrDefault(w => w.Name == name);
        if (found != null) return found;
        var curtain = all.Where(w => w.Kind == WallKind.Curtain)
            .OrderBy(w => w.Name.Contains("Storefront", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        if (curtain == null) { Log.Add("No curtain wall type in the project: glazed partitions are built as walls."); return null; }
        return (WallType)curtain.Duplicate(name);
    }

    FamilySymbol? DoorType(double widthMm)
    {
        var family = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => f.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_Doors && f.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted)
            .Where(f => !Regex.IsMatch(f.Name, "double|curtain|garage|revolving|sliding", RegexOptions.IgnoreCase))
            .OrderBy(f => f.Name.Contains("Single", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        family ??= LoadFromLibrary(@"door.*single|single.*door", @"double|curtain|garage|revolving|sliding",
            f => f.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_Doors && f.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted);
        if (family == null) { Log.Add("No single door family in the project or Revit library: solid partitions have no doors."); return null; }
        Log.Add($"Doors use the family '{family.Name}'.");
        return Activate(family);
    }

    FamilySymbol? CurtainDoor()
    {
        bool IsPanel(Family f) => f.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_Doors && f.FamilyPlacementType != FamilyPlacementType.OneLevelBasedHosted; // curtain panel doors are not wall-hosted
        var family = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().Where(IsPanel)
            .OrderBy(f => f.Name.Contains("Single", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        family ??= LoadFromLibrary(@"curtain.*door|door.*curtain|storefront.*door", @"double", IsPanel);
        if (family == null) { Log.Add("No curtain wall door family in the project or Revit library: glazed partitions have open doorways."); return null; }
        Log.Add($"Glazed partition doors use the family '{family.Name}'.");
        return Activate(family);
    }

    // ---------- furniture ----------

    // What to look for per kind, best first; "skip" patterns rule a family out. Names are matched against the
    // family (or .rfa file) name, so the UK, metric and imperial Autodesk libraries all work.
    static readonly Dictionary<string, (string[] Want, string Skip, string? Else, double HeightMm)> Kinds = new()
    {
        ["desk"] = (new[] { @"^(m_)?desk([-_ ]?(\d|rect|office|work|single))?$", @"desk" }, @"reception|corner|l[-_ ]?shape|curved|chair|lamp", null, 740),
        ["recdesk"] = (new[] { @"reception" }, @"chair|sofa|seat", "desk", 1100),
        ["chair"] = (new[] { @"chair[-_ ]?(task|office|desk|executive)", @"(task|office)[-_ ]?chair", @"chair" }, @"arm|lounge|stack|wheel|sofa|club|easy|high|stool|baby|child", null, 900),
        ["arm"] = (new[] { @"arm[-_ ]?chair|chair[-_ ]?(lounge|club|arm|easy)|lounge|easy" }, @"sofa|stool", "chair", 800),
        ["sofa"] = (new[] { @"sofa|couch|settee" }, @"bed", "arm", 800),
        ["bench"] = (new[] { @"bench|banquette|booth" }, @"work|lab|tool", "sofa", 900),
        ["stool"] = (new[] { @"stool" }, @"tool", "chair", 750),
        ["coffee"] = (new[] { @"coffee" }, @"machine|maker", "table", 400),
        ["table"] = (new[] { @"^(m_)?table([-_ ]?(rect|square|\d))?", @"table" }, @"coffee|night|bedside|end|side|dining[-_ ]?round|picnic|ping|pool|change|chang|operat|lamp", null, 740),
        ["meettable"] = (new[] { @"(conference|meeting|board)", @"table[-_ ]?rect" }, @"chair|room", "table", 740),
        ["hightable"] = (new[] { @"(bar|high|poseur)[-_ ]?table|table[-_ ]?(bar|high)" }, @"chair|stool", "table", 1050),
        ["plant"] = (new[] { @"plant|planter|ficus|palm|shrub" }, @"power|treatment|room", null, 1200),
    };
    static readonly long[] FurnitureCategories =
    [
        (long)BuiltInCategory.OST_Furniture, (long)BuiltInCategory.OST_FurnitureSystems, (long)BuiltInCategory.OST_Planting,
        (long)BuiltInCategory.OST_Casework, (long)BuiltInCategory.OST_SpecialityEquipment, (long)BuiltInCategory.OST_GenericModel,
    ];
    static bool Sizable(string kind) => kind is "desk" or "recdesk" or "table" or "meettable" or "hightable" or "coffee" or "sofa" or "bench";

    void Furniture(TestFitLayout layout)
    {
        foreach (var it in layout.Items)
        {
            if (it.Kind == "screen" || !Kinds.TryGetValue(it.Kind, out var spec)) continue;
            var symbol = FurnitureType(it.Kind);
            try
            {
                if (symbol == null) Block(it, spec.HeightMm);
                else Place(it, Sizable(it.Kind) ? Sized(symbol, "Test-Fit " + it.Kind, it.WidthMm, it.DepthMm, false) : symbol);
            }
            catch (Exception ex) { Log.Add($"A {it.Kind} at {it.Center} was not placed: {ex.Message}"); }
        }
    }

    void Place(FitItem it, FamilySymbol symbol)
    {
        var target = At(it.Center);
        var inst = _doc.Create.NewFamilyInstance(target, symbol, _level, StructuralType.NonStructural);
        Mark(inst);
        // Families put their insertion point anywhere (a corner, the back edge...): centre each type's plan box
        // on the place the page drew it, measured once per type.
        if (!_offsets.TryGetValue(symbol.Id, out var offset))
        {
            _doc.Regenerate();
            var box = inst.get_BoundingBox(null);
            offset = box == null ? XYZ.Zero : new XYZ((box.Min.X + box.Max.X) / 2 - target.X, (box.Min.Y + box.Max.Y) / 2 - target.Y, 0);
            _offsets[symbol.Id] = offset;
        }
        if (!offset.IsZeroLength()) ElementTransformUtils.MoveElement(_doc, inst.Id, -offset);
        if (Math.Abs(it.Rotation) > 1e-6)
            ElementTransformUtils.RotateElement(_doc, inst.Id, Line.CreateBound(target, target + XYZ.BasisZ), it.Rotation);
        Placed[symbol.Family.Name] = Placed.GetValueOrDefault(symbol.Family.Name) + 1;
    }

    /// <summary>No family of this kind anywhere: a plain block of the piece's size, on the Furniture category.</summary>
    void Block(FitItem it, double heightMm)
    {
        double hw = it.WidthMm / 2, hd = it.DepthMm / 2, c = Math.Cos(it.Rotation), s = Math.Sin(it.Rotation);
        XYZ Corner(double x, double y) => At(it.Center + new Vec2(x * c - y * s, x * s + y * c));
        var loop = new CurveLoop();
        var pts = new[] { Corner(-hw, -hd), Corner(hw, -hd), Corner(hw, hd), Corner(-hw, hd) };
        for (var i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, Ft(heightMm));
        var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_Furniture));
        ds.SetShape(new GeometryObject[] { solid });
        ds.Name = "Test-Fit " + it.Kind;
        Mark(ds);
        Blocks++;
    }

    FamilySymbol? FurnitureType(string kind)
    {
        if (_families.TryGetValue(kind, out var cached)) return cached;
        var (want, skip, fallback, _) = Kinds[kind];
        bool Fits(Family f) => FurnitureCategories.Contains(f.FamilyCategory?.Id.Value ?? -1) && f.FamilyPlacementType == FamilyPlacementType.OneLevelBased;
        var inProject = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().Where(Fits).ToList();
        Family? family = null;
        foreach (var pattern in want)
        {
            family = inProject.Where(f => Regex.IsMatch(f.Name, pattern, RegexOptions.IgnoreCase) && !Regex.IsMatch(f.Name, skip, RegexOptions.IgnoreCase))
                .OrderBy(f => f.Name.Length).FirstOrDefault()
                ?? LoadFromLibrary(pattern, skip, Fits);
            if (family != null) break;
        }
        FamilySymbol? symbol = null;
        if (family != null)
        {
            symbol = Activate(family);
            Log.Add($"{kind}: family '{family.Name}'.");
        }
        else if (fallback != null) symbol = FurnitureType(fallback);
        else Log.Add($"{kind}: no family in the project or Revit library, placed as plain furniture blocks. Load one and send again.");
        return _families[kind] = symbol;
    }

    // ---------- rooms ----------

    void MakeRooms(TestFitLayout layout)
    {
        foreach (var z in layout.Zones.Where(z => z.Enclosed))
        {
            try
            {
                var c = At(z.Center);
                var room = _doc.Create.NewRoom(_level, new UV(c.X, c.Y));
                room.Name = z.Name;
                Mark(room);
                _doc.Regenerate();
                if (room.Area <= 0) { _doc.Delete(room.Id); continue; } // not closed by walls: no room
                Rooms++;
            }
            catch (Exception ex) { Log.Add($"Room '{z.Name}' not made: {ex.Message}"); }
        }
    }

    // ---------- families ----------

    FamilySymbol Activate(Family family)
    {
        var symbol = family.GetFamilySymbolIds().Select(id => (FamilySymbol)_doc.GetElement(id)).OrderBy(s => s.Name).First();
        if (!symbol.IsActive) { symbol.Activate(); _doc.Regenerate(); }
        return symbol;
    }

    /// <summary>A type of the family at this size, when it has width/depth parameters; else the family's own type.</summary>
    FamilySymbol Sized(FamilySymbol from, string prefix, double widthMm, double otherMm, bool isDoor)
    {
        var name = $"{prefix} {widthMm:0}x{otherMm:0}";
        var existing = from.Family.GetFamilySymbolIds().Select(id => (FamilySymbol)_doc.GetElement(id)).FirstOrDefault(s => s.Name == name);
        if (existing != null) return existing;
        Parameter? Find(FamilySymbol s, BuiltInParameter bip, params string[] names) =>
            new[] { bip == BuiltInParameter.INVALID ? null : s.get_Parameter(bip) }.Concat(names.Select(s.LookupParameter))
                .FirstOrDefault(p => p is { IsReadOnly: false, StorageType: StorageType.Double });
        var w = Find(from, BuiltInParameter.FAMILY_WIDTH_PARAM, "Width", "Length");
        var o = isDoor ? Find(from, BuiltInParameter.FAMILY_HEIGHT_PARAM, "Height") : Find(from, BuiltInParameter.INVALID, "Depth");
        if (w == null && o == null) return from;
        var symbol = (FamilySymbol)from.Duplicate(name);
        try
        {
            Find(symbol, BuiltInParameter.FAMILY_WIDTH_PARAM, "Width", "Length")?.Set(Ft(widthMm));
            (isDoor ? Find(symbol, BuiltInParameter.FAMILY_HEIGHT_PARAM, "Height") : Find(symbol, BuiltInParameter.INVALID, "Depth"))?.Set(Ft(otherMm));
        }
        catch { /* a size the family refuses: it keeps its own */ }
        if (!symbol.IsActive) symbol.Activate();
        _doc.Regenerate();
        return symbol;
    }

    /// <summary>The first family in Revit's library whose file name matches, that loads and fits; null if none.</summary>
    Family? LoadFromLibrary(string pattern, string skip, Func<Family, bool> fits)
    {
        _library ??= _doc.Application.GetLibraryPaths().Values.Where(Directory.Exists).SelectMany(root =>
        {
            try { return Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories).ToList(); }
            catch { return new List<string>(); }
        }).ToList();
        var candidates = _library.Where(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f);
                return Regex.IsMatch(n, pattern, RegexOptions.IgnoreCase) && !Regex.IsMatch(n, skip, RegexOptions.IgnoreCase);
            })
            .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length).Take(6);
        foreach (var file in candidates)
        {
            try
            {
                if (!_doc.LoadFamily(file, out Family loaded))
                {
                    // already in the project under that name
                    var name = Path.GetFileNameWithoutExtension(file);
                    loaded = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name)!;
                }
                if (loaded != null && fits(loaded))
                {
                    Log.Add($"Loaded from the Revit library: {Path.GetFileName(file)}");
                    return loaded;
                }
            }
            catch { /* a file that does not load is skipped */ }
        }
        return null;
    }
}
