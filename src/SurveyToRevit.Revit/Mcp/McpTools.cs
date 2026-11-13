using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core.Survey;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Revit;

/// <summary>
/// The engine's tools for an assistant (Claude, through the Barr Gazetas MCP add-in), declared the way
/// <see cref="Tools"/> declares ribbon buttons: plain strings only, since the caller shares no types with
/// the engine. The launcher's McpBridge passes these on; a release that adds a tool here needs no restart.
///
/// List(): one row per tool: name, description, input schema (JSON Schema, as text), flags.
///   Flags (comma separated): "background" = needs no Revit, may run off Revit's thread with a null application;
///   "write" = changes the model; "long" = can take minutes.
/// Call(app, name, argsJson): runs one and returns JSON text. A problem is thrown, with a message meant to be read.
/// Prompts(): name, description, text: ready-made instructions the assistant can load.
/// Keep names stable. Answers are summaries (a few hundred words), never raw geometry.
/// </summary>
public static class McpTools
{
    const string Files =
        "\"folder\":{\"type\":\"string\",\"description\":\"Folder holding the survey DWGs (all .dwg files in it are used)\"}," +
        "\"paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Or: the DWG files themselves\"}";

    static string Schema(string properties, params string[] required) =>
        "{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" + string.Join(",", required.Select(r => "\"" + r + "\"")) + "]}";

