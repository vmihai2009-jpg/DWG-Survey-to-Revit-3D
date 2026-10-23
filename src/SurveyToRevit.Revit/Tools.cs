namespace SurveyToRevit.Revit;

/// <summary>
/// The engine's own ribbon buttons. The launcher (1.0.3 and later) reads this list at start-up and after each
/// install, so a tool added here appears on the ribbon without restarting Revit. Plain strings only, since the
/// launcher shares no types with the engine: id, text ("\n" breaks the line), tooltip, command class, and the
/// 16 px and 32 px icons (resource names in this assembly). Keep ids stable: a tool keeps its place by its id.
/// Build from Survey and Wall from Lines are fixed buttons in the launcher and stay out of this list.
/// </summary>
public static class Tools
{
    public static string[][] Buttons() =>
    [
        ["review", "Review\\nList",
            "The places the last Build from Survey was unsure about (rooms with no way in, door symbols with no door, " +
            "gaps in a column of windows, walls stopping short...). Double-click one to go to it on its floor plan; " +
            "tick it off once looked at.",
            typeof(ReviewListCommand).FullName!, "Ribbon.icon16.png", "Ribbon.icon32.png"],
        ["score", "Score",
            "With a reference model of the same building linked in (modelled by hand): counts the generated walls, " +
            "windows and doors that are right, off, missed or invented, level by level. Nothing is changed.",
            typeof(ScoreCommand).FullName!, "Ribbon.icon16.png", "Ribbon.icon32.png"],
        ["site", "Surroundings",
            "Ground, neighbouring buildings and roads around the site from the Environment Agency LiDAR (picked on a map or " +
            "from files) and OpenStreetMap, on their own workset. Run it again with other settings to replace them; the " +
            "surveyed building is never touched.",
            typeof(SiteCommand).FullName!, "Ribbon.icon16.png", "Ribbon.icon32.png"],
        ["testfit", "Office\\nTest-Fit",
            "Open the floor plan of a floor, then click: the workplace test-fit opens on that floor's real outline, " +
            "walls, columns and rooms. Set the density, lay out workspace, meeting rooms and breakout, and read " +
            "headcount and NIA. Nothing in the model is changed.",
            typeof(TestFitCommand).FullName!, "Ribbon.testfit16.png", "Ribbon.testfit32.png"],
    ];
}
