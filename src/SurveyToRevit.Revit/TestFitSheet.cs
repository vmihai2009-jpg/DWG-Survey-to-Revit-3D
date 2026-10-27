using Autodesk.Revit.DB;
using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Revit;

/// <summary>
/// The study sheet for a test-fit: a zoning plan of the floor with each space as a filled region in its use's
/// colour and a label (name, use, m², seats), a 3D view of the floor cut at the storey above, and the
/// dashboard's figures (NIA, workstations against target, density, the space mix as a bar and a table, seats,
/// partitions, the space schedule and the checks) in a drafting view beside them. Sending again for the same
/// floor and design option replaces the sheet and its views.
/// </summary>
internal static class TestFitSheet
{
    // the page's colours per use: a pale fill for the plan, the stronger one for the bar
    static readonly Dictionary<string, (string Label, Color Fill, Color Strong, int Priority)> Uses = new()
    {
        ["work"] = ("Workspace", new Color(0xe3, 0xec, 0xf5), new Color(0x5d, 0x84, 0xa8), 2),
        ["soft"] = ("Soft seating", new Color(0xe2, 0xf0, 0xe5), new Color(0x4f, 0x8f, 0x62), 3),
        ["hotdesk"] = ("Hot desking", new Color(0xe4, 0xea, 0xf7), new Color(0x4a, 0x68, 0xb8), 2),
        ["breakout"] = ("Breakout", new Color(0xdf, 0xf1, 0xf0), new Color(0x2f, 0x8f, 0x8a), 3),
        ["lounge"] = ("Lounge", new Color(0xf3, 0xe2, 0xec), new Color(0xa5, 0x50, 0x7f), 3),
        ["tea"] = ("Tea point", new Color(0xf9, 0xe6, 0xd3), new Color(0xc0, 0x70, 0x2e), 4),
        ["reception"] = ("Reception", new Color(0xf6, 0xed, 0xd8), new Color(0xa5, 0x81, 0x2c), 4),
        ["meeting"] = ("Meeting room", new Color(0xeb, 0xe5, 0xf5), new Color(0x7b, 0x64, 0xad), 5),
        ["focus"] = ("Focus room", new Color(0xf4, 0xe4, 0xe2), new Color(0xa4, 0x5f, 0x58), 5),
        ["core"] = ("Core / not NIA", new Color(0xe3, 0xe6, 0xe7), new Color(0x6c, 0x77, 0x7c), 6),
        ["circ"] = ("Circulation & unallocated", new Color(0xc5, 0xcf, 0xd3), new Color(0xc5, 0xcf, 0xd3), 0),
    };
    static readonly BuiltInCategory[] Furniture =
    [
        BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_Planting,
        BuiltInCategory.OST_Casework, BuiltInCategory.OST_SpecialityEquipment,
    ];

    static double Ft(double mm) => UnitConversion.MmToFeet(mm);

    /// <summary>Makes the sheet; returns its number and name, or null (with why in the log).</summary>
    public static string? Make(Document doc, Level level, TestFitLayout layout, TestFitSite site, string? option, ElementId? phase, List<string> log)
    {
        var name = $"Test-Fit - {level.Name} - {option ?? "Main model"}";
        using var t = new Transaction(doc, "Test-fit: study sheet");
        var fh = t.GetFailureHandlingOptions();
        fh.SetFailuresPreprocessor(new ModelBuilder.WarningSwallower());
        t.SetFailureHandlingOptions(fh);
        try
        {
            t.Start();
            // the last sheet and views for this floor and option go first
            var old = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && (v.Name == name || v.Name == name + " - 3D" || v.Name == name + " - Summary" || v is ViewSheet s && s.Name == name))
                .Select(v => v.Id).ToList();
            if (old.Count > 0) doc.Delete(old);

            var plan = Plan(doc, level, layout, site, name, phase, log);
            var axo = Axo(doc, level, site, name, phase);
            var summary = Summary(doc, layout, level, option, name);

            var tb = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .OrderBy(s => s.Name.Contains("A1", StringComparison.OrdinalIgnoreCase) || s.Family.Name.Contains("A1", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (tb != null && !tb.IsActive) tb.Activate();
            var sheet = ViewSheet.Create(doc, tb?.Id ?? ElementId.InvalidElementId);
            sheet.Name = name;
            var taken = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < 1000; i++)
            {
                var number = $"TF-{i:000}";
                if (taken.Contains(number)) continue;
                sheet.SheetNumber = number;
                break;
            }
            doc.Regenerate();

            // the drawing area: the title block's, less a margin and its title strip; A1 without a title block
            double x0 = 0, y0 = 0, x1 = Ft(841), y1 = Ft(594);
            var block = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
            if (block?.get_BoundingBox(sheet) is { } bb) { x0 = bb.Min.X; y0 = bb.Min.Y; x1 = bb.Max.X; y1 = bb.Max.Y; }
            x0 += Ft(25); y0 += Ft(25); y1 -= Ft(20);
            x1 -= (x1 - x0) > Ft(700) ? Ft(200) : Ft(20); // leave the usual right-hand title strip
            var split = x0 + (x1 - x0) * 0.6;

            Place(doc, sheet, plan, x0, y0, split - Ft(10), y1);
            var mid = y0 + (y1 - y0) * 0.55;
            Place(doc, sheet, axo, split + Ft(10), mid, x1, y1);
            Place(doc, sheet, summary, split + Ft(10), y0, x1, mid - Ft(5));
            t.Commit();
            return $"{sheet.SheetNumber} {sheet.Name}";
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            log.Add("The study sheet could not be made: " + ex.Message);
            return null;
        }
    }