    public static string[][] List() =>
    [
        ["survey_status",
            "Where things stand: the Survey to Revit version, the open project, how many elements a build has made on each floor, the review list (open items) and the newest reports. Start here.",
            Schema(""), ""],
        ["survey_analyze",
            "Read survey DWGs without touching Revit: floors with heights, walls by confidence, windows, doors, rooms, drawings skipped and why, and the self-checks. Returns a short summary and saves the full report. Run before survey_build (the reading is kept, so the build does not read the DWGs again).",
            Schema(Files + ",\"fresh\":{\"type\":\"boolean\",\"description\":\"Read the files again even if nothing changed\"}"), "background,long"],
        ["survey_layers",
            "Every CAD layer in the DWGs with how much is on it and the role the layer rules give it (Wall, Door, Window, Other...). A busy layer with role Other is the usual reason a survey reads badly. Fix with survey_layer_rules.",
            Schema(Files + ",\"only_unassigned\":{\"type\":\"boolean\",\"description\":\"Only layers with role Other\"},\"limit\":{\"type\":\"integer\",\"description\":\"Most layers to return (default 60)\"}"), "background,long"],
        ["survey_layer_rules",
            "Read, or with rules + confirm write, the layers.txt next to the DWGs: the office's own layer names and their roles (\"MY_WALLS* = Wall\"). Roles: " + string.Join(", ", Enum.GetNames<LayerRole>()) + ". Ask the user before writing. The existing file is kept as layers.txt.bak. Analyse again afterwards.",
            Schema("\"folder\":{\"type\":\"string\",\"description\":\"Folder holding the survey DWGs\"}," +
                   "\"rules\":{\"type\":\"array\",\"description\":\"The whole new file: every layer name (\\\"*\\\" matches anything) and its role. Omit to read the current file.\",\"items\":{\"type\":\"object\",\"properties\":{\"layer\":{\"type\":\"string\"},\"role\":{\"type\":\"string\"}},\"required\":[\"layer\",\"role\"]}}," +
                   "\"note\":{\"type\":\"string\",\"description\":\"A line of explanation kept in the file\"}," +
                   "\"confirm\":{\"type\":\"boolean\",\"description\":\"true to write the file\"}", "folder"), "background"],
        ["survey_build",
            "Build from Survey with no windows: coordinates, levels, linked plans, walls, windows, doors, floors, ceilings, rooms, stairs, roofs, sections. Replaces what an earlier build made on the floors it builds; nothing else is touched. One undo step. Without confirm it changes nothing and says what it would do. With \"levels\" it rebuilds only those floors (coordinates and other floors stay; stairs, roofs, sections and plan links are left alone unless asked for).",
            Schema(Files + "," +
                   "\"levels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Rebuild only these floors (names or codes from survey_analyze)\"}," +
                   "\"exclude_levels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Floors to leave out altogether\"}," +
                   "\"level_overrides\":{\"type\":\"array\",\"description\":\"Corrections to what the survey reading found\",\"items\":{\"type\":\"object\",\"properties\":{\"level\":{\"type\":\"string\"},\"elevation_m\":{\"type\":\"number\",\"description\":\"Floor level above survey datum, metres\"},\"ceiling_mm\":{\"type\":\"number\"}},\"required\":[\"level\"]}}," +
                   "\"options\":{\"type\":\"object\",\"description\":\"What to build; all true by default except unclear_as_generic_models\",\"properties\":{" +
                       string.Join(",", OptionNames.Select(n => $"\"{n}\":{{\"type\":\"boolean\"}}")) + ",\"slab_thickness_mm\":{\"type\":\"number\"}}}," +
                   "\"confirm\":{\"type\":\"boolean\",\"description\":\"true to build; otherwise a preview\"}"), "write,long"],
        ["survey_overlay",
            "Overlay Survey with no windows: set up the project from the survey and build nothing: shared coordinates, a level per floor, each plan linked into its floor plan, section views, other drawings in drafting views. One undo step. Without confirm it only says what it would do.",
            Schema(Files + ",\"confirm\":{\"type\":\"boolean\",\"description\":\"true to do it; otherwise a preview\"}"), "write,long"],
        ["survey_generated",
            "What the builds made, counted by floor and kind (walls by confidence, doors, windows, rooms...). With a filter, lists matching element ids, e.g. contains \"Low\" for low-confidence walls.",
            Schema("\"level\":{\"type\":\"string\",\"description\":\"Floor name or code\"}," +
                   "\"contains\":{\"type\":\"string\",\"description\":\"Text in the element's note, e.g. Low, Parapet, Unclear, Room (no label)\"}," +
                   "\"category\":{\"type\":\"string\",\"description\":\"Revit category name, e.g. Walls\"}," +
                   "\"limit\":{\"type\":\"integer\",\"description\":\"Most ids to list (default 100)\"}"), ""],
        ["survey_review",
            "The review list of the last build: places the reading was unsure about (rooms with no way in, door symbols with no door, gaps in a column of windows, walls stopping short). Most important first. This is the to-do list: take one, look with survey_review_context, fix, tick with survey_review_tick.",
            Schema("\"level\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"description\":\"" + string.Join(", ", Enum.GetNames<CheckKind>()) + "\"}," +
                   "\"include_done\":{\"type\":\"boolean\"},\"limit\":{\"type\":\"integer\",\"description\":\"Most items (default 40)\"}"), ""],
        ["survey_review_context",
            "Everything in the model around a review item (or any place on a floor): walls with their lines and thickness, doors, windows, rooms, nearest first, with element ids. Also takes Revit there: the floor's plan, zoomed in, generated elements selected.",
            Schema("\"id\":{\"type\":\"integer\",\"description\":\"Review item number\"}," +
                   "\"level\":{\"type\":\"string\",\"description\":\"Or a place: floor name...\"},\"x\":{\"type\":\"number\",\"description\":\"...and model mm\"},\"y\":{\"type\":\"number\"}," +
                   "\"radius_mm\":{\"type\":\"number\",\"description\":\"How far around (default 2000)\"}," +
                   "\"show\":{\"type\":\"boolean\",\"description\":\"Go there in Revit (default true)\"}"), ""],
        ["survey_review_tick",
            "Tick review items as looked at (or untick), with a note of what was found or done. Only the review list file changes.",
            Schema("\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"done\":{\"type\":\"boolean\",\"description\":\"default true\"},\"note\":{\"type\":\"string\"}", "ids"), ""],
        ["survey_score",
            "With a hand-made reference model of the same building linked in: generated walls, windows and doors counted as right, off, missed or invented, whole building and per level. Nothing is changed. Use it before and after fixes to see whether they helped. This compares with another model; to measure the model against the survey DWGs themselves (1:1), use the BG MCP hub's survey_fidelity, which includes this score when a reference model is linked.",
            Schema("\"link\":{\"type\":\"string\",\"description\":\"Name of the linked reference model, when several are linked\"}"), "long"],
        ["survey_wall",
            "Add a wall the reading missed (Wall from Lines without the picking): between two face lines, or along a centre line at a thickness. Model mm. Built as a Concept Wall up to the next level, Existing phase; kept when the floor is rebuilt. One undo step.",
            Schema("\"level\":{\"type\":\"string\"}," +
                   "\"face_a\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"[x1, y1, x2, y2] of one face\"}," +
                   "\"face_b\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"[x1, y1, x2, y2] of the other face\"}," +
                   "\"centre\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"Or: [x1, y1, x2, y2] of the centre line...\"}," +
                   "\"thickness_mm\":{\"type\":\"number\",\"description\":\"...and the thickness\"}", "level"), "write"],
        ["survey_reading",
            "The engine's whole reading of the survey, for other tools to measure a model against (the BG MCP hub's survey_fidelity and survey_autofix use it): per floor, in model mm, the wall face lines and arcs as drawn, window and door lines, the walls read (thickness, confidence), windows and doors with sill and head, every height note (sill, head, door head, ceiling), spot heights, room names, ceilings, stairs and the slab outline; also sections and roofs. Layer roles and label formats are the build's own (built-in rules plus layers.txt). Writes it to a JSON file (schema survey-reading/1) and returns the path with counts per floor; the geometry is never in the answer. Without folder or paths, the survey used last.",
            Schema(Files + ",\"levels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Only these floors (names or codes)\"},\"fresh\":{\"type\":\"boolean\",\"description\":\"Read the files again even if nothing changed\"}"), "background,long"],
        ["survey_layer_roles",
            "The role each named CAD layer has under the layer rules (built-in plus the layers.txt next to the survey): Wall, Window, Door, LevelText, HeightText... For tools that read drawings themselves (elevations), so they use the same layer mapping as the build. Without folder or paths, the survey used last.",
            Schema(Files + ",\"layers\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Layer names\"}", "layers"), "background"],
        ["survey_report",
            "The text of the newest saved report: analysis, build, overlay, score or error. For detail the summaries leave out.",
            Schema("\"kind\":{\"type\":\"string\",\"enum\":[\"analysis\",\"build\",\"overlay\",\"score\",\"error\"]}," +
                   "\"max_chars\":{\"type\":\"integer\",\"description\":\"default 6000\"},\"from\":{\"type\":\"integer\",\"description\":\"Start at this character, to read on\"}", "kind"), "background"],
    ];

