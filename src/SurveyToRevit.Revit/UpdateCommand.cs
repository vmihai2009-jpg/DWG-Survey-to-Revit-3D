using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using DialogResult = System.Windows.Forms.DialogResult;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SurveyToRevit.Revit;

/// <summary>
/// Installs a newer release from its zip, from inside Revit: the newest SurveyToRevit-x.y.z.zip in
/// Downloads (or one picked by hand) is unpacked and its installer run. The installer puts each
/// version in its own folder, so the running copy is untouched; the new one loads when Revit
/// restarts. Current user only, no admin rights, nothing uninstalled first.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class UpdateCommand : IExternalCommand
{
    static readonly Regex ZipName = new(@"SurveyToRevit-(\d+(?:\.\d+){1,3})\.zip$", RegexOptions.IgnoreCase);

    public static Version Installed => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var current = Installed;
        string? zip = null;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var newest = Directory.Exists(downloads)
            ? Directory.GetFiles(downloads, "SurveyToRevit-*.zip")
                .Select(f => (File: f, V: ZipName.Match(f) is { Success: true } m && Version.TryParse(m.Groups[1].Value, out var v) ? v : null))
                .Where(x => x.V != null).OrderByDescending(x => x.V).FirstOrDefault()
            : default;
        if (newest.File != null && newest.V! > current)
        {
            var ask = TaskDialog.Show("Survey to Revit - update",
                $"Installed: v{Short(current)}\nFound in Downloads: v{Short(newest.V!)}\n\nInstall it?",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);
            if (ask == TaskDialogResult.Yes) zip = newest.File;
        }
        if (zip == null)
        {
            using var pick = new OpenFileDialog
            {
                Title = $"Pick the Survey to Revit release zip (installed: v{Short(current)})",
                Filter = "Survey to Revit release (SurveyToRevit-*.zip)|SurveyToRevit-*.zip|Zip files (*.zip)|*.zip",
                InitialDirectory = Directory.Exists(downloads) ? downloads : "",
            };
            if (pick.ShowDialog() != DialogResult.OK) return Result.Cancelled;
            zip = pick.FileName;
        }

        var temp = Path.Combine(Path.GetTempPath(), "SurveyToRevit-update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            ZipFile.ExtractToDirectory(zip, temp);
            var script = Directory.GetFiles(temp, "install.ps1", SearchOption.AllDirectories).FirstOrDefault();
            if (script == null) { message = "That zip is not a Survey to Revit release (no install.ps1 inside)."; return Result.Failed; }
            var run = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(run)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) { message = "The update did not install:\n" + output.Trim(); return Result.Failed; }
        }
        catch (Exception ex) { message = "The update did not install: " + ex.Message; return Result.Failed; }
        finally { try { Directory.Delete(temp, true); } catch { /* temp folder, Windows clears it */ } }

        var name = ZipName.Match(zip);
        TaskDialog.Show("Survey to Revit - update",
            $"Installed {(name.Success ? "v" + name.Groups[1].Value : Path.GetFileName(zip))}. Close and reopen Revit to use it.");
        return Result.Succeeded;
    }

    static string Short(Version v) => v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
}