    /// <summary>A viewport for the view in the box (sheet feet), centred, scaled down to fit when it is a plan.</summary>
    static void Place(Document doc, ViewSheet sheet, View view, double x0, double y0, double x1, double y1)
    {
        var vp = Viewport.Create(doc, sheet.Id, view.Id, new XYZ((x0 + x1) / 2, (y0 + y1) / 2, 0));
        doc.Regenerate();
        if (view is ViewDrafting)
        {
            // the summary reads from the top left of its box
            var o = vp.GetBoxOutline();
            vp.SetBoxCenter(new XYZ(x0 + (o.MaximumPoint.X - o.MinimumPoint.X) / 2, y1 - (o.MaximumPoint.Y - o.MinimumPoint.Y) / 2, 0));
        }
    }

    // ---------- the zoning plan ----------

    static ViewPlan Plan(Document doc, Level level, TestFitLayout layout, TestFitSite site, string name, ElementId? phase, List<string> log)
    {
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.FloorPlan);
        var plan = ViewPlan.Create(doc, vft.Id, level.Id);
        plan.Name = name;
        if (phase != null) plan.get_Parameter(BuiltInParameter.VIEW_PHASE)?.Set(phase);
        foreach (var c in Furniture)
            try { plan.SetCategoryHidden(new ElementId(c), true); } catch { /* not hideable here */ }

        // crop to the floor, at the largest usual scale that fits the plan's part of an A1 sheet
        var pts = site.Faces.SelectMany(f => f).SelectMany(l => l).ToList();
        double fx0 = pts.Min(p => p.X), fx1 = pts.Max(p => p.X), fy0 = pts.Min(p => p.Y), fy1 = pts.Max(p => p.Y);
        var scale = new[] { 50, 100, 200, 250, 500, 1000 }.FirstOrDefault(s => (fx1 - fx0 + 4000) / s <= 440 && (fy1 - fy0 + 4000) / s <= 520);
        plan.Scale = scale == 0 ? 1000 : scale;
        var box = plan.CropBox;
        box.Min = new XYZ(Ft(fx0 - 2000), Ft(fy0 - 2000), box.Min.Z);
        box.Max = new XYZ(Ft(fx1 + 2000), Ft(fy1 + 2000), box.Max.Z);
        plan.CropBox = box;
        plan.CropBoxActive = true;
        plan.CropBoxVisible = false;

