using System.IO.Compression;
using System.Reflection;

namespace SurveyToRevit.Launcher;

/// <summary>What an install put in place. Launcher is null when the release's launcher was not newer than the one running.</summary>
internal sealed record Installed(Version Engine, string EngineFolder, Version? Launcher);

/// <summary>
/// Installs a release from its zip without restarting Revit: the engine goes into a new folder that the
/// next click runs; the launcher (ribbon, Updater, Settings) is only replaced when the release has a newer
/// one, and that part shows after the next Revit restart. Nothing running is touched or deleted.
/// </summary>
internal static class Installer
{
    public const string EngineDll = "SurveyToRevit.Revit.dll";
    public const string LauncherDll = "SurveyToRevit.Launcher.dll";
    const string AddInId = "01d8ac57-f4f6-4331-9bb8-767badc5ce76";

    public static Installed FromZip(string zip, Version? runningLauncher, DateTime now, string? root = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), "SurveyToRevit-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zip, temp);
            var source = Directory.GetFiles(temp, EngineDll, SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                ?? throw new InvalidDataException("That zip is not a Survey to Revit release (there is no SurveyToRevit.Revit.dll inside).");
            return FromFolder(source!, runningLauncher, now, root ?? Releases.Root);
        }
        finally { try { Directory.Delete(temp, true); } catch { /* temp folder, Windows clears it */ } }
    }

    public static Installed FromFolder(string source, Version? runningLauncher, DateTime now, string root)
    {
        static bool IsLauncher(string f) => Path.GetFileName(f).StartsWith("SurveyToRevit.Launcher", StringComparison.OrdinalIgnoreCase);

        var engineVersion = VersionOf(Path.Combine(source, EngineDll));
        var engine = Copy(source, Path.Combine(root, "engine", Releases.NewFolderName(engineVersion, now)), f => !IsLauncher(f));

        Version? launcherVersion = null;
        var launcher = Path.Combine(source, LauncherDll);
        if (File.Exists(launcher) && (runningLauncher == null || VersionOf(launcher) > runningLauncher))
        {
            launcherVersion = VersionOf(launcher);
            var folder = Copy(source, Path.Combine(root, "launcher", Releases.NewFolderName(launcherVersion, now)), IsLauncher);
            var addins = Path.GetDirectoryName(root)!;
            WriteManifest(addins, Path.GetRelativePath(addins, Path.Combine(folder, LauncherDll)));
        }
        return new(engineVersion, engine, launcherVersion);
    }

    public static Version VersionOf(string dll) => AssemblyName.GetAssemblyName(dll).Version ?? new Version(0, 0, 0);

    /// <summary>Copies into a temporary name first, so a half-finished copy is never picked up as an install.</summary>
    static string Copy(string source, string target, Func<string, bool> include)
    {
        var partial = target + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        Directory.CreateDirectory(partial);
        foreach (var f in Directory.GetFiles(source).Where(include)
                     .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".pdb" or ".json"))
            File.Copy(f, Path.Combine(partial, Path.GetFileName(f)), true);
        Directory.Move(partial, target);
        return target;
    }

    /// <summary>The .addin file Revit reads at start-up, pointing at the launcher (path relative to the Addins folder).</summary>
    public static void WriteManifest(string addinsFolder, string launcherPath)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <RevitAddIns>
              <AddIn Type="Application">
                <Name>Survey to Revit</Name>
                <Assembly>{launcherPath}</Assembly>
                <AddInId>{AddInId}</AddInId>
                <FullClassName>SurveyToRevit.Launcher.App</FullClassName>
                <VendorId>SURVEYTOREVIT</VendorId>
                <VendorDescription>DWG survey to Revit concept model</VendorDescription>
              </AddIn>
            </RevitAddIns>
            """;
        var path = Path.Combine(addinsFolder, "SurveyToRevit.addin");
        File.WriteAllText(path + ".tmp", xml);
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>
    /// Deletes copies no longer needed: older engines beyond the number to keep, launchers other than the
    /// one running, and folders left by v0.12 installs. Anything Windows still holds is left for next time.
    /// </summary>
    public static int Tidy(string root, int keepOlder, string? runningEngine, string? chosenEngine, string? runningLauncherFolder)
    {
        var gone = 0;
        void Delete(string dir)
        {
            try { Directory.Delete(dir, true); gone++; } catch { /* still in use */ }
        }
        foreach (var old in Releases.ToRemove(Releases.List(Path.Combine(root, "engine")), keepOlder, runningEngine, chosenEngine))
            Delete(old.Folder);
        foreach (var old in Releases.List(Path.Combine(root, "launcher")).Where(l => !SameFolder(l.Folder, runningLauncherFolder)).SkipLast(1))
            Delete(old.Folder);
        if (Directory.Exists(root))
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                // v0.12 put each install straight in here as "yyyyMMdd-HHmmss"; also failed half-copies.
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{8}-\d{6}(\.old)?$") || name.EndsWith(".partial"))
                    if (!SameFolder(dir, runningLauncherFolder)) Delete(dir);
            }
        return gone;
    }

    static bool SameFolder(string a, string? b) =>
        b != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
