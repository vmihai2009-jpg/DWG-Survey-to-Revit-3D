using Autodesk.Revit.DB;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>
/// The drawings of the survey on sheets: an elevation view looking at each side of the building, then every floor plan,
/// elevation and section on a sheet of its own with the project's title block. In a project made from an office template
/// that has existing-drawing sheets to fill in ("Existing (View Name) Plan" and the like), each sheet is made like that one
/// (its title block, its parameters, its number series); otherwise plain sheets on the project's title block.
/// A rebuild fills the same sheets again.
/// </summary>
public sealed partial class ModelBuilder
{
    static readonly (string Name, XYZ Dir)[] Sides =
        [("North", XYZ.BasisY), ("East", XYZ.BasisX), ("South", -XYZ.BasisY), ("West", -XYZ.BasisX)];

    void BuildSheets(Dictionary<SurveyLevel, (Level level, ViewPlan? view)> levels)
    {
        // the building: our own walls (the neighbours' context left out)
        var box = OurExtent();
        if (box == null) { Log.Add("Sheets: nothing built to put on sheets."); return; }
        var settings = SurveyToRevit.Shared.UserSettings.Load();
        var prefix = settings.ExistingViewPrefix ?? "";
        bool office = levels.Values.Any(v => v.view != null && prefix.Length > 0 && v.view.Name.StartsWith(prefix, StringComparison.Ordinal));

        using var t = new Transaction(_doc, "Survey sheets");
        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new WarningSwallower()));
        t.Start();
        var plans = levels.OrderBy(kv => kv.Value.level.Elevation).Where(kv => kv.Value.view != null).Select(kv => (kv.Key, kv.Value.level, view: kv.Value.view!)).ToList();
        // the office's existing-drawing view templates (BG: *BGA_01_FP_Existing, *BGA_01_SE_Existing), on our views
        var planTemplate = ExistingTemplate(ViewType.FloorPlan);
        var sectionTemplate = ExistingTemplate(ViewType.Section) ?? ExistingTemplate(ViewType.Elevation);
        foreach (var (_, _, view) in plans) { Existing(view, planTemplate); CropPlan(view, box); }
        var elevations = plans.Count > 0 ? Elevations(box, plans[0].view, office ? prefix : null) : new List<(string, View)>();
        var sections = new FilteredElementCollector(_doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.Section && v.Name.StartsWith("Survey Section ", StringComparison.Ordinal)).OrderBy(v => v.Name).ToList();
        foreach (var (_, view) in elevations) Existing(view, sectionTemplate);
        foreach (var view in sections) Existing(view, sectionTemplate);
        _doc.Regenerate();

        // numbered in the office's existing-drawing series (BG: 10.200 Ground Floor plan, 10.202 Second Floor, 10.270 the
        // first elevation); every plan at the same place on its sheet, so the floors line up when flicked through
        string Num(int n) => settings.ExistingSheetPrefix + n.ToString("000");
        string Group(string format, int first) { try { return string.Format(format, Num(first)); } catch { return format; } }
        var made = new List<string>();
        var ground = plans.Count > 0 ? plans[0].level.Elevation : box.Min.Z;
        var centre = new XYZ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, ground);
        var used = new HashSet<int>();
        foreach (var (sl, level, view) in plans)
        {
            // the Ground Floor is the base number, floors up count on, basements count down (B1 = 199)
            var code = PlanCode(sl, level);
            int floor = code.StartsWith("B", StringComparison.OrdinalIgnoreCase) && int.TryParse(code[1..], out var b) ? -b
                      : int.TryParse(code, out var f) ? f : 50 + used.Count;
            int n = settings.ExistingPlanBase + floor;
            while (!used.Add(n)) n++;
            if (OnSheet("Plan", Bare(level.Name), Num(n), Group(settings.ExistingPlanGroup, settings.ExistingPlanBase), view, code, new XYZ(centre.X, centre.Y, level.Elevation)) is string s) made.Add(s);
        }
        int k = 0;
        // the middle of the elevations' crop (1 m below the lowest floor to 2 m over the top)
        var mid = new XYZ(centre.X, centre.Y, (box.Min.Z - Ft(1000) + box.Max.Z + Ft(2000)) / 2);
        foreach (var (side, view) in elevations)
            if (OnSheet("Elevation", side, Num(settings.ExistingElevationBase + k++), Group(settings.ExistingElevationGroup, settings.ExistingElevationBase), view, "ZZ", mid) is string s) made.Add(s);
        k = 0;
        foreach (var view in sections)
            if (OnSheet("Section", view.Name["Survey Section ".Length..], Num(settings.ExistingSectionBase + k++), Group(settings.ExistingSectionGroup, settings.ExistingSectionBase), view, "ZZ", null) is string s) made.Add(s);
        t.Commit();
        Log.Add(made.Count > 0 ? $"Sheets: {string.Join("; ", made)}." : "Sheets: none made.");
    }

    /// <summary>The level's code for drawing numbers: the office level name's own ("02" of "02_Second Floor"), else from the survey.</summary>
    static string PlanCode(SurveyLevel sl, Level level) =>
        OfficeLevelName.Match(level.Name) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : OfficeLevelCode(sl);

    /// <summary>A view template for existing drawings of this kind: named for "Existing", plan ones preferring "FP", section ones "SE".</summary>
    View? ExistingTemplate(ViewType type) =>
        new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.IsTemplate && v.ViewType == type && v.Name.Contains("Existing", StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.Name.Contains(type == ViewType.FloorPlan ? "_FP_" : "_SE_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();

    /// <summary>An existing drawing: the survey's phase, and the office's existing view template when there is one.</summary>
    void Existing(View view, View? template)
    {
        if (template != null) try { view.ViewTemplateId = template.Id; } catch (Exception ex) { Log.Add($"Sheets: template {template.Name} not applied to {view.Name} ({ex.Message})."); }
        if (_phase != null) try { if (view.get_Parameter(BuiltInParameter.VIEW_PHASE) is { IsReadOnly: false } p) p.Set(_phase.Id); } catch { }
    }

    bool _linkDrawings;

    /// <summary>
    /// The survey's own elevation of this side, linked into the elevation view and moved onto the model: the drawing the
    /// elevation index matched to the facade nearest the viewer on this side, placed by that match (distance along the
    /// facade from the sheet's x, survey height from the sheet's y). Left out when no drawing shows this side, or its
    /// sheet would have to be mirrored to fit.
    /// </summary>
    void OverlayElevation(ViewSection view, XYZ dir, XYZ centre)
    {
        try
        {
            var outward = new Vec2(dir.X, dir.Y); var c2 = new Vec2(Mm(centre.X), Mm(centre.Y));
            var pick = _model.Elevations
                .Where(e => e.FacadeLine is { Length: 4 } && e.Score >= ElevationIndex.MinScore && e.Parts.Count > 0)
                .Select(e => (E: e, A: new Vec2(e.FacadeLine![0], e.FacadeLine[1]), B: new Vec2(e.FacadeLine[2], e.FacadeLine[3])))
                // square to this side, and on it
                .Where(x => Math.Abs((x.B - x.A).Normalized().Dot(outward)) < 0.2 && (((x.A + x.B) * 0.5) - c2).Dot(outward) > 0)
                .OrderByDescending(x => (((x.A + x.B) * 0.5) - c2).Dot(outward)).FirstOrDefault();
            if (pick.E == null) return;
            var e = pick.E;
            var folder = System.IO.Path.GetDirectoryName(_model.Levels.FirstOrDefault()?.Plan.Path ?? "") ?? "";
            var path = new[] { e.File, e.File + ".dwg", System.IO.Path.Combine(folder, e.File), System.IO.Path.Combine(folder, e.File + ".dwg") }.FirstOrDefault(File.Exists);
            if (path == null) { Log.Add($"Sheets: {view.Name}: the elevation drawing {e.File} was not found to link."); return; }

            var options = new DWGImportOptions { ThisViewOnly = true, Placement = ImportPlacement.Origin, Unit = ImportUnit.Millimeter, ColorMode = ImportColorMode.Preserved };
            if (!_doc.Link(path, options, view, out ElementId id) || _doc.GetElement(id) is not ImportInstance inst) { Log.Add($"Sheets: {view.Name}: {e.File} could not be linked."); return; }
            _doc.Regenerate();
            var t = inst.GetTotalTransform();
            // where a sheet point lands now, and where the match says it belongs
            double sx = (e.Box[0] + e.Box[2]) / 2, sy = (e.Box[1] + e.Box[3]) / 2;
            var part = e.Parts.FirstOrDefault(p => sx >= p.X0 && sx <= p.X1) ?? e.Parts[0];
            var lineDir = (pick.B - pick.A).Normalized();
            double along = e.ShiftMm + (e.Mirrored ? -1 : 1) * e.Scale * sx;
            var at2 = pick.A + lineDir * along;
            var want = new XYZ(Ft(at2.X), Ft(at2.Y), Z(part.A * sy + part.B));
            var now = t.OfPoint(new XYZ(Ft(sx), Ft(sy), 0));
            // the sheet's x must run the way the match says (an import cannot be mirrored)
            var sheetX = t.OfVector(XYZ.BasisX).Normalize();
            var alongDir = new XYZ(lineDir.X, lineDir.Y, 0) * (e.Mirrored ? -1 : 1);
            if (sheetX.DotProduct(alongDir) < 0.9)
            {
                _doc.Delete(id);
                Log.Add($"Sheets: {view.Name}: {e.Title} not overlaid (the drawing runs the other way in this view).");
                return;
            }
            var move = want - now;
            move -= view.ViewDirection * move.DotProduct(view.ViewDirection); // in the view's plane
            // a link comes in pinned: unpinned to move it onto the model, pinned again there
            inst.Pinned = false;
            ElementTransformUtils.MoveElement(_doc, id, move);
            inst.Pinned = true;
            Log.Add($"Sheets: {view.Name}: survey elevation '{e.Title}' ({e.File}) overlaid on facade {e.Facade}.");
        }
        catch (Exception ex) { Log.Add($"Sheets: {view.Name}: elevation drawing not overlaid ({ex.Message})."); }
    }

    static string Bare(string levelName) => OfficeLevelName.Match(levelName) is { Success: true } m ? m.Groups[2].Value : levelName;

    /// <summary>The box round our own walls (model feet); null when none.</summary>
    BoundingBoxXYZ? OurExtent()
    {
        XYZ? min = null, max = null;
        foreach (var w in new FilteredElementCollector(_doc).OfClass(typeof(Wall)).Cast<Wall>())
        {
            var c = w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            if (!c.StartsWith(Tag + "|", StringComparison.Ordinal) || c.StartsWith($"{Tag}|{ContextCode}|", StringComparison.Ordinal)) continue;
            if (w.get_BoundingBox(null) is not { } b) continue;
            min = min == null ? b.Min : new XYZ(Math.Min(min.X, b.Min.X), Math.Min(min.Y, b.Min.Y), Math.Min(min.Z, b.Min.Z));
            max = max == null ? b.Max : new XYZ(Math.Max(max.X, b.Max.X), Math.Max(max.Y, b.Max.Y), Math.Max(max.Z, b.Max.Z));
        }
        foreach (var r in new FilteredElementCollector(_doc).OfClass(typeof(RoofBase)).Cast<RoofBase>())
            if ((r.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(Tag + "|" + RoofCode, StringComparison.Ordinal)
                && r.get_BoundingBox(null) is { } b && max != null)
                max = new XYZ(max.X, max.Y, Math.Max(max.Z, b.Max.Z));
        return min == null || max == null ? null : new BoundingBoxXYZ { Min = min, Max = max };
    }

    /// <summary>The plan cropped to the building with 2 m round it.</summary>
    void CropPlan(ViewPlan view, BoundingBoxXYZ box)
    {
        try
        {
            var crop = view.CropBox;
            var inv = crop.Transform.Inverse;
            var pts = Corners(box, Ft(2000)).Select(inv.OfPoint).ToList();
            crop.Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), crop.Min.Z);
            crop.Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), crop.Max.Z);
            view.CropBox = crop;
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
        }
        catch (Exception ex) { Log.Add($"Sheets: {view.Name} not cropped ({ex.Message})."); }
    }

    static IEnumerable<XYZ> Corners(BoundingBoxXYZ b, double grow)
    {
        foreach (var x in new[] { b.Min.X - grow, b.Max.X + grow })
            foreach (var y in new[] { b.Min.Y - grow, b.Max.Y + grow })
                foreach (var z in new[] { b.Min.Z, b.Max.Z })
                    yield return new XYZ(x, y, z);
    }

    /// <summary>
    /// An elevation looking at each side of the building from 3 m out, cropped to it (2 m round, 1 m below the lowest
    /// floor, 2 m over the top) and seeing as far as its far side. Named like the template's ("01_ZZ_North") in an office
    /// project, "Survey Elevation North" otherwise; an earlier build's are replaced.
    /// </summary>
    List<(string Side, View View)> Elevations(BoundingBoxXYZ box, ViewPlan plan, string? prefix)
    {
        var result = new List<(string, View)>();
        var all = new FilteredElementCollector(_doc).OfClass(typeof(ViewSection)).Cast<ViewSection>().Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation).ToList();
        string NameOf(string side) => prefix != null ? $"{prefix}ZZ_{side}" : $"Survey Elevation {side}";
        var names = Sides.Select(s => NameOf(s.Name)).ToHashSet();
        // the template's own elevation: the pattern for ours (type, view template, office parameters)
        var pattern = all.FirstOrDefault(v => !names.Contains(v.Name));
        var type = pattern != null ? _doc.GetElement(pattern.GetTypeId()) as ViewFamilyType : null;
        type ??= new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.Elevation);
        if (type == null) { Log.Add("Sheets: the project has no elevation view type; no elevations made."); return result; }
        var old = all.Where(v => names.Contains(v.Name)).Select(v => v.Id).ToList();
        try { if (old.Count > 0) _doc.Delete(old); } catch { /* open: Revit names the new ones */ }

        var c = (box.Min + box.Max) / 2;
        double z0 = plan.GenLevel?.Elevation ?? box.Min.Z;
        foreach (var (side, dir) in Sides)
        {
            try
            {
                double reach = Math.Abs(dir.X) > 0 ? (box.Max.X - box.Min.X) / 2 : (box.Max.Y - box.Min.Y) / 2;
                var at = new XYZ(c.X, c.Y, z0) + dir * (reach + Ft(3000));
                var marker = ElevationMarker.CreateElevationMarker(_doc, type.Id, at, plan.Scale > 0 ? plan.Scale : 100);
                ViewSection? view = null; var wrong = new List<ElementId>();
                for (int i = 0; i < 4 && view == null; i++)
                {
                    if (!marker.IsAvailableIndex(i)) continue;
                    var v = marker.CreateElevation(_doc, plan.Id, i);
                    // the view's direction points back at the viewer: out from this side
                    if (v.ViewDirection.DotProduct(dir) > 0.9) view = v; else wrong.Add(v.Id);
                }
                if (wrong.Count > 0) _doc.Delete(wrong);
                if (view == null) { try { _doc.Delete(marker.Id); } catch { } Log.Add($"Sheets: no {side} elevation (the marker gave no view that way)."); continue; }
                try { view.Name = NameOf(side); } catch { }
                // an office template: set up like its existing-survey plans (package, purpose, phase, scale); otherwise like its elevations
                if (prefix != null) CopyViewSettings(_doc, plan, view);
                else if (pattern != null) CopyViewSettings(_doc, pattern, view);
                if (_phase != null) try { view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.Set(_phase.Id); } catch { }
                _doc.Regenerate();
                var crop = view.CropBox;
                var inv = crop.Transform.Inverse;
                var pts = Corners(box, Ft(2000)).Select(inv.OfPoint).ToList();
                double low = inv.OfPoint(new XYZ(c.X, c.Y, box.Min.Z - Ft(1000))).Y, high = inv.OfPoint(new XYZ(c.X, c.Y, box.Max.Z + Ft(2000))).Y;
                crop.Min = new XYZ(pts.Min(p => p.X), Math.Min(low, high), crop.Min.Z);
                crop.Max = new XYZ(pts.Max(p => p.X), Math.Max(low, high), crop.Max.Z);
                view.CropBox = crop;
                view.CropBoxActive = true;
                view.CropBoxVisible = false;
                // as deep as the building, from the marker to its far side
                try
                {
                    view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.Set(1);
                    view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)?.Set(2 * reach + Ft(4000));
                }
                catch { }
                if (_linkDrawings) OverlayElevation(view, dir, c);
                result.Add((side, view));
            }
            catch (Exception ex) { Log.Add($"Sheets: {side} elevation not made ({ex.Message})."); }
        }
        return result;
    }

    /// <summary>
    /// The view on a sheet of its own (the k-th of its kind). In an office template with a sheet to fill in for this kind
    /// ("Existing (View Name) Plan"), made like it: its title block and parameters, "(View Name)" replaced, numbered on from
    /// its number; the first fills that sheet itself. Otherwise a plain sheet "Survey Plan - ..." on the project's title
    /// block. A sheet of the same name from an earlier build is emptied and used again. Returns "number name", or null.
    /// </summary>
    string? OnSheet(string kind, string label, string number, string group, View view, string levelCode, XYZ? alignOn)
    {
        try
        {
            var sheets = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
            var placeholder = sheets.FirstOrDefault(s => s.Name.Contains("(View Name)", StringComparison.OrdinalIgnoreCase)
                                                         && s.Name.StartsWith("Existing", StringComparison.OrdinalIgnoreCase)
                                                         && s.Name.EndsWith(kind, StringComparison.OrdinalIgnoreCase));
            string name = placeholder != null ? placeholder.Name.Replace("(View Name)", label, StringComparison.OrdinalIgnoreCase) : $"Existing {label} {kind}";

            // a rebuild fills the same sheet again
            var sheet = sheets.FirstOrDefault(s => s.Name == name);
            if (sheet == null)
            {
                var tbType = placeholder != null ? TitleBlockOn(placeholder)?.GetTypeId() : null;
                tbType ??= ProjectTitleBlock();
                sheet = ViewSheet.Create(_doc, tbType ?? ElementId.InvalidElementId);
                if (placeholder != null) CopySheet(placeholder, sheet);
            }
            if (sheet.SheetNumber != number && !sheets.Any(s => s.Id != sheet.Id && s.SheetNumber == number))
                try { sheet.SheetNumber = number; } catch { }
            if (sheet.Name != name) try { sheet.Name = name; } catch { }
            if (sheet.LookupParameter("Sheet Level") is { IsReadOnly: false, StorageType: StorageType.String } lp) lp.Set(levelCode);
            // the project browser's folder for this kind ("10.200 Existing Plans")
            if (SurveyToRevit.Shared.UserSettings.Load().SheetGroupParameter is { Length: > 0 } gp
                && sheet.LookupParameter(gp) is { IsReadOnly: false, StorageType: StorageType.String } g) g.Set(group);

            // this sheet emptied of what an earlier build put there (a view someone put on another sheet stays there)
            foreach (var old in new FilteredElementCollector(_doc).OfClass(typeof(Viewport)).Cast<Viewport>().Where(v => v.SheetId == sheet.Id).ToList())
                _doc.Delete(old.Id);
            _doc.Regenerate();
            if (!Viewport.CanAddViewToSheet(_doc, sheet.Id, view.Id)) { Log.Add($"Sheets: {view.Name} cannot go on {sheet.SheetNumber} (it is on another sheet)."); return null; }

            // the drawing area: the title block less a margin and its title strip (A1 without one)
            double x0 = 0, y0 = 0, x1 = Ft(841), y1 = Ft(594);
            if (TitleBlockOn(sheet)?.get_BoundingBox(sheet) is { } bb) { x0 = bb.Min.X; y0 = bb.Min.Y; x1 = bb.Max.X; y1 = bb.Max.Y; }
            x0 += Ft(25); y0 += Ft(25); y1 -= Ft(25);
            x1 -= (x1 - x0) > Ft(700) ? Ft(130) : Ft(25); // the title strip down the right (BG A1: about 110 mm)
            FitScale(view, Mm(x1 - x0), Mm(y1 - y0));
            var middle = new XYZ((x0 + x1) / 2, (y0 + y1) / 2, 0);
            var vp = Viewport.Create(_doc, sheet.Id, view.Id, middle);
            // the building's centre in the middle of the drawing area, whatever annotation (level heads, elevation marks)
            // runs past the crop: the same point on every sheet of a kind, so the floors line up when flicked through
            if (alignOn != null)
                try
                {
                    _doc.Regenerate();
                    // model -> the view's projection space -> the sheet
                    var toProjection = view.GetModelToProjectionTransforms().First().GetModelToProjectionTransform();
                    var at = vp.GetProjectionToSheetTransform().OfPoint(toProjection.OfPoint(alignOn));
                    var shift = middle - new XYZ(at.X, at.Y, 0);
                    // never off the sheet: a shift larger than the sheet means the sums went wrong; leave it centred as placed
                    if (shift.GetLength() < Ft(400)) vp.SetBoxCenter(vp.GetBoxCenter() + shift);
                    else Log.Add($"Sheets: {view.Name} left as placed (centring gave {Mm(shift.GetLength()):0} mm).");
                }
                catch (Exception ex) { Log.Add($"Sheets: {view.Name} not centred on its sheet ({ex.Message})."); }
            try { if (view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION) is { IsReadOnly: false } title) title.Set(name); } catch { }
            return $"{sheet.SheetNumber} - {sheet.Name}";
        }
        catch (Exception ex) { Log.Add($"Sheets: {view.Name} not put on a sheet ({ex.Message})."); return null; }
    }

    /// <summary>The largest usual scale at which the view's crop fits the drawing area (sheet mm); left alone when a view template sets it.</summary>
    static void FitScale(View view, double widthMm, double heightMm)
    {
        try
        {
            var crop = view.CropBox;
            double w = Mm(crop.Max.X - crop.Min.X), h = Mm(crop.Max.Y - crop.Min.Y);
            // survey GAs: 1:100 unless the building needs smaller
            int scale = new[] { 100, 200, 250, 500, 1000 }.FirstOrDefault(s => w / s <= widthMm && h / s <= heightMm);
            view.Scale = scale == 0 ? 1000 : scale;
        }
        catch { /* the view template owns the scale */ }
    }

    FamilyInstance? TitleBlockOn(ViewSheet sheet) =>
        new FilteredElementCollector(_doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault();

    /// <summary>The title block most sheets use (an office template's own), else an A1 one, else any.</summary>
    ElementId? ProjectTitleBlock()
    {
        var used = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance))
            .GroupBy(e => e.GetTypeId().Value).OrderByDescending(g => g.Count()).Select(g => new ElementId(g.Key)).FirstOrDefault();
        if (used != null) return used;
        var symbol = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .OrderBy(s => s.Name.Contains("A1", StringComparison.OrdinalIgnoreCase) || s.Family.Name.Contains("A1", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        if (symbol != null && !symbol.IsActive) symbol.Activate();
        return symbol?.Id;
    }

    /// <summary>The template sheet's own settings on a new sheet: its writable parameters (status, purpose, view type,
    /// zone, guide grid...) and its title block's.</summary>
    void CopySheet(ViewSheet from, ViewSheet to)
    {
        CopyParameters(from, to, BuiltInParameter.SHEET_NAME, BuiltInParameter.SHEET_NUMBER, BuiltInParameter.VIEW_NAME);
        _doc.Regenerate();
        if (TitleBlockOn(from) is { } a && TitleBlockOn(to) is { } b)
            CopyParameters(a, b, BuiltInParameter.SHEET_NAME, BuiltInParameter.SHEET_NUMBER);
    }

    static void CopyParameters(Element from, Element to, params BuiltInParameter[] skip)
    {
        foreach (Parameter p in from.Parameters)
            try
            {
                if (p.IsReadOnly || !p.HasValue || p.Definition is not { } d) continue;
                if (d is InternalDefinition id && skip.Contains(id.BuiltInParameter)) continue;
                var q = d is InternalDefinition { BuiltInParameter: not BuiltInParameter.INVALID } bi ? to.get_Parameter(bi.BuiltInParameter) : to.LookupParameter(d.Name);
                if (q == null || q.IsReadOnly || q.StorageType != p.StorageType) continue;
                switch (p.StorageType)
                {
                    case StorageType.String: q.Set(p.AsString() ?? ""); break;
                    case StorageType.Integer: q.Set(p.AsInteger()); break;
                    case StorageType.Double: q.Set(p.AsDouble()); break;
                    case StorageType.ElementId: q.Set(p.AsElementId()); break;
                }
            }
            catch { /* one the sheet does not take */ }
    }
}
