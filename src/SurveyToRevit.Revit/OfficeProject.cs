using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Revit;

/// <summary>
/// A survey project made from the office project template (the Barr Gazetas one unless set otherwise in the settings):
/// the whole template comes with it (title blocks, sheets to fill in, view templates, phases, parameters, levels), and
/// the build fills it: the template's levels moved to the survey's heights, its existing-survey plans, its sheets.
/// </summary>
public static class OfficeProject
{
    /// <summary>The project already comes from an office template: it has existing-drawing sheets to fill in.</summary>
    public static bool IsFromTemplate(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Any(s => s.Name.Contains("(View Name)", StringComparison.OrdinalIgnoreCase));

    public static string Template => UserSettings.Load().Template;
    public static bool TemplateAvailable => File.Exists(Template);

    /// <summary>A name for the new project from the survey's folder: "&lt;folder&gt; - Existing.rvt".</summary>
    public static string SuggestedName(string surveyFile)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(surveyFile)) ?? "Survey";
        foreach (var c in Path.GetInvalidFileNameChars()) folder = folder.Replace(c, '_');
        if (folder.Length > 60) folder = folder[..60].Trim();
        return folder + " - Existing.rvt";
    }

    /// <summary>Makes the project from the template, saves it at path and opens it as the active project.</summary>
    public static UIDocument Create(UIApplication app, string path, string? template = null)
    {
        template ??= Template;
        if (!File.Exists(template)) throw new FileNotFoundException($"There is no template {template}.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var doc = app.Application.NewProjectDocument(template);
        doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
        return app.OpenAndActivateDocument(path);
    }
}