        var types = new Dictionary<string, ElementId>();
        var floor = FloorSolid(site, level);
        var zones = layout.Zones.Select((z, i) => (z, i, solid: Prism(Outline(z), level))).ToList();
        var text = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        foreach (var (z, i, solid) in zones)
        {
            if (solid == null) continue;
            var pri = Uses.GetValueOrDefault(z.Type).Priority;
            Solid? area = solid;
            try
            {
                if (floor != null) area = BooleanOperationsUtils.ExecuteBooleanOperation(area, floor, BooleanOperationsType.Intersect);
                // where spaces overlap, the higher-priority one (a room over open plan) has the floor, as on the page
                foreach (var (o, j, os) in zones)
                    if (o != z && os != null && area != null && area.Volume > 1e-9 && (Uses.GetValueOrDefault(o.Type).Priority > pri || Uses.GetValueOrDefault(o.Type).Priority == pri && j > i))
                        area = BooleanOperationsUtils.ExecuteBooleanOperation(area, os, BooleanOperationsType.Difference);
            }
            catch { area = solid; }
            if (area == null || area.Volume < 1e-9) continue;
            var type = types.TryGetValue(z.Type, out var tid) ? tid : types[z.Type] = RegionType(doc, z.Type);
            foreach (var face in area.Faces.OfType<PlanarFace>().Where(f => f.FaceNormal.Z < -0.99))
                try { FilledRegion.Create(doc, type, plan.Id, face.GetEdgesAsCurveLoops()); }
                catch (Exception ex) { log.Add($"The filled region for '{z.Name}' was not drawn: {ex.Message}"); }

            var use = z.NotNia && z.Type != "core" ? Uses.GetValueOrDefault(z.Type).Label + " (not NIA)" : Uses.GetValueOrDefault(z.Type).Label ?? z.Type;
            var seats = z.Type is "work" or "hotdesk" ? layout.Items.Count(it => it.Zone == z.Id && it.Kind == "desk") + (z.Type == "work" ? " desks" : " hot desks") : z.Seats > 0 ? z.Seats + " seats" : null;
            var label = $"{z.Name}\n{use} · {z.AreaM2:0} m²" + (seats != null ? "\n" + seats : "");
            var p = z.RoomPoint;
            var opts = new TextNoteOptions(text) { HorizontalAlignment = HorizontalTextAlignment.Center };
            try { TextNote.Create(doc, plan.Id, new XYZ(Ft(p.X), Ft(p.Y), level.ProjectElevation), label, opts); } catch { }
        }
        return plan;
    }

    static IReadOnlyList<Vec2> Outline(FitZone z) => z.Outline ?? new[] { z.Min, new Vec2(z.Max.X, z.Min.Y), z.Max, new Vec2(z.Min.X, z.Max.Y) };

    static CurveLoop? Loop(IReadOnlyList<Vec2> pts, double z, bool ccw)
    {
        var l = pts.Where((p, i) => i == 0 || p.DistanceTo(pts[i - 1]) > 5).ToList();
        if (l.Count > 2 && l[0].DistanceTo(l[^1]) <= 5) l.RemoveAt(l.Count - 1);
        if (l.Count < 3) return null;
        var area = 0.0;
        for (var i = 0; i < l.Count; i++) area += l[i].X * l[(i + 1) % l.Count].Y - l[(i + 1) % l.Count].X * l[i].Y;
        if (area > 0 != ccw) l.Reverse();
        var loop = new CurveLoop();
        for (var i = 0; i < l.Count; i++)
            loop.Append(Line.CreateBound(new XYZ(Ft(l[i].X), Ft(l[i].Y), z), new XYZ(Ft(l[(i + 1) % l.Count].X), Ft(l[(i + 1) % l.Count].Y), z)));
        return loop;
    }

    static Solid? Prism(IReadOnlyList<Vec2> pts, Level level)
    {
        try { return Loop(pts, level.ProjectElevation, true) is { } l ? GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { l }, XYZ.BasisZ, 1) : null; }
        catch { return null; }
    }

    /// <summary>The floor (its slabs with their holes) as one thin solid, to keep the regions on it.</summary>
    static Solid? FloorSolid(TestFitSite site, Level level)
    {
        Solid? all = null;
        foreach (var face in site.Faces)
        {
            try
            {
                var loops = face.Select((l, i) => Loop(l, level.ProjectElevation - 0.5, i == 0)).Where(l => l != null).Cast<CurveLoop>().ToList();
                if (loops.Count == 0) continue;
                var s = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, 2);
                all = all == null ? s : BooleanOperationsUtils.ExecuteBooleanOperation(all, s, BooleanOperationsType.Union);
            }
            catch { /* a slab Revit cannot extrude: the regions are not clipped to it */ }
        }
        return all;
    }

    /// <summary>"Test-Fit Workspace" etc: a solid fill in the use's colour, made once and kept in step with the page.</summary>
    static ElementId RegionType(Document doc, string use, bool strong = false)
    {
        var (label, fill, bar, _) = Uses.GetValueOrDefault(use, ("Other", new Color(220, 220, 220), new Color(150, 150, 150), 0));
        var name = "Test-Fit " + label + (strong ? " (chart)" : "");
        var all = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
        var type = all.FirstOrDefault(t => t.Name == name) ?? (FilledRegionType)all.First().Duplicate(name);
        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting);
        if (solid != null) type.ForegroundPatternId = solid.Id;
        type.ForegroundPatternColor = strong ? bar : fill;
        type.BackgroundPatternId = ElementId.InvalidElementId;
        type.IsMasking = false;
        return type.Id;
    }

    // ---------- the 3D view ----------

    static View3D Axo(Document doc, Level level, TestFitSite site, string name, ElementId? phase)
    {
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
        var view = View3D.CreateIsometric(doc, vft.Id);
        view.Name = name + " - 3D";
        if (phase != null) view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.Set(phase);
        var pts = site.Faces.SelectMany(f => f).SelectMany(l => l).ToList();
        // cut just under the floor above so the layout shows from above
        view.SetSectionBox(new BoundingBoxXYZ
        {
            Min = new XYZ(Ft(pts.Min(p => p.X) - 500), Ft(pts.Min(p => p.Y) - 500), level.ProjectElevation - Ft(300)),
            Max = new XYZ(Ft(pts.Max(p => p.X) + 500), Ft(pts.Max(p => p.Y) + 500), level.ProjectElevation + Ft(Math.Max(2400, site.StoreyHeightMm - 300))),
        });
        view.DisplayStyle = DisplayStyle.ShadingWithEdges;
        view.Scale = 200;
        return view;
    }

    // ---------- the figures ----------

    static ViewDrafting Summary(Document doc, TestFitLayout layout, Level level, string? option, string name)
    {
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.Drafting);
        var view = ViewDrafting.Create(doc, vft.Id);
        view.Name = name + " - Summary";
        view.Scale = 1; // drawn at paper size
        var text = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var y = 0.0; // paper mm, going down
        void Line(double x, string s, double step = 6) { TextNote.Create(doc, view.Id, new XYZ(Ft(x), Ft(-y), 0), s, text); y += step; }
        void Row(params (double X, string S)[] cells) { foreach (var (x, s) in cells) TextNote.Create(doc, view.Id, new XYZ(Ft(x), Ft(-y), 0), s, text); y += 5.5; }
        void Box(double x, double w, double h, ElementId type)
        {
            var l = new CurveLoop();
            XYZ P(double a, double b) => new(Ft(a), Ft(-b), 0);
            l.Append(Autodesk.Revit.DB.Line.CreateBound(P(x, y), P(x + w, y)));
            l.Append(Autodesk.Revit.DB.Line.CreateBound(P(x + w, y), P(x + w, y + h)));
            l.Append(Autodesk.Revit.DB.Line.CreateBound(P(x + w, y + h), P(x, y + h)));
            l.Append(Autodesk.Revit.DB.Line.CreateBound(P(x, y + h), P(x, y)));
            FilledRegion.Create(doc, type, view.Id, new List<CurveLoop> { l });
        }

        Line(0, $"OFFICE TEST-FIT  ·  {level.Name}  ·  {option ?? "Main model"}", 9);
        var s = layout.Summary;
        if (s == null)
        {
            Line(0, $"{layout.Desks} workstations on {layout.NiaM2:0} m² NIA.");
            return view;
        }
        Row((0, "NIA"), (45, $"{s.Nia:0} m²"), (85, "Gross floor"), (130, $"{s.Gross:0} m²"));
        Row((0, "Not in NIA"), (45, $"{s.NotNia:0} m²"), (85, "Total seats"), (130, $"{s.TotalSeats}"));
        Row((0, "Workstations"), (45, $"{s.Desks} of {s.Target}"), (85, "Density"), (130, $"1:{s.Achieved:0.0} m²/ws (target 1:{s.Density:0.0}, {s.Band})"));
        y += 4;

        // the space mix: a stacked bar, then its table
        Line(0, "SPACE MIX (% OF NIA)", 7);
        const double barW = 180, barH = 8;
        var x = 0.0;
        foreach (var m in s.Mix.Where(m => m.Pct > 0))
        {
            var w = barW * Math.Min(1, m.Pct / 100);
            if (w > 0.5) Box(x, w, barH, RegionType(doc, m.Type, strong: true));
            x += w;
        }
        y += barH + 4;
        foreach (var m in s.Mix)
        {
            Box(0, 4, 4, RegionType(doc, m.Type, strong: true));
            Row((7, m.Label), (75, $"{m.M2:0} m²"), (105, $"{m.Pct:0.#}%"));
        }
        y += 4;
        if (s.Seats.Count > 0)
        {
            Line(0, "SEATS BY SETTING", 7);
            foreach (var (n, c) in s.Seats) Row((0, n), (75, c.ToString()));
            y += 4;
        }
        Line(0, "PARTITIONS", 7);
        Row((0, "Glazed"), (45, $"{s.GlazedM:0.0} m"), (85, "Solid"), (130, $"{s.SolidM:0.0} m"));
        Row((0, "Doors"), (45, s.Doors.ToString()));
        y += 4;

        Line(0, "SPACE SCHEDULE", 7);
        Row((0, "Space"), (60, "Use"), (105, "m²"), (125, "Seats / desks"));
        foreach (var z in layout.Zones)
        {
            var count = z.Type is "work" or "hotdesk" ? layout.Items.Count(it => it.Zone == z.Id && it.Kind == "desk") : z.Seats;
            Row((0, z.Name), (60, Uses.GetValueOrDefault(z.Type).Label + (z.NotNia && z.Type != "core" ? " (not NIA)" : "")), (105, $"{z.AreaM2:0}"), (125, count > 0 ? count.ToString() : "–"));
        }
        if (s.Checks.Count > 0)
        {
            y += 4;
            Line(0, "CHECKS", 7);
            foreach (var (lvl, c) in s.Checks) Line(0, (lvl.Length > 0 ? lvl + ": " : "") + c, 5.5);
        }
        return view;
    }
}
