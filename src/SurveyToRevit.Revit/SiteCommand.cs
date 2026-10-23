using System.Drawing;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;
using SurveyToRevit.Shared;
using Form = System.Windows.Forms.Form;
using ComboBox = System.Windows.Forms.ComboBox;
using TextBox = System.Windows.Forms.TextBox;
using Control = System.Windows.Forms.Control;
using Color = System.Drawing.Color;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SurveyToRevit.Revit;

/// <summary>
/// Surroundings on their own, on a model already built (or any project whose shared coordinates are on the
/// national grid): ground, neighbouring buildings and roads from LiDAR and OpenStreetMap, on their own workset.
/// Run it again with other settings to replace them; the surveyed building is never touched.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SiteCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }

        // where the model sits on the national grid: the project's shared coordinates (set by Build from Survey)
        // (Revit's total transform runs one way or the other depending on version: take the one that sends the origin onto the grid, where eastings and northings are positive)
        var total = doc.ActiveProjectLocation.GetTotalTransform();
        var t = new[] { total, total.Inverse }.OrderByDescending(m => (m.Origin.X > 0 ? 1 : 0) + (m.Origin.Y > 0 ? 1 : 0)).First();
        var frame = new SurveyFrame(new Vec2(t.Origin.X * 304.8, t.Origin.Y * 304.8), Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180 / Math.PI);
        if (Math.Abs(frame.BasePoint.X) < 1000 && Math.Abs(frame.BasePoint.Y) < 1000)
        {
            message = "This project has no position on the national grid yet. Run Build from Survey first (it sets the shared coordinates), " +
                      "or set the shared coordinates to British National Grid.";
            return Result.Failed;
        }
        var outlines = BuildingOutlines(doc);

        using var dialog = new SiteDialog(frame, outlines, doc.IsWorkshared);
        if (dialog.ShowDialog() != DialogResult.OK) return Result.Cancelled;

        if (!doc.IsWorkshared && dialog.TurnOnWorksharing)
        {
            try { doc.EnableWorksharing("Shared Levels and Grids", "Workset1"); }
            catch (Exception ex) { TaskDialog.Show("Surroundings", "Worksharing could not be turned on, so the surroundings go on no workset:\n" + ex.Message); }
        }

        SiteContext site;
        try
        {
            site = SiteContextBuilder.Build(dialog.Files, frame, outlines, dialog.RadiusM, centreM: dialog.Centre, roofStepM: dialog.RoofStepM);
        }
        catch (Exception ex) { message = "The LiDAR could not be read: " + ex.Message; return Result.Failed; }
        site = new SiteContext
        {
            Ground = dialog.Ground ? site.Ground : Array.Empty<(Vec2, double)>(),
            Blocks = dialog.Buildings ? site.Blocks : Array.Empty<ContextBlock>(),
            Roads = dialog.Roads && dialog.Ground ? site.Roads : Array.Empty<RoadArea>(),
            SurveyedRoofTopMm = site.SurveyedRoofTopMm, Notes = site.Notes, Footprint = site.Footprint,
        };

        var builder = new ModelBuilder(doc) { ContextWorksetName = dialog.WorksetName };
        try { builder.RunSite(site); }
        catch (Exception ex) { message = "The surroundings could not be built: " + ex.Message; return Result.Failed; }

        try { var s = UserSettings.Load(); s.SiteRadiusM = (int)dialog.RadiusM; s.Save(); } catch { /* a convenience */ }
        var log = string.Join("\n", builder.Log.Select(l => "- " + l));
        try
        {
            var dir = UserSettings.Load().Reports; Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"survey-surroundings_{DateTime.Now:yyyyMMdd_HHmmss}.txt"), log);
        }
        catch { /* the dialog shows it anyway */ }
        TaskDialog.Show("Surroundings", "Done. Run Surroundings again with other settings to replace them.\n\n" + log);
        return Result.Succeeded;
    }

    /// <summary>The surveyed building's footprint: the outlines of the floor slabs Build from Survey made (model mm).</summary>
    static List<Polygon2> BuildingOutlines(Document doc)
    {
        var result = new List<Polygon2>();
        foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
        {
            var note = floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            if (!note.StartsWith("SurveyToRevit|") || !note.EndsWith("|Floor")) continue;
            if (doc.GetElement(floor.SketchId) is not Sketch sketch) continue;
            foreach (CurveArray loop in sketch.Profile)
            {
                var pts = new List<Vec2>();
                foreach (Curve c in loop)
                    foreach (var p in c.Tessellate().Take(c.Tessellate().Count - 1)) pts.Add(new Vec2(p.X * 304.8, p.Y * 304.8));
                if (pts.Count >= 3) result.Add(new Polygon2(pts));
            }
        }
        return result.OrderByDescending(p => p.Area).ToList();
    }
}

