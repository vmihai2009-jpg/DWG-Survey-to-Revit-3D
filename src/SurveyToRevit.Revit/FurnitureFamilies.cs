using Autodesk.Revit.DB;

namespace SurveyToRevit.Revit;

/// <summary>
/// Simple furniture families of our own, for a project and Revit library that have none of a kind (a desk,
/// chair, sofa, table, plant...). Each is made inside Revit from Revit's installed furniture family template
/// at the size the test-fit drew, out of a few plain blocks shaped like the piece, front facing -Y like
/// Autodesk's families. Nothing is shipped with the add-in.
/// </summary>
internal static class FurnitureFamilies
{
    public const string Prefix = "Test-Fit ";

    static double Ft(double mm) => mm / 304.8;

    /// <summary>The family's name for a kind and size, e.g. "Test-Fit Sofa 2200x850".</summary>
    public static string Name(string kind, double widthMm, double depthMm) =>
        $"{Prefix}{Label(kind)} {Math.Round(widthMm / 10) * 10:0}x{Math.Round(depthMm / 10) * 10:0}";

    static string Label(string kind) => kind switch
    {
        "recdesk" => "Reception Desk", "arm" => "Armchair", "coffee" => "Coffee Table", "meettable" => "Meeting Table",
        "hightable" => "High Table", "kitchen" => "Kitchen Counter", _ => char.ToUpperInvariant(kind[0]) + kind[1..],
    };

    /// <summary>Makes and loads the family (or finds it from an earlier send); null, with why in the log, if Revit has no furniture template.</summary>
    public static Family? Make(Document project, string kind, double widthMm, double depthMm, List<string> log)
    {
        var name = Name(kind, widthMm, depthMm);
        var existing = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name);
        if (existing != null) return existing;
        var app = project.Application;
        var template = ConceptFamilies.TemplateRoots(app).Select(r => ConceptFamilies.FindTemplate(r, "Furniture")).FirstOrDefault(t => t != null);
        if (template == null) { log.Add("No furniture family template (.rft) in Revit's Family Templates folder, so no furniture family could be made."); return null; }
        Document? fam = null;
        var path = Path.Combine(Path.GetTempPath(), "SurveyToRevit-families", name + ".rfa");
        try
        {
            fam = app.NewFamilyDocument(template);
            using (var t = new Transaction(fam, "Test-fit furniture"))
            {
                t.Start();
                foreach (var (x0, y0, x1, y1, z0, z1, round) in Parts(kind, widthMm, depthMm)) Block(fam, x0, y0, x1, y1, z0, z1, round);
                t.Commit();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            fam.Close(false);
            fam = null;
            var loaded = ConceptFamilies.Load(project, path);
            if (loaded == null) log.Add($"The family '{name}' was made but Revit would not load it.");
            return loaded;
        }
        catch (Exception ex)
        {
            log.Add($"The family '{name}' could not be made ({ex.Message}).");
            try { fam?.Close(false); } catch { }
            return null;
        }
    }

