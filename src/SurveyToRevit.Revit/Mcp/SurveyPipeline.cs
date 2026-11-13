using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Revit;

/// <summary>
/// The tools' work with no windows in the way: analyse, build, overlay, score, the review list and the
/// generated elements, each as a plain call that returns a small JSON answer. The ribbon commands keep their
/// own windows; this is what an assistant (through <see cref="McpTools"/>) or a script calls instead.
/// Nothing here shows a dialog. Everything that changes the model is one undo step.
/// </summary>
internal static class SurveyPipeline
{
    static double Ft(double mm) => UnitConversion.MmToFeet(mm);
    static double Mm(double ft) => UnitConversion.FeetToMm(ft);

    // ---------- analysis (no Revit: safe on a background thread) ----------

    static readonly object Gate = new();
    static string? _key;
    static SurveyModel? _model;
    /// <summary>Each cached floor as the analysis left it, so a build's corrections never leak into the next one.</summary>
    static Dictionary<SurveyLevel, (double Elevation, double Ceiling, double WallHeight)> _asRead = new();

    /// <summary>
    /// The analysis of these drawings. The last one is kept (keyed by the files and when they, or their
    /// layers.txt, last changed), so an analyse followed by a build reads the DWGs once.
    /// </summary>
    public static SurveyModel Analyze(IReadOnlyList<string> dwgs, bool fresh = false)
    {
        var key = string.Join("|", dwgs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Select(d => d + "@" + File.GetLastWriteTimeUtc(d).Ticks)
            .Concat(dwgs.SelectMany(d => new[] { Path.Combine(Path.GetDirectoryName(d) ?? "", LayerRules.OverrideFileName), Path.Combine(Path.GetDirectoryName(d) ?? "", SurveyDecisions.FileName) }).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(f => f + "@" + (File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks : 0))));
        lock (Gate)
        {
            if (!fresh && _key == key && _model != null) { Reset(_model); return _model; }
        }
        var model = SurveyAnalyzer.Analyze(dwgs);
        lock (Gate)
        {
            _key = key; _model = model;
            _asRead = model.Levels.ToDictionary(l => l, l => (l.ElevationMm, l.CeilingHeightMm, l.WallHeightMm), ReferenceEqualityComparer.Instance as IEqualityComparer<SurveyLevel>);
        }
        return model;
    }

    static void Reset(SurveyModel model)
    {
        foreach (var l in model.Levels)
        {
            l.Include = true;
            if (_asRead.TryGetValue(l, out var v)) { l.ElevationMm = v.Elevation; l.CeilingHeightMm = v.Ceiling; l.WallHeightMm = v.WallHeight; }
        }
    }

    /// <summary>A build may change the model it was given (walls added for doors, outlines trimmed): never reuse it.</summary>
    public static void Forget() { lock (Gate) { _key = null; _model = null; _asRead = new(); } }

    /// <summary>Wall heights follow the levels being built, as in the build window: each floor's walls run to the next one up.</summary>
    public static void FollowLevels(SurveyModel model)
    {
        var built = model.Levels.Where(l => l.Include).OrderBy(l => l.ElevationMm).ToList();
        for (int i = 0; i < built.Count; i++)
            built[i].WallHeightMm = i + 1 < built.Count ? built[i + 1].ElevationMm - built[i].ElevationMm : SurveyAnalyzer.DefaultTopWallHeightMm;
    }

    public static (List<string> Dwgs, List<string> Site) Split(IEnumerable<string> files)
    {
        var all = files.ToList();
        return (all.Where(f => !SiteContextBuilder.IsSiteFile(f)).ToList(), all.Where(SiteContextBuilder.IsSiteFile).ToList());
    }

    public static JsonObject AnalyzeToJson(IReadOnlyList<string> dwgs, bool fresh)
    {
        var model = Analyze(dwgs, fresh);
        var summary = SurveySummary.From(model);
        var node = JsonNode.Parse(summary.ToJson())!.AsObject();
        node["reportPath"] = Save("analysis", "txt", SurveyReport.Build(model));
        node["summaryPath"] = Save("analysis", "json", summary.ToJson());
        if (DecisionsJson(model) is JsonObject dj) node["decisions"] = dj;
        // item V: where to act, worst first (the MCP side starts from this file)
        try
        {
            var blog = BuildLog.From(model);
            var paths = blog.Save(Reports, "", BuildLog.SurveyFolder(model));
            node["buildLog"] = LogJson(blog, paths, 10);
            node["facadeTops"] = TopsJson(model, FacadeTops.Save(model.FacadeTops, Reports, "", BuildLog.SurveyFolder(model)));
            if (model.SectionIndex.Count > 0) node["sections"] = SectionsJson(model, SectionProfiles.Save(model.SectionIndex, Reports, "", BuildLog.SurveyFolder(model)));
        }
        catch (Exception ex) { node["buildLog"] = new JsonObject { ["error"] = ex.Message }; }
        return node;
    }

