using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurveyToRevit.Shared;

/// <summary>
/// The user's Survey to Revit settings, kept in %AppData%\SurveyToRevit\settings.json.
/// Compiled into both the launcher and the engine, which each read the file when needed;
/// settings one side does not know about are kept as they are when the other saves.
/// </summary>
internal sealed class UserSettings
{
    /// <summary>Where the Updater looks for SurveyToRevit-x.y.z.zip; empty means Downloads.</summary>
    public string? UpdatesFolder { get; set; }
    /// <summary>Show "Update available" on the ribbon when a newer zip is waiting there.</summary>
    public bool CheckAtStartup { get; set; } = true;
    /// <summary>Installed version folder to run instead of the newest (to go back a version); empty means newest.</summary>
    public string? RunVersion { get; set; }
    /// <summary>Older installed versions kept for going back.</summary>
    public int KeepVersions { get; set; } = 3;
    /// <summary>Where analysis reports and build logs are saved; empty means the Windows temp folder.</summary>
    public string? ReportsFolder { get; set; }
    public int SlabMm { get; set; } = 300;
    public int SiteRadiusM { get; set; } = 150;
    /// <summary>The folder the survey picker opened last time.</summary>
    public string? LastSurveyFolder { get; set; }
    /// <summary>The office project template a new survey project starts from (survey_new_project); empty means the Barr Gazetas one.</summary>
    public string? ProjectTemplate { get; set; }
    /// <summary>How the office template names a level's existing-survey plan: this prefix and the level's name (BG: package 01_Existing).</summary>
    public string ExistingViewPrefix { get; set; } = "01_";
    /// <summary>Sheet numbers of the existing drawings: "{prefix}{number}", plans numbered from PlanBase (the Ground Floor;
    /// floors up count on, basements count down: B1 = 199), elevations and sections from theirs.</summary>
    public string ExistingSheetPrefix { get; set; } = "10.";
    public int ExistingPlanBase { get; set; } = 200;
    public int ExistingSectionBase { get; set; } = 250;
    public int ExistingElevationBase { get; set; } = 270;
    /// <summary>The sheet parameter the project browser groups sheets by (BG: View Type), and each kind's group: {0} its first number.</summary>
    public string SheetGroupParameter { get; set; } = "View Type";
    /// <summary>Draw the survey's elevations (their own lines, as detail lines) in the elevation views, on the model.</summary>
    public bool OverlayElevations { get; set; } = true;
    public string ExistingPlanGroup { get; set; } = "{0} Existing Plans";
    public string ExistingSectionGroup { get; set; } = "{0} Existing Sections";
    public string ExistingElevationGroup { get; set; } = "{0} Existing Elevations";

    [JsonIgnore]
    public string Template => string.IsNullOrWhiteSpace(ProjectTemplate) ? DefaultTemplate : ProjectTemplate!;
    public const string DefaultTemplate = @"I:\BG Technical Library\8.0 BG Revit Library\02_Project Template\BGA_Revit Project Template_2025.rte";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }

    [JsonIgnore]
    public string Updates => string.IsNullOrWhiteSpace(UpdatesFolder) ? DefaultDownloads : UpdatesFolder!;
    [JsonIgnore]
    public string Reports => string.IsNullOrWhiteSpace(ReportsFolder) ? DefaultReports : ReportsFolder!;

    public static string DefaultDownloads =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public static string DefaultReports => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SurveyToRevit", "Reports");
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SurveyToRevit", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The saved settings, or the defaults if there are none or the file cannot be read.</summary>
    public static UserSettings Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (File.Exists(path)) return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch { /* a damaged settings file must never stop the add-in: fall back to the defaults */ }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, true);
    }
}
