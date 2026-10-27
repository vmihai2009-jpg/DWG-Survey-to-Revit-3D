using Autodesk.Revit.DB;

namespace SurveyToRevit.Revit;

/// <summary>
/// A window family of our own for projects that have none (an office template without windows): made inside
/// Revit from Revit's own installed window family template (so no Autodesk family is shipped with the add-in).
/// The template already cuts a Width × Height opening; a glass pane is added in the middle of the wall and
/// locked to the template's reference planes, so each Concept type resizes it.
/// </summary>
internal static class ConceptFamilies
{
    public const string WindowName = "SurveyToRevit Concept Window";

    /// <summary>Makes and loads the family; null (and why) if Revit has no window template or refuses.</summary>
    public static Family? Window(Document project, List<string> log)
    {
        var app = project.Application;
        var template = TemplateRoots(app).Select(r => FindTemplate(r, "Window")).FirstOrDefault(t => t != null);
        if (template == null) { log.Add("No window family template (.rft) found in Revit's Family Templates folder, so no window family could be made."); return null; }
        Document? fam = null;
        string path = Path.Combine(Path.GetTempPath(), "SurveyToRevit-families", WindowName + ".rfa");
        try
        {
            fam = app.NewFamilyDocument(template);
            string locked;
            using (var t = new Transaction(fam, "Concept window"))
            {
                t.Start();
                locked = AddPane(fam);
                t.Commit();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            fam.Close(false);
            fam = null;
            if (Load(project, path) is not Family loaded) { log.Add("The concept window family was made but Revit would not load it."); return null; }
            log.Add($"No window family in the project or library: made '{WindowName}' from Revit's template {Path.GetFileName(template)} ({locked}). " +
                    "Swap it for an office family any time (same types, Width and Height).");
            return loaded;
        }
        catch (Exception ex)
        {
            log.Add($"The concept window family could not be made ({ex.Message}).");
            try { fam?.Close(false); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Where Revit's own families are: the library paths set in Revit's options (an office may point these at its
    /// own folder) and then Autodesk's installed content (C:\ProgramData\Autodesk\RVT 2025\Libraries\…).
    /// Loading from these at run time uses the user's own Revit installation; nothing is shipped with the add-in.
    /// </summary>
    public static List<string> LibraryRoots(Autodesk.Revit.ApplicationServices.Application app)
    {
        var roots = new List<string>();
        try { roots.AddRange(app.GetLibraryPaths().Values); } catch { }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", $"RVT {app.VersionNumber}", "Libraries"));
        return roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static List<string> TemplateRoots(Autodesk.Revit.ApplicationServices.Application app)
    {
        var roots = new List<string>();
        try { roots.Add(app.FamilyTemplatePath); } catch { }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", $"RVT {app.VersionNumber}", "Family Templates"));
        return roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Loads a family file into the project, in a transaction of its own when none is open.</summary>
    public static Family? Load(Document project, string path)
    {
        if (project.IsModifiable) return project.LoadFamily(path, new Overwrite(), out Family f) ? f : null;
        using var t = new Transaction(project, "Load " + Path.GetFileNameWithoutExtension(path));
        t.Start();
        Family? loaded = project.LoadFamily(path, new Overwrite(), out Family g) ? g : null;
        if (t.Commit() != TransactionStatus.Committed) return null;
        return loaded;
    }

    internal static string? FindTemplate(string root, string kind)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
        try
        {
            var all = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)
                .Where(f => Path.GetFileNameWithoutExtension(f).Contains(kind, StringComparison.OrdinalIgnoreCase)).ToList();
            // the plain metric one first ("Metric Window.rft"), then any metric, then any
            return all.OrderBy(f => Path.GetFileNameWithoutExtension(f).Equals($"Metric {kind}", StringComparison.OrdinalIgnoreCase) ? 0
                                  : f.Contains("Metric", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                      .ThenBy(f => f.Length).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>A 24 mm glass pane filling the opening, its four edges locked to the reference planes the opening uses.</summary>
    static string AddPane(Document fam)
    {
        double width = Value(fam, BuiltInParameter.WINDOW_WIDTH, "Width") ?? 1000 / 304.8;
        double height = Value(fam, BuiltInParameter.WINDOW_HEIGHT, "Height") ?? 1500 / 304.8;
        double sill = Value(fam, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, "Default Sill Height") ?? 900 / 304.8;

        // the template's reference planes: vertical ones across the wall (left, right), horizontal ones (sill, head)
        var planes = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
        ReferencePlane? Near(Func<ReferencePlane, bool> facing, Func<ReferencePlane, double> at, double want) =>
            planes.Where(facing).OrderBy(p => Math.Abs(at(p) - want)).FirstOrDefault(p => Math.Abs(at(p) - want) < 0.05);
        bool AcrossX(ReferencePlane p) => Math.Abs(p.Normal.X) > 0.99;
        bool AcrossZ(ReferencePlane p) => Math.Abs(p.Normal.Z) > 0.99;
        var left = Near(AcrossX, p => p.BubbleEnd.X, -width / 2);
        var right = Near(AcrossX, p => p.BubbleEnd.X, width / 2);
        var bottom = Near(AcrossZ, p => p.BubbleEnd.Z, sill) ?? Near(AcrossZ, p => p.BubbleEnd.Z, 0);
        var top = Near(AcrossZ, p => p.BubbleEnd.Z, sill + height) ?? Near(AcrossZ, p => p.BubbleEnd.Z, height);
        double z0 = bottom?.BubbleEnd.Z ?? sill, z1 = top?.BubbleEnd.Z ?? sill + height;

        // pane in the wall's middle plane (the template's wall runs along X; Y is through it)
        var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, XYZ.Zero);
        var sketch = SketchPlane.Create(fam, plane);
        var c = new[] { new XYZ(-width / 2, 0, z0), new XYZ(width / 2, 0, z0), new XYZ(width / 2, 0, z1), new XYZ(-width / 2, 0, z1) };
        var loop = new CurveArray();
        for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(c[i], c[(i + 1) % 4]));
        var profile = new CurveArrArray();
        profile.Append(loop);
        double thick = 24 / 304.8;
        var pane = fam.FamilyCreate.NewExtrusion(true, profile, sketch, thick);
        pane.StartOffset = -thick / 2;
        pane.EndOffset = thick / 2;
        var glass = Glass(fam);
        if (glass != null) pane.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(glass.Id);
        fam.Regenerate();

        // lock each edge to its plane, in the exterior (or any) elevation
        var view = new FilteredElementCollector(fam).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation)
            .OrderBy(v => v.Name.Contains("Exterior", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        int locked = 0;
        if (view != null)
        {
            var faces = Faces(pane);
            foreach (var (refPlane, normal) in new[] { (left, -XYZ.BasisX), (right, XYZ.BasisX), (bottom, -XYZ.BasisZ), (top, XYZ.BasisZ) })
            {
                if (refPlane == null) continue;
                var face = faces.FirstOrDefault(f => f.FaceNormal.IsAlmostEqualTo(normal));
                if (face?.Reference == null) continue;
                try { var a = fam.FamilyCreate.NewAlignment(view, refPlane.GetReference(), face.Reference); a.IsLocked = true; locked++; }
                catch { /* that edge stays at its size */ }
            }
        }
        return locked == 4 ? "glass locked to Width and Height" : $"glass locked on {locked} of 4 edges";
    }

    static List<PlanarFace> Faces(Extrusion e)
    {
        var faces = new List<PlanarFace>();
        foreach (var g in e.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true }))
            if (g is Solid s) foreach (Face f in s.Faces) if (f is PlanarFace pf) faces.Add(pf);
        return faces;
    }

    static Material? Glass(Document fam)
    {
        var m = new FilteredElementCollector(fam).OfClass(typeof(Material)).Cast<Material>()
            .FirstOrDefault(x => x.Name.Contains("Glass", StringComparison.OrdinalIgnoreCase));
        if (m != null) return m;
        try
        {
            m = (Material)fam.GetElement(Material.Create(fam, "Glass"));
            m.Color = new Color(150, 190, 220);
            m.Transparency = 60;
            return m;
        }
        catch { return null; }
    }

    static double? Value(Document fam, BuiltInParameter bip, string name)
    {
        var fm = fam.FamilyManager;
        var p = fm.get_Parameter(bip) ?? fm.get_Parameter(name);
        if (p == null || p.StorageType != StorageType.Double) return null;
        return fm.CurrentType?.AsDouble(p);
    }

    sealed class Overwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = false; return true; }
    }
}