    // ---------- files ----------

    public static string Reports
    {
        get
        {
            var dir = UserSettings.Load().Reports;
            try { Directory.CreateDirectory(dir); }
            catch { dir = UserSettings.DefaultReports; Directory.CreateDirectory(dir); } // e.g. a network drive that is not connected
            return dir;
        }
    }

    public static string Save(string kind, string extension, string text)
    {
        var path = Path.Combine(Reports, $"survey-{kind}_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>The newest saved report of a kind (analysis, build, overlay, score, error), or null.</summary>
    public static string? Latest(string kind, string extension = "txt") =>
        Directory.Exists(Reports)
            ? Directory.GetFiles(Reports, $"survey-{kind}_*.{extension}").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

    // ---------- building ----------

    public static Document Guard(Document? doc)
    {
        if (doc == null) throw new InvalidOperationException("Open a Revit project first.");
        if (doc.IsFamilyDocument) throw new InvalidOperationException("This is a family. Open a project first.");
        if (doc.IsReadOnly) throw new InvalidOperationException("This project is read-only.");
        if (doc.IsModifiable) throw new InvalidOperationException("Another command is still changing the model. Finish it in Revit, then try again.");
        return doc;
    }

    /// <summary>
    /// Build from Survey without its windows: the model is built (one undo step), the review list and the
    /// log are saved as the button saves them, and the answer says what was made and what Revit refused.
    /// </summary>
    public static JsonObject Build(Document doc, SurveyModel model, BuildOptions options, string kind = "build")
    {
        Guard(doc);
        var builder = new ModelBuilder(doc, model);
        try { builder.Run(options); }
        catch (Exception ex)
        {
            var errorPath = Save("error", "txt", ex + "\n\nLOG\n" + string.Join("\n", builder.Log));
            throw new InvalidOperationException($"{ex.Message} Nothing was changed. Details: {errorPath}");
        }
        finally { Forget(); }

        var floors = model.Levels.Where(l => l.Include && !double.IsNaN(l.ElevationMm) && (options.OnlyLevels == null || options.OnlyLevels.Contains(l.Code))).ToList();
        var result = new JsonObject
        {
            ["done"] = true,
            ["project"] = doc.Title,
            ["floors"] = new JsonArray(floors.Select(l => (JsonNode)l.Name).ToArray()),
        };

        if (!options.OverlayOnly)
        {
            var fresh = ReviewList.From(doc.Title, model.Checks, model.Levels);
            var review = fresh;
            var reviewPath = ReviewPath(doc);
            if (options.OnlyLevels != null && ReviewList.Load(reviewPath) is { } old)
            {
                old.ReplaceLevels(fresh, floors.Select(l => l.Name)); // the other floors keep their ticks and notes
                review = old;
            }
            try
            {
                var blog = BuildLog.From(model, doc.Title);
                blog.AddBuildLines(builder.Log);
                var problems = new List<string>();
                var built = BuiltModelChecks.Run(doc, builder.SurveyPhase, problems);
                foreach (var e in BuiltChecks.AsLog(built)) blog.Add(e);
                blog.Sort();
                result["builtChecks"] = BuiltJson(built, problems);
                var paths = blog.Save(Reports, doc.Title, BuildLog.SurveyFolder(model));
                result["buildLog"] = LogJson(blog, paths, 10);
                result["facadeTops"] = TopsJson(model, FacadeTops.Save(model.FacadeTops, Reports, doc.Title, BuildLog.SurveyFolder(model)));
                if (model.SectionIndex.Count > 0) result["sections"] = SectionsJson(model, SectionProfiles.Save(model.SectionIndex, Reports, doc.Title, BuildLog.SurveyFolder(model)));
                if (options.OnlyLevels == null) review.AddLog(blog);
            }
            catch (Exception ex) { result["buildLog"] = new JsonObject { ["error"] = ex.Message }; }
            try { review.Save(reviewPath); }
            catch { reviewPath = ReviewList.PathFor(UserSettings.DefaultReports, doc.Title); review.Save(reviewPath); }
            result["review"] = new JsonObject { ["open"] = review.Items.Count(i => !i.Done), ["total"] = review.Items.Count, ["path"] = reviewPath };
        }

        if (DecisionsJson(model) is JsonObject dj) result["decisions"] = dj;
        var log = builder.Log;
        result["warnings"] = Lines(log.Where(l => l.StartsWith("WARNING")), 30);
        result["revitRefused"] = Lines(log.Where(l => l.StartsWith("REVIT REFUSED")), 30);
        result["log"] = Lines(log.Where(l => !l.StartsWith("WARNING") && !l.StartsWith("REVIT REFUSED") &&
            (kind != "build" || model.Levels.Any(x => l.StartsWith(x.Name + ":")) || l.StartsWith("Surroundings: ") || l.StartsWith("Project 0") || l.StartsWith("Shared"))), 40);
        if (!options.OverlayOnly) result["generated"] = GeneratedSummary(doc);
        result["logPath"] = Save(kind, "txt", (kind == "build" ? SurveyReport.Build(model) + "\nWHAT WAS DONE IN REVIT\n" : kind.ToUpperInvariant() + "\n") + string.Join("\n", log.Select(l => "  - " + l)));
        result["undo"] = "One undo step in Revit: 'Build from survey'.";
        Save(kind, "json", result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
        return result;
    }

    /// <summary>What survey-decisions.json did on this reading (item T), or null without the file.</summary>
    public static JsonObject? DecisionsJson(SurveyModel model) => model.DecisionsFile == null ? null : new JsonObject
    {
        ["file"] = model.DecisionsFile.Path,
        ["applied"] = model.Decisions.Count(o => o.State == "applied"),
        ["stale"] = model.Decisions.Count(o => o.State == "stale"),
        ["contradicted"] = model.Decisions.Count(o => o.State == "contradicted"),
        ["waiting"] = model.Decisions.Count(o => o.State == "waiting"),
        ["each"] = new JsonArray(model.Decisions.Take(60).Select(o => (JsonNode)new JsonObject { ["kind"] = o.Kind, ["what"] = o.What, ["state"] = o.State, ["detail"] = o.Detail }).ToArray()),
    };

    /// <summary>The sections in short (item H): fit, placement, each end's class and offset, where section-index.json went.</summary>
    public static JsonObject SectionsJson(SurveyModel model, IReadOnlyList<string> paths) => new()
    {
        ["each"] = new JsonArray(model.SectionIndex.Select(x => (JsonNode)new JsonObject
        {
            ["name"] = x.Name, ["file"] = x.File, ["note"] = x.Note,
            ["fit"] = x.Fit is { } f ? new JsonObject { ["a"] = f.A, ["b"] = f.B, ["residualMm"] = f.ResidualMm, ["used"] = f.Used.Count, ["rejected"] = f.Rejected.Count } : null,
            ["sure"] = x.Registration?.Sure, ["mirrored"] = x.Registration?.Mirrored,
            ["ends"] = new JsonArray(x.Ends.Select(e => (JsonNode)new JsonObject { ["end"] = e.End, ["class"] = e.Class, ["why"] = e.Why, ["offsetMm"] = e.OffsetMm, ["bands"] = e.Bands.Count }).ToArray()),
        }).ToArray()),
        ["paths"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray()),
    };

    /// <summary>The facade tops in short (item C): counts by class, the stretches, where roof-edges.json went.</summary>
    public static JsonObject TopsJson(SurveyModel model, IReadOnlyList<string> paths) => new()
    {
        ["byClass"] = new JsonObject(model.FacadeTops.GroupBy(t => t.Class).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
        ["stretches"] = new JsonArray(model.FacadeTops.Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id, ["class"] = t.Class, ["confidence"] = t.Confidence, ["evidence"] = t.Evidence, ["settle"] = t.Settle,
            ["line"] = new JsonArray(t.Line.Select(v => (JsonNode)v).ToArray()), ["skinLinesLeftOut"] = t.SkinLinesLeftOut.Count,
        }).ToArray()),
        ["paths"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray()),
    };

    /// <summary>The build log's counts, where it was saved and its worst entries.</summary>
    static JsonObject BuiltJson(List<BuiltFinding> found, List<string> problems) => new()
    {
        ["count"] = found.Count,
        ["byKind"] = new JsonObject(found.GroupBy(f => f.Kind).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
        ["worst"] = new JsonArray(found.OrderByDescending(f => f.Severity).Take(15).Select(f => (JsonNode)new JsonObject
        {
            ["kind"] = f.Kind, ["level"] = f.Level, ["ids"] = new JsonArray(f.Ids.Take(20).Select(i => (JsonNode)i).ToArray()),
            ["message"] = f.Message, ["fix"] = f.Fix,
        }).ToArray()),
        ["skipped"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
        ["note"] = "read-only checks of what was built; each is also in build-log.json and on the review list",
    };

    public static JsonObject LogJson(BuildLog log, IReadOnlyList<string> paths, int top) => new()
    {
        ["entries"] = log.Items.Count,
        ["byKind"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(log.ByKind, new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() })),
        ["paths"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray()),
        ["worst"] = new JsonArray(JsonNode.Parse(log.ToJson())!["items"]!.AsArray().Take(top).Select(i => i!.DeepClone()).ToArray()),
    };

    /// <summary>The project's last saved build log (reports folder), or null.</summary>
    public static JsonNode? LastLog(Document doc)
    {
        string safe = Regex.Replace(Path.GetFileNameWithoutExtension(doc.Title), @"[^\w\- ]", "_");
        foreach (var root in new[] { UserSettings.Load().Reports, UserSettings.DefaultReports })
        {
            var p = Path.Combine(root, safe, "build-log.json");
            if (File.Exists(p)) try { var n = JsonNode.Parse(File.ReadAllText(p))!; n["path"] = p; return n; } catch { }
        }
        return null;
    }

    static JsonArray Lines(IEnumerable<string> lines, int max)
    {
        var list = lines.ToList();
        var array = new JsonArray(list.Take(max).Select(l => (JsonNode)l).ToArray());
        if (list.Count > max) array.Add($"... and {list.Count - max} more (see the log file)");
        return array;
    }

    // ---------- generated elements ----------

    /// <summary>Every element a build made: the level code and the note from its Comments ("SurveyToRevit|code|note").</summary>
    public static IEnumerable<(Element Element, string Code, string What)> Generated(Document doc)
    {
        var rule = ParameterFilterRuleFactory.CreateBeginsWithRule(new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), ModelBuilder.Tag + "|");
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementParameterFilter(rule)))
        {
            var parts = (e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").Split('|', 3);
            if (parts.Length == 3) yield return (e, parts[1], parts[2]);
        }
    }

    /// <summary>The note without its sizes, so like things count together: "Ceiling 2.52 m (surveyed...)" and "Unclear: ..." </summary>
    static string Kind(Element e, string what)
    {
        var w = what.StartsWith("Unclear:") ? "Unclear" : Regex.Replace(what, @"\s*\d[\d.,x×]*\s*(mm|m)?\b", "").Trim();
        return $"{e.Category?.Name ?? "Other"}: {(w.Length == 0 ? what : w)}";
    }

    public static string LevelName(Document doc, Element e) =>
        (doc.GetElement(e.LevelId) as Level)?.Name ?? (e is Wall w ? (doc.GetElement(w.LevelId) as Level)?.Name : null) ?? "";

    public static JsonObject GeneratedSummary(Document doc)
    {
        var all = Generated(doc).ToList();
        var floors = new JsonArray();
        foreach (var g in all.GroupBy(x => x.Code).OrderBy(g => g.Key))
        {
            var counts = new JsonObject();
            foreach (var k in g.GroupBy(x => Kind(x.Element, x.What)).OrderByDescending(k => k.Count())) counts[k.Key] = k.Count();
            floors.Add(new JsonObject
            {
                ["code"] = g.Key,
                ["level"] = g.Select(x => LevelName(doc, x.Element)).FirstOrDefault(n => n.Length > 0) ?? "",
                ["elements"] = g.Count(),
                ["counts"] = counts,
            });
        }
        return new JsonObject { ["total"] = all.Count, ["floors"] = floors };
    }

    // ---------- review list ----------

    public static string ReviewPath(Document doc) => ReviewList.PathFor(UserSettings.Load().Reports, doc.Title);

    public static (ReviewList List, string Path) Review(Document doc)
    {
        foreach (var path in new[] { ReviewPath(doc), ReviewList.PathFor(UserSettings.DefaultReports, doc.Title) })
            if (ReviewList.Load(path) is { } list) return (list, path);
        throw new InvalidOperationException("There is no review list for this project yet. A build saves one (survey_build, or Build from Survey on the ribbon).");
    }

    public static JsonObject ItemJson(ReviewItem i)
    {
        var o = new JsonObject
        {
            ["id"] = i.Id, ["level"] = i.Level, ["kind"] = i.Kind, ["what"] = i.What, ["message"] = i.Message,
            ["x"] = i.X, ["y"] = i.Y, ["priority"] = i.Priority, ["done"] = i.Done,
        };
        if (!string.IsNullOrEmpty(i.Note)) o["note"] = i.Note;
        return o;
    }

    /// <summary>
    /// What is in the model around a place on a floor (model mm): walls with their lines and thickness, doors,
    /// windows, rooms and the rest, nearest first. With <paramref name="show"/>, Revit goes there too: the
    /// floor's plan, zoomed in, the generated elements selected (as the Review List window does).
    /// </summary>
    public static JsonObject Around(UIDocument uidoc, string levelName, double xMm, double yMm, double radiusMm, bool show, int max = 60)
    {
        var doc = uidoc.Document;
        var level = FindLevel(doc, levelName);
        double x = Ft(xMm), y = Ft(yMm), z = level.Elevation, r = Ft(radiusMm);
        var box = new BoundingBoxIntersectsFilter(new Outline(new XYZ(x - r, y - r, z - Ft(500)), new XYZ(x + r, y + r, z + Ft(2500))));
        var found = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(box)
            .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model
                        && e is not (Floor or Ceiling or RoofBase or Toposolid or ImportInstance or RevitLinkInstance or Level))
            .Select(e => (Element: e, Distance: Distance(e, x, y)))
            .OrderBy(t => t.Distance).ToList();

        var elements = new JsonArray();
        foreach (var (e, d) in found.Take(max))
        {
            var o = new JsonObject { ["id"] = e.Id.Value, ["category"] = e.Category!.Name };
            string name; try { name = e.Name; } catch { name = ""; }
            if (name.Length > 0) o["name"] = name;
            var comment = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            if (comment?.StartsWith(ModelBuilder.Tag + "|") == true) o["generated"] = comment[(ModelBuilder.Tag.Length + 1)..];
            o["distanceMm"] = Math.Round(Mm(d));
            switch (e)
            {
                case Wall w when w.Location is LocationCurve { Curve: var c } && c.IsBound:
                    XYZ a = c.GetEndPoint(0), b = c.GetEndPoint(1);
                    o["line"] = new JsonArray(Math.Round(Mm(a.X)), Math.Round(Mm(a.Y)), Math.Round(Mm(b.X)), Math.Round(Mm(b.Y)));
                    o["thicknessMm"] = Math.Round(Mm(w.Width));
                    break;
                case SpatialElement room:
                    o["room"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                    o["number"] = room.Number;
                    o["areaM2"] = Math.Round(room.Area * 0.09290304, 1);
                    if (room.Location is LocationPoint rp) o["at"] = new JsonArray(Math.Round(Mm(rp.Point.X)), Math.Round(Mm(rp.Point.Y)));
                    break;
                default:
                    if (e.Location is LocationPoint lp) o["at"] = new JsonArray(Math.Round(Mm(lp.Point.X)), Math.Round(Mm(lp.Point.Y)));
                    break;
            }
            elements.Add(o);
        }

        var result = new JsonObject
        {
            ["level"] = level.Name, ["x"] = xMm, ["y"] = yMm, ["radiusMm"] = radiusMm,
            ["found"] = found.Count, ["elements"] = elements,
            ["units"] = "x, y, lines and sizes are model mm (Revit internal X and Y).",
        };
        if (show) result["shown"] = Show(uidoc, level, x, y, found.Select(t => t.Element)) ?? "ok";
        return result;
    }

    static double Distance(Element e, double x, double y)
    {
        var p = new XYZ(x, y, 0);
        XYZ Flat(XYZ q) => new(q.X, q.Y, 0);
        try
        {
            if (e.Location is LocationPoint lp) return Flat(lp.Point).DistanceTo(p);
            if (e.Location is LocationCurve { Curve: var c } && c.IsBound)
            {
                XYZ a = Flat(c.GetEndPoint(0)), b = Flat(c.GetEndPoint(1)), d = b - a;
                double len2 = d.DotProduct(d), t = len2 < 1e-12 ? 0 : Math.Clamp((p - a).DotProduct(d) / len2, 0, 1);
                return (a + d * t).DistanceTo(p);
            }
            if (e.get_BoundingBox(null) is { } bb) return Flat((bb.Min + bb.Max) / 2).DistanceTo(p);
        }
        catch { /* an element with no usable place sorts last */ }
        return double.MaxValue / 4;
    }

    /// <summary>Goes to a place: its floor's plan, zoomed on it, our elements near it selected. Returns a reason if it could not.</summary>
    static string? Show(UIDocument uidoc, Level level, double x, double y, IEnumerable<Element> near)
    {
        try
        {
            var doc = uidoc.Document;
            var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id)
                .OrderBy(v => v.Name == level.Name ? 0 : 1).FirstOrDefault();
            if (view == null) return $"There is no floor plan of {level.Name}.";
            if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
            double zoom = Ft(4000), z = level.Elevation;
            uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id)?.ZoomAndCenterRectangle(new XYZ(x - zoom, y - zoom, z), new XYZ(x + zoom, y + zoom, z));
            uidoc.Selection.SetElementIds(near
                .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(ModelBuilder.Tag + "|") == true)
                .Select(e => e.Id).ToList());
            return null;
        }
        catch (Exception ex) { return "Could not go there: " + ex.Message; }
    }

    public static Level FindLevel(Document doc, string name)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        return levels.FirstOrDefault(l => l.Name == name)
            ?? levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"There is no level called \"{name}\". Levels: {string.Join(", ", levels.OrderBy(l => l.Elevation).Select(l => l.Name))}.");
    }

    // ---------- score ----------

    /// <summary>The Score button's comparison with a linked reference model, as numbers. Nothing is changed.</summary>
    public static JsonObject Score(Document doc, string? linkName)
    {
        if (doc == null || doc.IsFamilyDocument) throw new InvalidOperationException("Open a project first.");
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() != null).ToList();
        if (links.Count == 0)
            throw new InvalidOperationException("No reference model is linked. Link the hand-made model of the same building (Insert > Link Revit) and make sure it is loaded.");
        RevitLinkInstance link;
        if (string.IsNullOrWhiteSpace(linkName))
            link = links.Count == 1 ? links[0]
                : throw new InvalidOperationException("Several models are linked. Say which is the reference with \"link\": " + string.Join(", ", links.Select(l => l.GetLinkDocument().Title)));
        else
            link = links.FirstOrDefault(l => l.GetLinkDocument().Title.Contains(linkName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No linked model is called \"{linkName}\". Linked: " + string.Join(", ", links.Select(l => l.GetLinkDocument().Title)));

        var ourLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var transform = link.GetTotalTransform();
        var refDoc = link.GetLinkDocument();
        string? OurLevel(double elevationFt) =>
            ourLevels.OrderBy(l => Math.Abs(l.Elevation - elevationFt)).FirstOrDefault(l => Math.Abs(l.Elevation - elevationFt) <= Ft(300))?.Name;

        var notes = new List<string>();
        var ours = ScoreCommand.Collect(doc, Transform.Identity, ScoreCommand.Ours, l => l.Name, notes, "this model");
        var reference = ScoreCommand.Collect(refDoc, transform, _ => true, l => OurLevel(transform.OfPoint(new XYZ(0, 0, l.Elevation)).Z), notes, refDoc.Title);
        if (ours.Walls.Count == 0) throw new InvalidOperationException("No walls made by Build from Survey were found in this model.");

        var score = ScoreCard.Score(ours.Walls, reference.Walls, ours.Windows, reference.Windows, ours.Doors, reference.Doors);
        score.Notes.AddRange(notes);

        static JsonNode? Pct(double v) => double.IsNaN(v) ? null : Math.Round(v * 100);
        static JsonObject Tally(ScoreTally t) => new()
        {
            ["right"] = t.Right, ["off"] = t.Off, ["missed"] = t.Missed, ["invented"] = t.Invented,
            ["foundPct"] = Pct(t.Recall), ["realPct"] = Pct(t.Precision),
        };
        var (w, wi, d) = score.Total();
        var levels = new JsonObject();
        foreach (var (level, (lw, lwi, ld)) in score.Levels)
            levels[level] = new JsonObject { ["walls"] = Tally(lw), ["windows"] = Tally(lwi), ["doors"] = Tally(ld) };
        var text = $"SCORE AGAINST {refDoc.Title}\n{DateTime.Now:d MMM yyyy HH:mm}\n" +
                   "right = in place at the right size; off = found, but out of place or the wrong size; missed = in the reference only; invented = in ours only.\n\n" + score.Text();
        var result = new JsonObject
        {
            ["reference"] = refDoc.Title,
            ["meaning"] = "right = in place at the right size; off = found but out of place or the wrong size; missed = in the reference only; invented = in ours only. foundPct = share of the reference found; realPct = share of ours that is in the reference.",
            ["walls"] = Tally(w), ["windows"] = Tally(wi), ["doors"] = Tally(d),
            ["wallLengthFoundM"] = Math.Round(score.FoundWallM), ["wallLengthReferenceM"] = Math.Round(score.RefWallM), ["wallLengthInventedM"] = Math.Round(score.InventedWallM),
            ["levels"] = levels,
            ["notes"] = new JsonArray(score.Notes.Select(n => (JsonNode)n).ToArray()),
            ["reportPath"] = Save("score", "txt", text),
        };
        Save("score", "json", result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
        return result;
    }

    // ---------- a wall the reading missed ----------

    /// <summary>
    /// Wall from Lines without the picking: the wall between two face lines (model mm, x1 y1 x2 y2), or along a
    /// centre line at a thickness. Built as the button builds it (Concept Wall type, to the next level, Existing
    /// phase, kept when the floor is rebuilt).
    /// </summary>
    public static JsonObject Wall(Document doc, string levelName, double[]? faceA, double[]? faceB, double[]? centre, double? thicknessMm)
    {
        Guard(doc);
        var level = FindLevel(doc, levelName);
        double z = level.Elevation;
        Line Make(double x1, double y1, double x2, double y2)
        {
            if (Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) < 100) throw new ArgumentException("A wall line must be at least 100 mm long.");
            return Line.CreateBound(new XYZ(Ft(x1), Ft(y1), z), new XYZ(Ft(x2), Ft(y2), z));
        }
        Line a, b;
        if (faceA is { Length: 4 } && faceB is { Length: 4 })
        {
            a = Make(faceA[0], faceA[1], faceA[2], faceA[3]);
            b = Make(faceB[0], faceB[1], faceB[2], faceB[3]);
        }
        else if (centre is { Length: 4 } && thicknessMm is double t && t > 0)
        {
            double dx = centre[2] - centre[0], dy = centre[3] - centre[1], len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 100) throw new ArgumentException("A wall line must be at least 100 mm long.");
            double nx = -dy / len * t / 2, ny = dx / len * t / 2;
            a = Make(centre[0] + nx, centre[1] + ny, centre[2] + nx, centre[3] + ny);
            b = Make(centre[0] - nx, centre[1] - ny, centre[2] - nx, centre[3] - ny);
        }
        else throw new ArgumentException("Give the wall's two faces (\"face_a\" and \"face_b\", each [x1, y1, x2, y2] in model mm), or its \"centre\" line and \"thickness_mm\".");

        List<ElementId> Picked() => new FilteredElementCollector(doc).OfClass(typeof(Wall))
            .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(WallFromLinesCommand.PickedTag) == true)
            .Select(e => e.Id).ToList();
        var before = Picked().ToHashSet();
        if (WallFromLinesCommand.Build(doc, level, a, b) is string problem) throw new InvalidOperationException(problem);
        var made = Picked().FirstOrDefault(id => !before.Contains(id));
        var wall = made == null ? null : doc.GetElement(made) as Wall;
        return new JsonObject
        {
            ["done"] = wall != null,
            ["wallId"] = wall?.Id.Value,
            ["level"] = level.Name,
            ["type"] = wall?.WallType.Name,
            ["thicknessMm"] = wall == null ? null : Math.Round(Mm(wall.Width)),
            ["undo"] = "One undo step in Revit: 'Wall from survey lines'.",
        };
    }
}
