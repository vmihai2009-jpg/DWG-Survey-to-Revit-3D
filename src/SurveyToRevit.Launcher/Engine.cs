using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Launcher;

/// <summary>
/// Runs a tool from the installed engine. On every click it looks again for the copy to run (the newest,
/// or the one chosen in Settings), so a version installed by the Updater is used straight away.
/// Each version is loaded once per Revit session and then reused.
/// </summary>
internal static class Engine
{
    static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static Version LauncherVersion => typeof(Engine).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static string LauncherFolder => Path.GetDirectoryName(typeof(Engine).Assembly.Location)!;

    public static List<Install> Installs() => Releases.List(Releases.EngineRoot);

    /// <summary>The engine the next click will run. Falls back to one built next to the launcher (manual installs).</summary>
    public static Install? Current(UserSettings settings)
    {
        var pick = Releases.Pick(Installs(), settings.RunVersion);
        if (pick != null) return pick;
        var beside = Path.Combine(LauncherFolder, Installer.EngineDll);
        return File.Exists(beside) ? new Install(LauncherFolder, "", Installer.VersionOf(beside)) : null;
    }

    /// <summary>The engine's main assembly, loaded once per Revit session.</summary>
    public static Assembly Load(Install engine)
    {
        if (!Loaded.TryGetValue(engine.Folder, out var assembly))
            Loaded[engine.Folder] = assembly = new EngineContext(engine.Folder).LoadMain("SurveyToRevit.Revit");
        return assembly;
    }

    public static Result Run(string command, ExternalCommandData data, ref string message, ElementSet elements)
    {
        var engine = Current(UserSettings.Load());
        if (engine == null)
        {
            message = "Survey to Revit is not fully installed. Run install.cmd from the release zip, or use the Updater.";
            return Result.Failed;
        }
        Assembly assembly;
        try { assembly = Load(engine); }
        catch (Exception ex)
        {
            message = $"Could not load Survey to Revit {engine} from {engine.Folder}: {ex.Message}";
            return Result.Failed;
        }
        var type = assembly.GetType(command);
        if (type == null || Activator.CreateInstance(type) is not IExternalCommand tool)
        {
            message = $"This tool is not in Survey to Revit {engine}. Install a newer release with the Updater.";
            return Result.Failed;
        }
        return tool.Execute(data, ref message, elements);
    }
}