    static readonly string[] OptionNames =
        ["link_plans", "walls", "floors", "ceilings", "windows", "doors", "rooms", "stairs", "details", "roofs", "sections", "unclear_as_generic_models"];

    public static string[][] Prompts() =>
    [
        ["survey_to_model",
            "Turn survey DWGs into a checked Revit model: analyse, build, then work through the review list. With the BG MCP hub, use its survey_to_one_to_one instead: the same steps, then measured to 1:1.",
            """
            If the tools survey_fidelity and survey_autofix are also listed (the BG MCP hub), load the prompt
            survey_to_one_to_one and follow it instead: it includes every step below and then takes the model to 1:1.

            You are helping build a Revit concept model from 2D survey DWGs with Survey to Revit. The engine does the
            modelling; you choose, check and fix. Keep answers short. Never place elements one at a time when the
            engine can build them.

            1. survey_status. If no project is open, ask for one. Remind the user to work on a copy.
            2. Ask for the folder of survey DWGs if you do not have it. survey_analyze.
               - Drawings skipped, floors with no height, or few walls on a floor: run survey_layers with
                 only_unassigned. If busy layers have role Other, propose a layers.txt (survey_layer_rules) and ask
                 before writing. Analyse again.
               - Say in a few lines what was found: floors and heights, walls (how many low confidence), doors,
                 windows, checks.
            3. Propose what to build. survey_build without confirm shows the plan; build only after the user says yes
               (confirm: true). Report warnings and anything Revit refused.
            4. If a reference model is linked: survey_score, and keep the numbers.
            5. Work through survey_review, most important first. For each item: survey_review_context, decide, fix
               (survey_wall for a missed wall; the other Revit tools for a parameter, a door, a room), then
               survey_review_tick with a note. If you cannot tell, say so in the note and leave it unticked.
               A floor that needs many fixes of one kind is usually a layer or reading problem: fix the cause and
               rebuild that floor (survey_build with levels) rather than patching by hand.
            6. Score again if you scored before. Finish with: what was built, what you fixed, what is left for a person.

            Units: positions and sizes are model millimetres (Revit internal X and Y). Levels are metres above survey datum.
            Every change is an undo step in Revit; say so when you make one.
            """],
    ];

    public static string Call(UIApplication? app, string name, string argsJson)
    {
        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var a = parsed.RootElement;
        JsonNode result = name switch
        {
            "survey_status" => Status(app),
            "survey_analyze" => Analyze(a),
            "survey_layers" => Layers(a),
            "survey_layer_rules" => LayerRulesTool(a),
            "survey_build" => Build(Doc(app), a),
            "survey_overlay" => Overlay(Doc(app), a),
            "survey_generated" => GeneratedTool(Doc(app), a),
            "survey_review" => ReviewTool(Doc(app), a),
            "survey_review_context" => ReviewContext(app, a),
            "survey_review_tick" => ReviewTick(Doc(app), a),
            "survey_score" => SurveyPipeline.Score(Doc(app), Str(a, "link")),
            "survey_wall" => SurveyPipeline.Wall(Doc(app), Str(a, "level") ?? throw new ArgumentException("\"level\" is needed."),
                Numbers(a, "face_a"), Numbers(a, "face_b"), Numbers(a, "centre"), Num(a, "thickness_mm")),
            "survey_report" => Report(a),
            "survey_reading" => Reading(a),
            "survey_layer_roles" => LayerRoles(a),
            _ => throw new ArgumentException($"Survey to Revit has no tool called {name}."),
        };
        return result.ToJsonString();
    }

    // ---------- arguments ----------

    static Document Doc(UIApplication? app) =>
        app?.ActiveUIDocument?.Document is { IsFamilyDocument: false } doc ? doc : throw new InvalidOperationException("Open a Revit project first.");

