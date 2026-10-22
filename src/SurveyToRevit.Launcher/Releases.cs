using System.Text.RegularExpressions;

namespace SurveyToRevit.Launcher;

/// <summary>One installed copy: a folder named "yyyyMMdd-HHmmssfff_v1.2.3" (when it was installed, and which release).</summary>
internal sealed record Install(string Folder, string Stamp, Version Version)
{
    public string Name => Path.GetFileName(Folder);
    public override string ToString() => "v" + Releases.Short(Version);
}

/// <summary>
/// Where things are installed and which copy to run. No Revit here, so it can be tested anywhere.
/// Layout under %AppData%\Autodesk\Revit\Addins\2025\SurveyToRevit:
///   launcher\&lt;stamp&gt;_v&lt;version&gt;\  the small part Revit loads at start-up (ribbon buttons, Updater, Settings)
///   engine\&lt;stamp&gt;_v&lt;version&gt;\    everything else; the launcher runs the newest one on each click
/// </summary>
internal static class Releases
{
    static readonly Regex ZipName = new(@"SurveyToRevit-(\d+(?:\.\d+){1,3})\.zip$", RegexOptions.IgnoreCase);
    static readonly Regex FolderName = new(@"^(\d{8}-\d{6,9})_v(\d+(?:\.\d+){1,3})$");

    public static string AddinsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2025");
    public static string Root => Path.Combine(AddinsFolder, "SurveyToRevit");
    public static string EngineRoot => Path.Combine(Root, "engine");
    public static string LauncherRoot => Path.Combine(Root, "launcher");

    public static string NewFolderName(Version v, DateTime when) => $"{when:yyyyMMdd-HHmmssfff}_v{Short(v)}";

    /// <summary>The installed copies in a folder, oldest install first.</summary>
    public static List<Install> List(string root)
    {
        if (!Directory.Exists(root)) return new();
        return Directory.GetDirectories(root)
            .Select(d => (Dir: d, M: FolderName.Match(Path.GetFileName(d))))
            .Where(x => x.M.Success && Version.TryParse(x.M.Groups[2].Value, out _))
            .Select(x => new Install(x.Dir, x.M.Groups[1].Value, Version.Parse(x.M.Groups[2].Value)))
            .OrderBy(i => i.Stamp, StringComparer.Ordinal).ToList();
    }

    /// <summary>The copy to run: the one chosen in Settings if it is still there, otherwise the latest installed.</summary>
    public static Install? Pick(IReadOnlyList<Install> installs, string? chosen) =>
        installs.FirstOrDefault(i => !string.IsNullOrEmpty(chosen) && i.Name == chosen) ?? installs.LastOrDefault();

    /// <summary>Older copies beyond the number to keep. The one in use and the chosen one always stay.</summary>
    public static List<Install> ToRemove(IReadOnlyList<Install> installs, int keepOlder, params string?[] keep)
    {
        var newest = installs.LastOrDefault();
        var older = installs.Where(i => i != newest && !keep.Contains(i.Name)).ToList();
        return older.Take(Math.Max(0, older.Count - Math.Max(0, keepOlder))).ToList();
    }

    public static Version? ZipVersion(string path) =>
        ZipName.Match(Path.GetFileName(path)) is { Success: true } m && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;

    /// <summary>The highest-numbered release zip in a folder, if any.</summary>
    public static (string File, Version Version)? NewestZip(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var best = Directory.GetFiles(folder, "SurveyToRevit-*.zip")
            .Select(f => (File: f, V: ZipVersion(f)))
            .Where(x => x.V != null).OrderByDescending(x => x.V).FirstOrDefault();
        return best.File == null ? null : (best.File, best.V!);
    }

    public static string Short(Version v) => v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
}
