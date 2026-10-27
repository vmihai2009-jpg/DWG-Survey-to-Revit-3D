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
/// the phase of the plan it was sent from (the newest phase without one).
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
    readonly Dictionary<string, Family?> _families = new();
    readonly Dictionary<ElementId, XYZ> _offsets = new();
    static List<string>? _library;
    static bool _saidNoLibrary;
    readonly Dictionary<string, Family?> _own = new();
    readonly ElementId _option;
    public List<string> Log { get; } = new();
    public Dictionary<string, int> Placed { get; } = new();
    public int Blocks, Walls, Glazed, Doors, Rooms, Removed, Grouped;
    List<FitItem> _items = new();
    Dictionary<string, FitZone> _zones = new();

    readonly Dictionary<string, (Family? Family, string Type, double Turn)> _picked = new(StringComparer.Ordinal);

    public TestFitBuilder(Document doc, Level level, double storeyHeightMm, View? view = null)
    {
        _doc = doc;
        _level = level;
        _heightMm = storeyHeightMm;
        // a design option made active in Revit (Manage > Design Options) takes the layout; each option keeps its own
        _option = DesignOption.GetActiveDesignOptionId(doc);
        _tag = TagPrefix + level.Name + (_option != ElementId.InvalidElementId ? "|OPTION|" + _option.Value : "");
        // the phase the plan shows, so the layout appears in the view it was made from
        _phase = view?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() is { } ph && doc.GetElement(ph) is Phase vp ? vp
            : doc.Phases.Size > 0 ? doc.Phases.get_Item(doc.Phases.Size - 1) : null;
    }

    public string? PhaseName => _phase?.Name;
    public ElementId? PhaseId => _phase?.Id;

    /// <summary>The design option the layout went into ("Furniture Layout : Option 2"), or null for the main model.</summary>
    public string? OptionName => _option != ElementId.InvalidElementId && _doc.GetElement(_option) is DesignOption o
        ? (o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId() is { } set && _doc.GetElement(set) is { } s ? s.Name + " : " : "") + o.Name
        : null;

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
                .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == _tag).ToList();
            // a group goes with its members; then what was not grouped
            var groups = old.OfType<Autodesk.Revit.DB.Group>().Select(g => g.Id)
                .Concat(old.Select(e => e.GroupId).Where(id => id != ElementId.InvalidElementId)).Distinct().ToList();
            if (groups.Count > 0) Removed += _doc.Delete(groups).Count;
            var loose = old.Where(e => e is not Autodesk.Revit.DB.Group && e.IsValidObject && e.GroupId == ElementId.InvalidElementId).Select(e => e.Id).ToList();
            if (loose.Count > 0) Removed += _doc.Delete(loose).Count;
            foreach (var gt in new FilteredElementCollector(_doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                         .Where(t => t.Name.StartsWith("Test-Fit ", StringComparison.Ordinal) && t.Groups.IsEmpty).Select(t => t.Id).ToList())
                try { _doc.Delete(gt); } catch { /* still used somewhere */ }
            _workset = Workset();
        });
        _items = layout.WithMonitors().ToList();
        _zones = layout.Zones.ToDictionary(z => z.Id, z => z, StringComparer.Ordinal);
        ResolveFurniture(layout);
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
        WallType? divider = null;
        var height = Ft(_heightMm);
        Wall? Make(Vec2 a, Vec2 b, bool glass, bool split = false)
        {
            if (a.DistanceTo(b) < 50) return null;
            var type = split ? divider ??= DividerType(solid) : glass && glazed != null ? glazed : solid;
            var wall = Wall.Create(_doc, Line.CreateBound(At(a), At(b)), type.Id, _level.Id, height, 0, false, false);
            Mark(wall);
            if (type.Kind == WallKind.Curtain) Glazed++; else Walls++;
            return wall;
        }
        foreach (var w in layout.Walls) Make(w.A, w.B, w.Glazed, w.Divider);

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

    /// <summary>The wall type for the divider between the halves of a split room: an operable wall, to swap for the product chosen.</summary>
    WallType DividerType(WallType solid)
    {
        const string name = "Test-Fit Operable Divider";
        var found = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(w => w.Name == name);
        if (found != null) return found;
        try { return (WallType)solid.Duplicate(name); } catch { return solid; }
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
    internal static readonly Dictionary<string, (string[] Want, string Skip, string? Else, double HeightMm)> Kinds = new()
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
        ["monitor"] = (new[] { @"^(m_)?(computer[-_ ]?)?monitor", @"computer[-_ ]?(screen|display)", @"monitor" }, @"tv|televis|patient|heart|wall|arm|bracket|stand[-_ ]?alone", null, 1230),
    };
    static readonly long[] FurnitureCategories =
    [
        (long)BuiltInCategory.OST_Furniture, (long)BuiltInCategory.OST_FurnitureSystems, (long)BuiltInCategory.OST_Planting,
        (long)BuiltInCategory.OST_Casework, (long)BuiltInCategory.OST_SpecialityEquipment, (long)BuiltInCategory.OST_GenericModel,
    ];
    internal static bool Sizable(string kind) => kind is "desk" or "recdesk" or "table" or "meettable" or "hightable" or "coffee" or "sofa" or "bench";
    internal static bool Fits(Family f) => FurnitureCategories.Contains(f.FamilyCategory?.Id.Value ?? -1)
        && f.FamilyPlacementType is FamilyPlacementType.OneLevelBased or FamilyPlacementType.WorkPlaneBased;

    /// <summary>
    /// Finds a family for each kind the layout uses before anything is placed: the project's, else Revit's
    /// library, else one the add-in makes at each size. Run outside a transaction (families are loaded and made).
    /// </summary>
    void ResolveFurniture(TestFitLayout layout)
    {
        // the families picked on the page's Assets tab, per space use and kind
        foreach (var (key, (id, type, turn)) in layout.Assets)
        {
            Family? family = null;
            try
            {
                if (id.StartsWith("p:") && long.TryParse(id[2..], out var v)) family = _doc.GetElement(new ElementId(v)) as Family;
                else if (id.StartsWith("f:"))
                    family = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == Path.GetFileNameWithoutExtension(id[2..]))
                             ?? (File.Exists(id[2..]) ? ConceptFamilies.Load(_doc, id[2..]) : null);
            }
            catch (Exception ex) { Log.Add($"The family picked for {key} could not be loaded ({ex.Message})."); }
            if (id != TestFitAssets.Own && (family == null || !Fits(family))) { Log.Add($"The family picked for {key} was not found, so the add-in picked one."); continue; }
            _picked[key] = (family, type, turn);
        }
        foreach (var kind in _items.Where(i => Pick(i) == null).Select(i => i.Kind).Distinct().Where(Kinds.ContainsKey)) FurnitureType(kind);
        foreach (var it in _items.Where(i => Kinds.ContainsKey(i.Kind) && (Pick(i) is { } p ? p.Family == null : _families.GetValueOrDefault(i.Kind) == null)))
        {
            var key = FurnitureFamilies.Name(it.Kind, it.WidthMm, it.DepthMm);
            if (_own.ContainsKey(key)) continue;
            var made = FurnitureFamilies.Make(_doc, it.Kind, it.WidthMm, it.DepthMm, Log);
            _own[key] = made;
            if (made != null) Log.Add($"{it.Kind}: no family in the project or Revit library, so the add-in made '{made.Name}'.");
        }
    }

    /// <summary>
    /// Places the furniture, one Revit group per workstation (desk, chair, monitor) and per space (a meeting room's
    /// table and chairs, a breakout area, reception). Workstations of one desk size share one group type: the
    /// first is built and grouped, the others are copies of that group turned to their place.
    /// </summary>
    void Furniture(TestFitLayout layout)
    {
        var stations = new Dictionary<string, (GroupType Type, XYZ Desk, double Turn, XYZ Origin, List<string> Families)>();
        foreach (var set in _items.Where(i => i.Kind != "screen" && Kinds.ContainsKey(i.Kind)).GroupBy(i => i.Group))
        {
            var pieces = set.ToList();
            var desk = pieces.FirstOrDefault(i => i.Kind == "desk");
            var station = desk != null && set.Key.StartsWith("ws", StringComparison.Ordinal);
            var size = desk == null ? "" : $"{desk.WidthMm:0}x{desk.DepthMm:0}";
            try
            {
                if (station && stations.TryGetValue(size, out var s))
                {
                    var at = At(desk!.Center);
                    var copy = _doc.Create.PlaceGroup(s.Origin + (at - s.Desk), s.Type);
                    var turn = desk.Rotation - s.Turn;
                    if (Math.Abs(turn) > 1e-6) ElementTransformUtils.RotateElement(_doc, copy.Id, Line.CreateBound(at, at + XYZ.BasisZ), turn);
                    Mark(copy);
                    foreach (var f in s.Families) Placed[f] = Placed.GetValueOrDefault(f) + 1;
                    Grouped++;
                    continue;
                }
                var ids = new List<ElementId>();
                var families = new List<string>();
                foreach (var it in pieces)
                {
                    try { if (Piece(it) is { } e) { ids.Add(e.Id); if (e is FamilyInstance fi) families.Add(fi.Symbol.Family.Name); } }
                    catch (Exception ex) { Log.Add($"A {it.Kind} at {it.Center} was not placed: {ex.Message}"); }
                }
                if (ids.Count < 2) continue;
                _doc.Regenerate();
                var group = _doc.Create.NewGroup(ids);
                var name = station ? $"Test-Fit Workstation {size}" : "Test-Fit " + (_zones.TryGetValue(pieces[0].Zone, out var z) ? z.Name : set.Key);
                Name(group.GroupType, name);
                Mark(group);
                Grouped++;
                if (station) stations[size] = (group.GroupType, At(desk!.Center), desk.Rotation, ((LocationPoint)group.Location).Point, families);
            }
            catch (Exception ex) { Log.Add($"The {(station ? "workstation" : "group")} '{set.Key}' was not grouped: {ex.Message}"); }
        }
    }

    /// <summary>A group type name not taken yet (an older layout in another design option keeps its own).</summary>
    void Name(GroupType type, string name)
    {
        var taken = new FilteredElementCollector(_doc).OfClass(typeof(GroupType)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = name;
        for (var i = 2; taken.Contains(n); i++) n = $"{name} ({i})";
        try { type.Name = n; } catch { /* Revit keeps its own name */ }
    }

    /// <summary>The family picked on the Assets tab for this piece's space use and kind, if any.</summary>
    (Family? Family, string Type, double Turn)? Pick(FitItem it) =>
        _picked.TryGetValue((_zones.TryGetValue(it.Zone, out var z) ? z.Type : "") + ":" + it.Kind, out var p) ? p : null;

    /// <summary>One piece of furniture: its family (sized), the add-in's own family, or a plain block.</summary>
    Element? Piece(FitItem it)
    {
        var spec = Kinds[it.Kind];
        var pick = Pick(it);
        if (pick is { Family: { } picked })
        {
            var symbol = picked.GetFamilySymbolIds().Select(id => (FamilySymbol)_doc.GetElement(id)).FirstOrDefault(s => s.Name == pick.Value.Type) ?? Activate(picked);
            if (!symbol.IsActive) { symbol.Activate(); _doc.Regenerate(); }
            // a family turned a quarter on the page has its own width along the piece's depth
            var turn = pick.Value.Turn * Math.PI / 180;
            var quarter = Math.Abs(Math.Round(pick.Value.Turn / 90)) % 2 == 1;
            var inst = Place(it, Sizable(it.Kind) ? Sized(symbol, "Test-Fit " + it.Kind, quarter ? it.DepthMm : it.WidthMm, quarter ? it.WidthMm : it.DepthMm, false) : symbol, turn);
            if (it.Kind == "monitor") Raise(inst, 740);
            return inst;
        }
        if (pick == null && _families.GetValueOrDefault(it.Kind) is { } family)
        {
            var symbol = Activate(family);
            var inst = Place(it, Sizable(it.Kind) ? Sized(symbol, "Test-Fit " + it.Kind, it.WidthMm, it.DepthMm, false) : symbol);
            if (it.Kind == "monitor") Raise(inst, 740); // a library monitor stands on the level: lift it onto the desk
            return inst;
        }
        if (_own.GetValueOrDefault(FurnitureFamilies.Name(it.Kind, it.WidthMm, it.DepthMm)) is { } own) return Place(it, Activate(own));
        if (it.Kind == "monitor") return null; // no plain block for a monitor
        return Block(it, spec.HeightMm);
    }

    static void Raise(FamilyInstance inst, double mm)
    {
        foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
            if (inst.get_Parameter(bip) is { IsReadOnly: false } p) { p.Set(Ft(mm)); return; }
    }

    FamilyInstance Place(FitItem it, FamilySymbol symbol, double turn = 0)
    {
        var target = At(it.Center);
        var inst = symbol.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased
            ? _doc.Create.NewFamilyInstance(_level.GetPlaneReference(), target, XYZ.BasisX, symbol)
            : _doc.Create.NewFamilyInstance(target, symbol, _level, StructuralType.NonStructural);
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
        if (Math.Abs(it.Rotation + turn) > 1e-6)
            ElementTransformUtils.RotateElement(_doc, inst.Id, Line.CreateBound(target, target + XYZ.BasisZ), it.Rotation + turn);
        Placed[symbol.Family.Name] = Placed.GetValueOrDefault(symbol.Family.Name) + 1;
        return inst;
    }

    /// <summary>No family of this kind anywhere and none could be made: a plain block of the piece's size, on the Furniture category.</summary>
    DirectShape Block(FitItem it, double heightMm)
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
        return ds;
    }

    /// <summary>The project's or library's family for a kind (or for the kind it falls back to); null if neither has one.</summary>
    Family? FurnitureType(string kind)
    {
        if (_families.TryGetValue(kind, out var cached)) return cached;
        var (want, skip, fallback, _) = Kinds[kind];
        var inProject = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().Where(Fits)
            .Where(f => !f.Name.StartsWith(FurnitureFamilies.Prefix, StringComparison.Ordinal)).ToList();
        Family? family = null;
        foreach (var pattern in want)
        {
            family = inProject.Where(f => Regex.IsMatch(f.Name, pattern, RegexOptions.IgnoreCase) && !Regex.IsMatch(f.Name, skip, RegexOptions.IgnoreCase))
                .OrderBy(f => f.Name.Length).FirstOrDefault();
            if (family != null) break;
        }
        if (family == null)
            foreach (var pattern in want)
                if ((family = LoadFromLibrary(pattern, skip, Fits)) != null) break;
        if (family != null) Log.Add($"{kind}: family '{family.Name}'.");
        else if (fallback != null && FurnitureType(fallback) is { } other && !Sizable(fallback) == !Sizable(kind)) family = other;
        return _families[kind] = family;
    }

    // ---------- rooms ----------

    void MakeRooms(TestFitLayout layout)
    {
        foreach (var z in layout.Zones.Where(z => z.Enclosed))
        {
            try
            {
                var c = At(z.RoomPoint);
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

    /// <summary>
    /// The first family in Revit's library (the library paths in Revit's options, then Autodesk's installed
    /// content) whose file name matches, that loads and fits; null if none. One that loads but does not fit
    /// (wrong category or hosting) is taken out of the project again.
    /// </summary>
    Family? LoadFromLibrary(string pattern, string skip, Func<Family, bool> fits)
    {
        _library ??= Library(_doc.Application);
        if (_library.Count == 0 && !_saidNoLibrary)
        {
            _saidNoLibrary = true;
            Log.Add("Revit's family library is not on this PC (no .rfa files in its library folders), so families come from the project or are made by the add-in.");
        }
        var candidates = Matching(_library, pattern, skip).Take(8);
        foreach (var file in candidates)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var before = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name);
            try
            {
                var loaded = before ?? ConceptFamilies.Load(_doc, file);
                if (loaded == null) continue;
                if (fits(loaded))
                {
                    if (before == null) Log.Add($"Loaded from the Revit library: {Path.GetFileName(file)}");
                    return loaded;
                }
                if (before == null) Remove(loaded);
            }
            catch { /* a file that does not load is skipped */ }
        }
        return null;
    }

    /// <summary>Every .rfa file in Revit's library folders (read once per Revit session).</summary>
    internal static List<string> Library(Autodesk.Revit.ApplicationServices.Application app) =>
        _library ??= ConceptFamilies.LibraryRoots(app).SelectMany(root =>
        {
            try { return Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories).ToList(); }
            catch { return new List<string>(); }
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Library files whose name matches, furniture folders and short names first.</summary>
    internal static IEnumerable<string> Matching(IEnumerable<string> files, string pattern, string skip) => files.Where(f =>
        {
            var n = Path.GetFileNameWithoutExtension(f);
            return Regex.IsMatch(n, pattern, RegexOptions.IgnoreCase) && !Regex.IsMatch(n, skip, RegexOptions.IgnoreCase);
        })
        .OrderBy(f => f.Contains("Furniture", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(f => Path.GetFileNameWithoutExtension(f).Length);

    void Remove(Family family)
    {
        if (_doc.IsModifiable) { try { _doc.Delete(family.Id); } catch { } return; }
        using var t = new Transaction(_doc, "Test-fit: unload " + family.Name);
        try { t.Start(); _doc.Delete(family.Id); t.Commit(); }
        catch { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
    }
}