/// <summary>What to build around the site, and from which files.</summary>
public sealed class SiteDialog : Form
{
    readonly SurveyFrame _frame;
    readonly List<Polygon2> _outlines;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly NumericUpDown _radius = new() { Minimum = 50, Maximum = 500, Increment = 50, Width = 70 };
    readonly ComboBox _detail = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly CheckBox _ground = new() { Text = "Ground surface (from the DTM)", Checked = true, AutoSize = true };
    readonly CheckBox _buildings = new() { Text = "Neighbouring buildings (generic models)", Checked = true, AutoSize = true };
    readonly CheckBox _roads = new() { Text = "Roads (cut into the ground surface)", Checked = true, AutoSize = true };
    readonly TextBox _workset = new() { Text = "Context", Width = 160 };
    readonly CheckBox _share = new() { Text = "Turn on worksharing so they get their own workset", AutoSize = true };
    (double E, double N)? _centre;

    public List<string> Files { get; } = new();
    public double RadiusM => (double)_radius.Value;
    public (double E, double N)? Centre => _centre;
    public int RoofStepM => new[] { 1, 2, 4 }[Math.Max(0, _detail.SelectedIndex)];
    public bool Ground => _ground.Checked;
    public bool Buildings => _buildings.Checked;
    public bool Roads => _roads.Checked;
    public string WorksetName => string.IsNullOrWhiteSpace(_workset.Text) ? "Context" : _workset.Text.Trim();
    public bool TurnOnWorksharing => _share.Checked;

    public SiteDialog(SurveyFrame frame, List<Polygon2> outlines, bool workshared)
    {
        _frame = frame; _outlines = outlines;
        Text = "Surroundings";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(680, 560);
        MinimumSize = new Size(600, 480);
        _radius.Value = Math.Clamp(UserSettings.Load().SiteRadiusM, 50, 500);
        _detail.Items.AddRange(new object[] { "Fine (1 m, slower)", "Normal (2 m)", "Rough (4 m)" });
        _detail.SelectedIndex = 1;

        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 64, Padding = new Padding(8, 8, 8, 0),
            Text = $"Site at E {frame.BasePoint.X / 1000:0} N {frame.BasePoint.Y / 1000:0} (from the project's shared coordinates); " +
                   $"{outlines.Count} surveyed floor outlines found and left to the survey. Running this again replaces the surroundings " +
                   "from the last run; anything you modelled yourself is kept.",
        };
        var files = new GroupBox { Text = "LiDAR and map files", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var fileButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 230, FlowDirection = FlowDirection.TopDown };
        var get = new Button { Text = "Get from the Environment Agency…", Width = 220, Height = 30 };
        var add = new Button { Text = "Add files (tif, asc, zip, shp)…", Width = 220, Height = 30 };
        var clear = new Button { Text = "Clear", Width = 220, Height = 30 };
        fileButtons.Controls.AddRange(new Control[] { get, add, clear });
        files.Controls.Add(_list);
        files.Controls.Add(fileButtons);

        var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 150, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        Control Row(params Control[] c) { var r = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty }; r.Controls.AddRange(c); return r; }
        Label L(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        options.Controls.Add(Row(L("Radius (m):"), _radius, L("   Roof detail:"), _detail));
        options.Controls.Add(Row(_ground, _buildings, _roads));
        options.Controls.Add(Row(L("Workset:"), _workset));
        if (!workshared) options.Controls.Add(_share);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 6) };
        var build = new Button { Text = "Build", Width = 100 };
        var cancel = new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel };
        bar.Controls.AddRange(new Control[] { cancel, build });

        Controls.Add(files);
        Controls.Add(options);
        Controls.Add(bar);
        Controls.Add(intro);
        CancelButton = cancel;
        _workset.Enabled = workshared;
        _share.CheckedChanged += (_, _) => _workset.Enabled = workshared || _share.Checked;
        _ground.CheckedChanged += (_, _) => _roads.Enabled = _ground.Checked;

        get.Click += (_, _) =>
        {
            using var map = new LidarMapDialog(_frame, _outlines, RadiusM);
            if (map.ShowDialog(this) != DialogResult.OK) return;
            Files.RemoveAll(f => f.Contains("SurveyToRevit-lidar-download"));
            Files.AddRange(map.Files);
            _centre = map.Centre;
            _radius.Value = (decimal)map.RadiusM;
            Refresh();
        };
        add.Click += (_, _) =>
        {
            using var pick = new OpenFileDialog
            {
                Multiselect = true, Title = "LiDAR tiles, download zips, footprints",
                Filter = "LiDAR and map files|*.tif;*.tiff;*.asc;*.zip;*.shp;*.json|All files|*.*",
            };
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in pick.FileNames.Where(SiteContextBuilder.IsSiteFile)) if (!Files.Contains(f)) Files.Add(f);
            Refresh();
        };
        clear.Click += (_, _) => { Files.Clear(); _centre = null; Refresh(); };
        build.Click += (_, _) =>
        {
            if (Files.Count == 0) { MessageBox.Show(this, "Get the LiDAR from the Environment Agency, or add files, first.", Text); return; }
            DialogResult = DialogResult.OK;
            Close();
        };
        Refresh();
    }

    new void Refresh()
    {
        _list.Items.Clear();
        foreach (var f in Files) _list.Items.Add(Path.GetFileName(f) + "   (" + Path.GetDirectoryName(f) + ")");
        if (Files.Count == 0) _list.Items.Add("No files yet: click Get from the Environment Agency…");
    }
}