    static string? Str(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
    static bool? Flag(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    static double? Num(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static int Int(JsonElement a, string key, int fallback) => Num(a, key) is double d ? (int)d : fallback;
    static double[]? Numbers(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray() : null;
    static List<string> Strings(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0).ToList() : new();

    /// <summary>The drawings named by "folder" and/or "paths" (or, with <paramref name="orLast"/>, the survey folder used last).</summary>
    static List<string> FilesOf(JsonElement a, bool orLast = false)
    {
        var list = Strings(a, "paths");
        var folder = Str(a, "folder");
        if (folder == null && list.Count == 0 && orLast && UserSettings.Load().LastSurveyFolder is { Length: > 0 } last && Directory.Exists(last)) folder = last;
        if (folder != null)
        {
            if (!Directory.Exists(folder)) throw new ArgumentException($"There is no folder {folder}.");
            list.AddRange(Directory.GetFiles(folder, "*.dwg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        list = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) throw new ArgumentException("Say where the survey is: \"folder\" (a folder of DWGs) or \"paths\" (the DWG files).");
        if (list.FirstOrDefault(f => !File.Exists(f)) is string missing) throw new ArgumentException($"There is no file {missing}.");
        Remember(list);
        return list;
    }

    /// <summary>The survey's folder, kept as the one used last (as the ribbon's Build from Survey keeps it), so a later call can leave it out.</summary>
    static void Remember(List<string> files)
    {
        try
        {
            var settings = UserSettings.Load();
            var folder = Path.GetDirectoryName(files[0]);
            if (folder != null && settings.LastSurveyFolder != folder) { settings.LastSurveyFolder = folder; settings.Save(); }
        }
        catch { /* only a convenience */ }
    }

    static List<SurveyLevel> Floors(SurveyModel model, IEnumerable<string> names) => names.Select(n =>
        model.Levels.FirstOrDefault(l => l.Code.Equals(n, StringComparison.OrdinalIgnoreCase) || l.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"The survey has no floor \"{n}\". Floors: {string.Join(", ", model.Levels.Select(l => $"{l.Name} ({l.Code})"))}.")).ToList();

    // ---------- tools ----------

    static JsonObject Status(UIApplication? app)
    {
        var settings = UserSettings.Load();
        var o = new JsonObject
        {
            ["surveyToRevit"] = typeof(McpTools).Assembly.GetName().Version?.ToString(3),
            ["reportsFolder"] = settings.Reports,
            ["defaults"] = new JsonObject { ["slabMm"] = settings.SlabMm, ["lastSurveyFolder"] = settings.LastSurveyFolder },
        };
        var reports = new JsonObject();
        foreach (var kind in new[] { "analysis", "build", "overlay", "score", "error" })
            if (SurveyPipeline.Latest(kind) is string p) reports[kind] = new JsonObject { ["path"] = p, ["saved"] = File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm") };
        o["newestReports"] = reports;

        var doc = app?.ActiveUIDocument?.Document;
        if (doc == null || doc.IsFamilyDocument) { o["project"] = null; o["next"] = "Open a Revit project (a copy), then survey_analyze."; return o; }
        o["project"] = new JsonObject { ["title"] = doc.Title, ["path"] = doc.PathName, ["workshared"] = doc.IsWorkshared };
        var generated = SurveyPipeline.GeneratedSummary(doc);
        o["generated"] = generated;
        try
        {
            var (list, path) = SurveyPipeline.Review(doc);
            o["review"] = new JsonObject { ["open"] = list.Items.Count(i => !i.Done), ["total"] = list.Items.Count, ["built"] = list.Built.ToString("yyyy-MM-dd HH:mm"), ["path"] = path };
        }
        catch { o["review"] = null; }
        o["linkedModels"] = new JsonArray(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Select(l => (JsonNode)(l.GetLinkDocument()?.Title ?? l.Name + " (not loaded)")).ToArray());
        o["next"] = generated["total"]!.GetValue<int>() == 0 ? "Nothing built yet: survey_analyze, then survey_build." : "A build is in the model: survey_review for what to check.";
        return o;
    }

    static JsonObject Analyze(JsonElement a)
    {
        var (dwgs, site) = SurveyPipeline.Split(FilesOf(a));
        if (dwgs.Count == 0) throw new ArgumentException("No survey DWGs among those files.");
        var o = SurveyPipeline.AnalyzeToJson(dwgs, Flag(a, "fresh") == true);
        o["drawings"] = dwgs.Count;
        if (site.Count > 0) o["siteFilesIgnored"] = site.Count;
        return o;
    }

    static JsonObject Layers(JsonElement a)
    {
        var (dwgs, _) = SurveyPipeline.Split(FilesOf(a));
        var problems = new List<string>();
        var rules = SurveyLayers.RulesFor(dwgs, problems);
        var drawings = new List<SurveyDrawing>();
        foreach (var d in dwgs)
            try { drawings.Add(SurveyDrawing.Read(d, rules)); }
            catch (Exception ex) { problems.Add($"{Path.GetFileName(d)} could not be read: {ex.Message}"); }
        var all = SurveyLayers.Of(drawings, rules);
        var shown = Flag(a, "only_unassigned") == true ? all.Where(l => l.Role == nameof(LayerRole.Other)).ToList() : all;
        int limit = Int(a, "limit", 60);
        var byRole = new JsonObject();
        foreach (var g in all.GroupBy(l => l.Role).OrderByDescending(g => g.Sum(l => l.Entities))) byRole[g.Key] = g.Sum(l => l.Entities);
        return new JsonObject
        {
            ["drawings"] = drawings.Count,
            ["layers"] = all.Count,
            ["layerFile"] = rules.OverrideFile,
            ["entitiesByRole"] = byRole,
            ["shown"] = new JsonArray(shown.Take(limit).Select(l => (JsonNode)new JsonObject { ["layer"] = l.Layer, ["role"] = l.Role, ["entities"] = l.Entities, ["drawings"] = l.Drawings }).ToArray()),
            ["notShown"] = Math.Max(0, shown.Count - limit),
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
        };
    }

    static JsonObject LayerRulesTool(JsonElement a)
    {
        var folder = Str(a, "folder") ?? throw new ArgumentException("\"folder\" is needed: the folder holding the survey DWGs.");
        if (!Directory.Exists(folder)) throw new ArgumentException($"There is no folder {folder}.");
        var path = Path.Combine(folder, LayerRules.OverrideFileName);
        var current = File.Exists(path) ? File.ReadAllText(path) : null;
        if (!a.TryGetProperty("rules", out var rulesIn) || rulesIn.ValueKind != JsonValueKind.Array)
            return new JsonObject { ["path"] = path, ["exists"] = current != null, ["text"] = current, ["roles"] = string.Join(", ", Enum.GetNames<LayerRole>()) };

        var rules = rulesIn.EnumerateArray().Select(r => (Str(r, "layer") ?? "", Str(r, "role") ?? "")).ToList();
        if (Flag(a, "confirm") != true)
            return new JsonObject
            {
                ["preview"] = true, ["path"] = path, ["replaces"] = current,
                ["wouldWrite"] = new JsonArray(rules.Select(r => (JsonNode)$"{r.Item1} = {r.Item2}").ToArray()),
                ["next"] = "Ask the user, then call again with confirm: true.",
            };
        var problems = SurveyLayers.Write(folder, rules, Str(a, "note"));
        if (problems.Count > 0) throw new ArgumentException("Nothing was written. " + string.Join(" ", problems));
        SurveyPipeline.Forget();
        return new JsonObject { ["written"] = path, ["rules"] = rules.Count, ["backup"] = current != null ? path + ".bak" : null, ["next"] = "survey_analyze again to see the difference." };
    }

    static JsonObject Build(Document doc, JsonElement a)
    {
        SurveyPipeline.Guard(doc);
        var (dwgs, site) = SurveyPipeline.Split(FilesOf(a));
        if (dwgs.Count == 0) throw new ArgumentException("No survey DWGs among those files.");
        var model = SurveyPipeline.Analyze(dwgs, Flag(a, "fresh") == true);
        var notes = new List<string>();
        if (site.Count > 0) notes.Add($"{site.Count} LiDAR/site file(s) ignored: surroundings are added with the Surroundings button.");

        // floors and corrections, as the build window takes them
        foreach (var l in Floors(model, Strings(a, "exclude_levels"))) l.Include = false;
        if (a.TryGetProperty("level_overrides", out var overrides) && overrides.ValueKind == JsonValueKind.Array)
            foreach (var o in overrides.EnumerateArray())
            {
                var l = Floors(model, [Str(o, "level") ?? throw new ArgumentException("Each level_overrides entry needs \"level\".")])[0];
                if (Num(o, "elevation_m") is double m) l.ElevationMm = Math.Round(m * 1000 / 10) * 10;
                if (Num(o, "ceiling_mm") is double c && c > 0) l.CeilingHeightMm = Math.Round(c / 10) * 10;
            }
        foreach (var l in model.Levels.Where(l => l.Include && double.IsNaN(l.ElevationMm)))
        {
            l.Include = false;
            notes.Add($"{l.Name} has no height in the survey and is left out. Give one with level_overrides (elevation_m) to build it.");
        }
        if (!model.Levels.Any(l => l.Include)) throw new InvalidOperationException("No floor to build: no usable floor plans were found. See survey_analyze and survey_report (analysis).");
        SurveyPipeline.FollowLevels(model);

        var options = new BuildOptions { SlabThicknessMm = UserSettings.Load().SlabMm };
        var only = Floors(model, Strings(a, "levels")).Where(l => l.Include).ToList();
        if (only.Count > 0)
        {
            options.OnlyLevels = only.Select(l => l.Code).ToHashSet();
            // a floor rebuilt on its own: what spans floors or was set up by the first build is left alone unless asked for
            options.LinkPlans = false; options.Stairs = false; options.Roofs = false; options.Sections = false;
        }
        var given = a.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.Object ? opt : default;
        options.LinkPlans = Flag(given, "link_plans") ?? options.LinkPlans;
        options.Walls = Flag(given, "walls") ?? options.Walls;
        options.Floors = Flag(given, "floors") ?? options.Floors;
        options.Ceilings = Flag(given, "ceilings") ?? options.Ceilings;
        options.Windows = options.Walls && (Flag(given, "windows") ?? options.Windows);
        options.Doors = options.Walls && (Flag(given, "doors") ?? options.Doors);
        options.Rooms = options.Walls && (Flag(given, "rooms") ?? options.Rooms);
        options.Stairs = Flag(given, "stairs") ?? options.Stairs;
        options.Details = Flag(given, "details") ?? options.Details;
        options.Roofs = Flag(given, "roofs") ?? options.Roofs;
        options.Sections = Flag(given, "sections") ?? options.Sections;
        options.UnclearAsGenericModels = Flag(given, "unclear_as_generic_models") ?? options.UnclearAsGenericModels;
        options.SlabThicknessMm = Num(given, "slab_thickness_mm") ?? options.SlabThicknessMm;

        var floors = model.Levels.Where(l => l.Include && (options.OnlyLevels == null || options.OnlyLevels.Contains(l.Code))).ToList();
        if (Flag(a, "confirm") != true)
        {
            var codes = floors.Select(l => l.Code).ToHashSet();
            var existing = SurveyPipeline.Generated(doc).Where(g => codes.Contains(g.Code)).Count();
            return new JsonObject
            {
                ["preview"] = true,
                ["project"] = doc.Title,
                ["wouldBuild"] = new JsonArray(floors.Select(l => (JsonNode)new JsonObject
                {
                    ["floor"] = l.Name, ["elevationM"] = Math.Round(l.ElevationMm / 1000, 3), ["wallHeightMm"] = Math.Round(l.WallHeightMm), ["ceilingMm"] = Math.Round(l.CeilingHeightMm),
                    ["walls"] = options.Walls ? l.Walls.Walls.Count : 0, ["windows"] = options.Windows ? l.Windows.Count : 0,
                    ["doors"] = options.Doors ? l.Doors.Count : 0, ["rooms"] = options.Rooms ? l.RoomNames.Count : 0,
                }).ToArray()),
                ["options"] = OptionsJson(options),
                ["replacesGeneratedElements"] = existing,
                ["setsCoordinatesAndLevels"] = true,
                ["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray()),
                ["next"] = "Nothing was changed. Ask the user, then call again with the same arguments and confirm: true. It is one undo step.",
            };
        }
        var result = SurveyPipeline.Build(doc, model, options);
        if (notes.Count > 0) result["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
        result["next"] = "survey_review for the places to check; survey_score if a reference model is linked.";
        return result;
    }

    static JsonObject OptionsJson(BuildOptions o) => new()
    {
        ["link_plans"] = o.LinkPlans, ["walls"] = o.Walls, ["floors"] = o.Floors, ["ceilings"] = o.Ceilings, ["windows"] = o.Windows,
        ["doors"] = o.Doors, ["rooms"] = o.Rooms, ["stairs"] = o.Stairs, ["details"] = o.Details, ["roofs"] = o.Roofs,
        ["sections"] = o.Sections, ["unclear_as_generic_models"] = o.UnclearAsGenericModels, ["slab_thickness_mm"] = o.SlabThicknessMm,
    };

    static JsonObject Overlay(Document doc, JsonElement a)
    {
        SurveyPipeline.Guard(doc);
        var (dwgs, _) = SurveyPipeline.Split(FilesOf(a));
        if (dwgs.Count == 0) throw new ArgumentException("No survey DWGs among those files.");
        var model = SurveyPipeline.Analyze(dwgs, Flag(a, "fresh") == true);
        var floors = model.Levels.Where(l => l.Include && !double.IsNaN(l.ElevationMm)).OrderBy(l => l.ElevationMm).ToList();
        if (floors.Count == 0) throw new InvalidOperationException("No floor plans with a height were found, so there is nothing to set levels from. See survey_report (analysis).");
        var plans = floors.Select(l => l.Plan.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = dwgs.Where(d => !plans.Contains(d)).ToList();
        if (Flag(a, "confirm") != true)
            return new JsonObject
            {
                ["preview"] = true,
                ["project"] = doc.Title,
                ["coordinates"] = $"E {model.Frame.BasePoint.X / 1000:0.000} N {model.Frame.BasePoint.Y / 1000:0.000}, project north {model.Frame.RotationDeg:0.00} deg; Ground Floor = 0",
                ["levels"] = new JsonArray(floors.Select(l => (JsonNode)new JsonObject { ["floor"] = l.Name, ["elevationM"] = Math.Round(l.ElevationMm / 1000, 3), ["drawing"] = Path.GetFileName(l.Plan.Path) }).ToArray()),
                ["sectionViews"] = model.Sections.Count(s => s.Cut != null),
                ["otherDrawingsLinked"] = others.Count,
                ["next"] = "Nothing was changed and nothing will be built. Ask the user, then call again with confirm: true. It is one undo step.",
            };
        return SurveyPipeline.Build(doc, model, BuildOptions.Overlay(others), "overlay");
    }

    static JsonObject GeneratedTool(Document doc, JsonElement a)
    {
        var summary = SurveyPipeline.GeneratedSummary(doc);
        string? level = Str(a, "level"), contains = Str(a, "contains"), category = Str(a, "category");
        if (level == null && contains == null && category == null) return summary;
        int limit = Int(a, "limit", 100);
        var matches = SurveyPipeline.Generated(doc).Where(g =>
            (level == null || g.Code.Equals(level, StringComparison.OrdinalIgnoreCase) || SurveyPipeline.LevelName(doc, g.Element).Equals(level, StringComparison.OrdinalIgnoreCase)) &&
            (contains == null || g.What.Contains(contains, StringComparison.OrdinalIgnoreCase)) &&
            (category == null || (g.Element.Category?.Name ?? "").Equals(category, StringComparison.OrdinalIgnoreCase))).ToList();
        return new JsonObject
        {
            ["matches"] = matches.Count,
            ["elements"] = new JsonArray(matches.Take(limit).Select(g => (JsonNode)new JsonObject
            {
                ["id"] = g.Element.Id.Value, ["category"] = g.Element.Category?.Name, ["level"] = SurveyPipeline.LevelName(doc, g.Element), ["code"] = g.Code, ["note"] = g.What,
            }).ToArray()),
            ["notShown"] = Math.Max(0, matches.Count - limit),
        };
    }

    static JsonObject ReviewTool(Document doc, JsonElement a)
    {
        var (list, path) = SurveyPipeline.Review(doc);
        string? level = Str(a, "level"), kind = Str(a, "kind");
        bool all = Flag(a, "include_done") == true;
        int limit = Int(a, "limit", 40);
        var items = list.Items.Where(i => (all || !i.Done) &&
            (level == null || i.Level.Equals(level, StringComparison.OrdinalIgnoreCase)) &&
            (kind == null || i.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))).OrderBy(i => i.Priority).ThenBy(i => i.Level).ToList();
        var open = new JsonObject();
        foreach (var g in list.Items.Where(i => !i.Done).GroupBy(i => i.What).OrderByDescending(g => g.Count())) open[g.Key] = g.Count();
        return new JsonObject
        {
            ["project"] = list.Project, ["built"] = list.Built.ToString("yyyy-MM-dd HH:mm"),
            ["total"] = list.Items.Count, ["open"] = list.Items.Count(i => !i.Done), ["openByKind"] = open,
            ["items"] = new JsonArray(items.Take(limit).Select(i => (JsonNode)SurveyPipeline.ItemJson(i)).ToArray()),
            ["notShown"] = Math.Max(0, items.Count - limit),
            ["path"] = path,
            ["buildLog"] = SurveyPipeline.LastLog(doc) is JsonNode bl ? new JsonObject
            {
                ["entries"] = bl["entries"]?.DeepClone(), ["byKind"] = bl["byKind"]?.DeepClone(), ["path"] = bl["path"]?.DeepClone(),
                ["worst"] = new JsonArray((bl["items"] as JsonArray ?? new JsonArray()).Take(limit / 2).Select(i => i?.DeepClone()).ToArray()),
                ["note"] = "Every uncertain call of the last build, worst first (severity x doubt): act from the top. The whole list is in the file.",
            } : null,
        };
    }

    static JsonObject ReviewContext(UIApplication? app, JsonElement a)
    {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("Open a Revit project first.");
        Doc(app);
        double radius = Math.Clamp(Num(a, "radius_mm") ?? 2000, 200, 20000);
        bool show = Flag(a, "show") != false;
        if (Num(a, "id") is double id)
        {
            var (list, _) = SurveyPipeline.Review(uidoc.Document);
            var item = list.Items.FirstOrDefault(i => i.Id == (int)id) ?? throw new ArgumentException($"The review list has no item {id:0}. It has {list.Items.Count} (1 to {list.Items.Count}).");
            var around = SurveyPipeline.Around(uidoc, item.Level, item.X, item.Y, radius, show);
            around["item"] = SurveyPipeline.ItemJson(item);
            return around;
        }
        if (Str(a, "level") is string level && Num(a, "x") is double x && Num(a, "y") is double y)
            return SurveyPipeline.Around(uidoc, level, x, y, radius, show);
        throw new ArgumentException("Give a review item's \"id\", or a place: \"level\", \"x\" and \"y\" (model mm).");
    }

    static JsonObject ReviewTick(Document doc, JsonElement a)
    {
        var (list, path) = SurveyPipeline.Review(doc);
        var ids = (Numbers(a, "ids") ?? []).Select(d => (int)d).ToHashSet();
        if (ids.Count == 0) throw new ArgumentException("\"ids\" is needed: the review item numbers.");
        bool done = Flag(a, "done") != false;
        var note = Str(a, "note");
        var hit = list.Items.Where(i => ids.Contains(i.Id)).ToList();
        foreach (var i in hit) { i.Done = done; if (note != null) i.Note = note; }
        if (hit.Count > 0) list.Save(path);
        return new JsonObject
        {
            ["changed"] = hit.Count,
            ["notFound"] = new JsonArray(ids.Except(hit.Select(i => i.Id)).Select(i => (JsonNode)i).ToArray()),
            ["open"] = list.Items.Count(i => !i.Done), ["total"] = list.Items.Count,
        };
    }

    static JsonObject Reading(JsonElement a)
    {
        var (dwgs, _) = SurveyPipeline.Split(FilesOf(a, orLast: true));
        if (dwgs.Count == 0) throw new ArgumentException("No survey DWGs among those files.");
        var model = SurveyPipeline.Analyze(dwgs, Flag(a, "fresh") == true);
        var only = Strings(a, "levels");
        if (only.Count > 0) Floors(model, only); // an unknown floor is an error, with the list
        var reading = SurveyReading.ToJson(model, only, dwgs);
        reading["engine"] = typeof(McpTools).Assembly.GetName().Version?.ToString(3);
        reading["folder"] = Path.GetDirectoryName(dwgs[0]);
        var path = Path.Combine(SurveyPipeline.Reports, "survey-reading.json");
        File.WriteAllText(path, reading.ToJsonString());
        static int Count(JsonNode? n) => n is JsonArray x ? x.Count : 0;
        return new JsonObject
        {
            ["path"] = path,
            ["schema"] = SurveyReading.Schema,
            ["folder"] = reading["folder"]?.DeepClone(),
            ["layerFile"] = model.Rules.OverrideFile,
            ["levels"] = new JsonArray(reading["levels"]!.AsArray().OfType<JsonObject>().Select(l => (JsonNode)new JsonObject
            {
                ["code"] = l["code"]?.DeepClone(), ["name"] = l["name"]?.DeepClone(), ["elevationMm"] = l["elevationMm"]?.DeepClone(),
                ["wallFaces"] = Count(l["wallFaces"]), ["wallFaceArcs"] = Count(l["wallFaceArcs"]), ["walls"] = Count(l["walls"]),
                ["windows"] = Count(l["windows"]), ["doors"] = Count(l["doors"]), ["heightNotes"] = Count(l["heights"]),
                ["spotHeights"] = Count(l["spotHeights"]), ["rooms"] = Count(l["rooms"]), ["stairs"] = Count(l["stairs"]),
            }).ToArray()),
            ["sections"] = Count(reading["sections"]),
            ["roofs"] = Count(reading["roofs"]),
            ["skipped"] = Count(reading["skipped"]),
        };
    }

    static JsonObject LayerRoles(JsonElement a)
    {
        var layers = Strings(a, "layers");
        if (layers.Count == 0) throw new ArgumentException("\"layers\" is needed: the layer names to look up.");
        List<string> files;
        try { files = FilesOf(a, orLast: true); }
        catch (ArgumentException) when (Str(a, "folder") == null && Strings(a, "paths").Count == 0) { files = new(); } // no survey known: the built-in rules
        var problems = new List<string>();
        var rules = SurveyLayers.RulesFor(files, problems);
        return new JsonObject
        {
            ["layerFile"] = rules.OverrideFile,
            ["roles"] = SurveyReading.Roles(rules, layers),
            ["problems"] = problems.Count > 0 ? new JsonArray(problems.Select(p => (JsonNode)p).ToArray()) : null,
        };
    }

    static JsonObject Report(JsonElement a)
    {
        var kind = Str(a, "kind") ?? "build";
        if (kind is not ("analysis" or "build" or "overlay" or "score" or "error")) throw new ArgumentException("kind is one of: analysis, build, overlay, score, error.");
        var path = SurveyPipeline.Latest(kind) ?? throw new InvalidOperationException($"No {kind} report has been saved yet in {SurveyPipeline.Reports}.");
        var text = File.ReadAllText(path);
        int from = Math.Clamp(Int(a, "from", 0), 0, text.Length), max = Math.Clamp(Int(a, "max_chars", 6000), 500, 40000);
        var part = text.Substring(from, Math.Min(max, text.Length - from));
        return new JsonObject
        {
            ["path"] = path, ["saved"] = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"),
            ["chars"] = text.Length, ["from"] = from, ["text"] = part,
            ["more"] = from + part.Length < text.Length ? $"Read on with from: {from + part.Length}." : null,
        };
    }
}
