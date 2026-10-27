using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SurveyToRevit.Core;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Revit;

/// <summary>
/// The page's Assets tab: which families each kind of furniture can use (the project's own first, then Revit's
/// Autodesk library, then the add-in's plain family), and the real size of each type of a family picked there.
/// </summary>
internal static class TestFitAssets
{
    public const string Own = "own";

    /// <summary>The families a desk, chair, sofa... can use, best first (the first is what the add-in picks by itself).</summary>
    public static void Catalog(Document doc, TestFitSite site)
    {
        var library = TestFitBuilder.Library(doc.Application);
        var project = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Where(TestFitBuilder.Fits)
            .Where(f => !f.Name.StartsWith(FurnitureFamilies.Prefix, StringComparison.Ordinal)).OrderBy(f => f.Name).ToList();
        foreach (var (kind, (want, skip, _, _)) in TestFitBuilder.Kinds)
        {
            var list = new List<SiteAsset>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string id, string name, string source) { if (seen.Add(name)) list.Add(new SiteAsset(id, name, source)); }
            foreach (var pattern in want)
                foreach (var f in project.Where(f => Regex.IsMatch(f.Name, pattern, RegexOptions.IgnoreCase) && !Regex.IsMatch(f.Name, skip, RegexOptions.IgnoreCase)).OrderBy(f => f.Name.Length))
                    Add("p:" + f.Id.Value, f.Name, "In the project");
            foreach (var pattern in want)
                foreach (var file in TestFitBuilder.Matching(library, pattern, skip).Take(20))
                    Add("f:" + file, Path.GetFileNameWithoutExtension(file), "Autodesk library");
            Add(Own, "Plain family made by the add-in", "Add-in");
            // anything else in the project's furniture can be linked by hand
            foreach (var f in project) Add("p:" + f.Id.Value, f.Name, "Other project families");
            site.Assets[kind] = list;
        }
    }

    /// <summary>
    /// The types of a family picked on the page with their plan size and height (mm), measured from an instance
    /// placed and then taken out again; a library family is loaded only for the measuring (it loads for real on
    /// Send to Revit). Returns JSON for the page: {types:[{name,w,d,h,sizable}]} or {error}.
    /// </summary>
    public static string Measure(Document doc, Level level, string id)
    {
        if (id == Own) return JsonSerializer.Serialize(new { types = Array.Empty<object>() });
        using var t = new Transaction(doc, "Test-fit: measure a family");
        try
        {
            t.Start();
            Family? family = id.StartsWith("p:") && long.TryParse(id[2..], out var v) ? doc.GetElement(new ElementId(v)) as Family
                : id.StartsWith("f:") && File.Exists(id[2..]) && doc.LoadFamily(id[2..], out Family loaded) ? loaded : null;
            family ??= id.StartsWith("f:") ? new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name == Path.GetFileNameWithoutExtension(id[2..])) : null; // already in the project
            if (family == null) return JsonSerializer.Serialize(new { error = "That family could not be loaded." });
            if (!TestFitBuilder.Fits(family)) return JsonSerializer.Serialize(new { error = $"'{family.Name}' is not a furniture family placed on a level." });
            var types = new List<object>();
            foreach (var symbol in family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().OrderBy(s => s.Name).Take(24))
            {
                if (!symbol.IsActive) symbol.Activate();
                doc.Regenerate();
                var inst = family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased
                    ? doc.Create.NewFamilyInstance(level.GetPlaneReference(), new XYZ(0, 0, level.ProjectElevation), XYZ.BasisX, symbol)
                    : doc.Create.NewFamilyInstance(new XYZ(0, 0, level.ProjectElevation), symbol, level, StructuralType.NonStructural);
                doc.Regenerate();
                var box = inst.get_BoundingBox(null);
                if (box == null) continue;
                double Mm(double ft) => Math.Round(UnitConversion.FeetToMm(ft));
                types.Add(new { name = symbol.Name, w = Mm(box.Max.X - box.Min.X), d = Mm(box.Max.Y - box.Min.Y), h = Mm(box.Max.Z - box.Min.Z), sizable = Sizable(symbol) });
                doc.Delete(inst.Id);
            }
            return types.Count == 0 ? JsonSerializer.Serialize(new { error = $"'{family.Name}' has no type that could be placed." })
                : JsonSerializer.Serialize(new { family = family.Name, types });
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
        finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
    }

    /// <summary>Whether the type has a width (or length) and a depth Revit can change, so it takes the size the layout gives it.</summary>
    static bool Sizable(FamilySymbol s)
    {
        bool Has(BuiltInParameter bip, params string[] names) =>
            new[] { bip == BuiltInParameter.INVALID ? null : s.get_Parameter(bip) }.Concat(names.Select(s.LookupParameter))
                .Any(p => p is { IsReadOnly: false, StorageType: StorageType.Double });
        return Has(BuiltInParameter.FAMILY_WIDTH_PARAM, "Width", "Length") && Has(BuiltInParameter.INVALID, "Depth");
    }
}