    /// <summary>The blocks a piece is made of (mm, centred on the origin, front towards -Y); round = a cylinder in the box.</summary>
    static IEnumerable<(double, double, double, double, double, double, bool)> Parts(string kind, double w, double d)
    {
        double hw = w / 2, hd = d / 2;
        (double, double, double, double, double, double, bool) B(double x0, double y0, double x1, double y1, double z0, double z1, bool round = false) =>
            (x0, y0, x1, y1, z0, z1, round);
        switch (kind)
        {
            case "desk":
                yield return B(-hw, -hd, hw, hd, 715, 740);                         // top
                yield return B(-hw + 20, -hd + 60, -hw + 60, hd - 60, 0, 715);      // legs
                yield return B(hw - 60, -hd + 60, hw - 20, hd - 60, 0, 715);
                yield return B(-hw + 10, hd - 30, hw - 10, hd - 10, 740, 1150);     // screen at the back
                break;
            case "recdesk":
                yield return B(-hw, -hd, hw, hd - 300, 715, 740);                   // worktop (receptionist side, -Y)
                yield return B(-hw, hd - 300, hw, hd, 0, 1100);                     // counter towards visitors
                yield return B(-hw, -hd, -hw + 40, hd - 300, 0, 715);
                yield return B(hw - 40, -hd, hw, hd - 300, 0, 715);
                break;
            case "chair":
            case "stool":
            {
                var seat = kind == "stool" ? 720.0 : 440;
                yield return B(-hw * 0.9, -hd * 0.9, hw * 0.9, hd * 0.85, seat, seat + 60, kind == "stool");
                yield return B(-30, -30, 30, 30, 40, seat);                         // post
                yield return B(-hw * 0.9, -30, hw * 0.9, 30, 0, 40);                // base
                yield return B(-30, -hd * 0.9, 30, hd * 0.9, 0, 40);
                if (kind == "chair") yield return B(-hw * 0.85, hd * 0.85 - 60, hw * 0.85, hd * 0.85, seat + 60, 950); // back
                break;
            }
            case "arm":
            case "sofa":
                yield return B(-hw, -hd, hw, hd - 180, 0, 420);                     // seat
                yield return B(-hw, hd - 180, hw, hd, 0, 800);                      // back
                yield return B(-hw, -hd, -hw + 140, hd - 180, 420, 620);            // arms
                yield return B(hw - 140, -hd, hw, hd - 180, 420, 620);
                break;
            case "bench":
                yield return B(-hw, -hd, hw, hd - 120, 0, 450);
                yield return B(-hw, hd - 120, hw, hd, 0, 1200);                     // high back (booth)
                break;
            case "coffee":
                yield return B(-hw, -hd, hw, hd, 380, 420);
                yield return B(-hw + 80, -hd + 80, hw - 80, hd - 80, 0, 380);
                break;
            case "hightable":
                yield return B(-hw, -hd, hw, hd, 1010, 1050);
                yield return B(-hw + 200, -hd + 150, -hw + 260, hd - 150, 0, 1010);
                yield return B(hw - 260, -hd + 150, hw - 200, hd - 150, 0, 1010);
                break;
            case "kitchen": // base units with a worktop, the user side towards -Y; a tall fridge housing at the +X end
                yield return B(-hw, -hd + 50, hw - 600, hd, 0, 870);
                yield return B(-hw, -hd, hw - 600, hd, 870, 910);
                yield return B(-hw + 300, -hd + 80, -hw + 800, hd - 120, 860, 912);  // sink
                yield return B(hw - 600, -hd, hw, hd, 0, 2000);
                break;
            case "monitor": // stands on the desk top (740 mm)
                yield return B(-100, -40, 100, 80, 740, 755);
                yield return B(-20, 40, 20, 70, 755, 900);
                yield return B(-hw, 70, hw, 95, 900, 1230);
                break;
            case "plant":
                yield return B(-hw * 0.7, -hd * 0.7, hw * 0.7, hd * 0.7, 0, 450, true);   // pot
                yield return B(-hw, -hd, hw, hd, 450, 1500, true);                      // foliage
                break;
            default: // table, meeting table
                yield return B(-hw, -hd, hw, hd, 720, 760);
                foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    double cx = sx * (hw - 100), cy = sy * (hd - 100);
                    yield return B(cx - 30, cy - 30, cx + 30, cy + 30, 0, 720);
                }
                break;
        }
    }

    static void Block(Document fam, double x0, double y0, double x1, double y1, double z0, double z1, bool round)
    {
        var loop = new CurveArray();
        if (round)
        {
            var c = new XYZ(Ft((x0 + x1) / 2), Ft((y0 + y1) / 2), 0);
            var r = Ft(Math.Min(x1 - x0, y1 - y0) / 2);
            loop.Append(Arc.Create(c, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            loop.Append(Arc.Create(c, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
        }
        else
        {
            var p = new[] { new XYZ(Ft(x0), Ft(y0), 0), new XYZ(Ft(x1), Ft(y0), 0), new XYZ(Ft(x1), Ft(y1), 0), new XYZ(Ft(x0), Ft(y1), 0) };
            for (var i = 0; i < 4; i++) loop.Append(Line.CreateBound(p[i], p[(i + 1) % 4]));
        }
        var profile = new CurveArrArray();
        profile.Append(loop);
        var sketch = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        var e = fam.FamilyCreate.NewExtrusion(true, profile, sketch, Ft(z1 - z0));
        e.StartOffset = Ft(z0);
        e.EndOffset = Ft(z1);
    }
}
