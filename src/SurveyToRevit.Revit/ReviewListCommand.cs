using System.Drawing;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Survey;
using SurveyToRevit.Shared;
using Color = System.Drawing.Color;
using Form = System.Windows.Forms.Form;
using View = Autodesk.Revit.DB.View;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using Control = System.Windows.Forms.Control;

namespace SurveyToRevit.Revit;

/// <summary>
/// The places Build from Survey was unsure about (rooms with no way in, door symbols with no door, gaps in
/// a column of windows, walls stopping short...), in a window that stays open beside the model. Pick one
/// to go to its floor plan, zoomed on the place, with the generated elements there selected; tick it off
/// when it has been looked at. The ticks are saved, so the list can be worked through over several sittings.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ReviewListCommand : IExternalCommand
{
    static ReviewWindow? _open;

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc == null || doc.IsFamilyDocument) { message = "Open a project first."; return Result.Failed; }
        var path = ReviewList.PathFor(UserSettings.Load().Reports, doc.Title);
        var list = ReviewList.Load(path);
        if (list == null)
        {
            TaskDialog.Show("Review list", "There is no review list for this project yet. Run Build from Survey: it saves one with each build.\n\n" + path);
            return Result.Cancelled;
        }
        Open(list, path);
        return Result.Succeeded;
    }

    /// <summary>Shows the list (replacing one already open). Call from a command or another API context.</summary>
    public static void Open(ReviewList list, string path)
    {
        _open?.Close();
        var go = new GoTo();
        _open = new ReviewWindow(list, path, go, ExternalEvent.Create(go));
        _open.FormClosed += (_, _) => _open = null;
        _open.Show(new RevitWindow());
    }

    /// <summary>Revit's main window as the list's owner, so it stays in front of Revit.</summary>
    sealed class RevitWindow : IWin32Window
    {
        public IntPtr Handle => System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
    }

    /// <summary>Goes to an item: its level's floor plan, zoomed on it, our elements near it selected.</summary>
    sealed class GoTo : IExternalEventHandler
    {
        public ReviewItem? Item;
        public string? Problem;
        public Action? Done;

        public string GetName() => "Survey to Revit review list";

        public void Execute(UIApplication app)
        {
            Problem = null;
            try { Problem = Show(app.ActiveUIDocument, Item); }
            catch (Exception ex) { Problem = ex.Message; }
            Done?.Invoke();
        }

        static string? Show(UIDocument? uidoc, ReviewItem? item)
        {
            if (uidoc == null || item == null) return "Open the project first.";
            var doc = uidoc.Document;
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == item.Level);
            if (level == null) return $"There is no level called {item.Level} in this project.";
            var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id)
                .OrderBy(v => v.Name == level.Name ? 0 : 1).FirstOrDefault();
            if (view == null) return $"There is no floor plan of {item.Level}.";
            if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;

            double Ft(double mm) => UnitConversion.MmToFeet(mm);
            double x = Ft(item.X), y = Ft(item.Y), z = level.Elevation, zoom = Ft(4000), near = Ft(1000);
            var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
            ui?.ZoomAndCenterRectangle(new XYZ(x - zoom, y - zoom, z), new XYZ(x + zoom, y + zoom, z));

            var box = new BoundingBoxIntersectsFilter(new Outline(new XYZ(x - near, y - near, z - Ft(500)), new XYZ(x + near, y + near, z + Ft(2500))));
            var ours = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().WherePasses(box)
                .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.StartsWith(ModelBuilder.Tag + "|") == true)
                .Select(e => e.Id).ToList();
            uidoc.Selection.SetElementIds(ours);
            return null;
        }
    }

    sealed class ReviewWindow : Form
    {
        static readonly Color Accent = Color.FromArgb(0, 84, 147), Soft = Color.FromArgb(242, 245, 249);
        readonly ReviewList _list;
        readonly string _path;
        readonly GoTo _go;
        readonly ExternalEvent _event;
        readonly ListView _items = new()
        {
            View = System.Windows.Forms.View.Details, FullRowSelect = true, HideSelection = false, CheckBoxes = true,
            MultiSelect = false, Dock = DockStyle.Fill,
        };
        readonly CheckBox _hideDone = new() { Text = "Hide ticked", AutoSize = true, Checked = true, Margin = new Padding(3, 8, 12, 3) };
        readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 8, 3, 3) };
        bool _filling;

        public ReviewWindow(ReviewList list, string path, GoTo handler, ExternalEvent ev)
        {
            _list = list; _path = path; _go = handler; _event = ev;
            Text = $"Review list: {list.Project}";
            Font = new Font("Segoe UI", 9f);
            Size = new Size(820, 520);
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(Screen.PrimaryScreen!.WorkingArea.Right - Width - 20, Screen.PrimaryScreen.WorkingArea.Top + 120);
            ShowInTaskbar = false;

            var head = new Label
            {
                Dock = DockStyle.Top, Height = 46, Padding = new Padding(12, 6, 12, 6), BackColor = Accent, ForeColor = Color.White,
                Text = $"Places the reading was unsure about, from the build of {list.Built:d MMM yyyy HH:mm}. " +
                       "Double-click one to go there (its generated elements are selected); tick it once looked at.",
            };
            _items.Columns.Add("", 28);
            _items.Columns.Add("Level", 130);
            _items.Columns.Add("What", 220);
            _items.Columns.Add("Details", 420);
            _items.ItemActivate += (_, _) => Go();
            _items.ItemChecked += (_, e) => Tick(e.Item);
            _hideDone.CheckedChanged += (_, _) => Fill();

            var go = new Button { Text = "Go to", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            go.Click += (_, _) => Go();
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 6, 8, 6), BackColor = Soft };
            bottom.Controls.AddRange(new Control[] { go, _hideDone, _status });
            Controls.Add(_items);
            Controls.Add(bottom);
            Controls.Add(head);
            _go.Done = () => BeginInvoke(() => _status.Text = _go.Problem ?? Status());
            FormClosed += (_, _) => _event.Dispose();
            Fill();
        }

        string Status() => $"{_list.Items.Count(i => i.Done)} of {_list.Items.Count} ticked";

        void Fill()
        {
            _filling = true;
            _items.BeginUpdate();
            _items.Items.Clear();
            foreach (var i in _list.Items.Where(i => !_hideDone.Checked || !i.Done))
            {
                var row = new ListViewItem(new[] { "", i.Level, i.What, i.Message }) { Tag = i, Checked = i.Done };
                if (i.Priority == 1) row.Font = new Font(_items.Font, FontStyle.Bold);
                _items.Items.Add(row);
            }
            _items.EndUpdate();
            _filling = false;
            _status.Text = Status();
        }

        void Go()
        {
            if (_items.SelectedItems.Count == 0 || _items.SelectedItems[0].Tag is not ReviewItem item) return;
            _go.Item = item;
            _status.Text = $"Going to {item.Level}…";
            _event.Raise();
        }

        void Tick(ListViewItem row)
        {
            if (_filling || row.Tag is not ReviewItem item) return;
            item.Done = row.Checked;
            try { _list.Save(_path); }
            catch (Exception ex) { _status.Text = "Could not save the ticks: " + ex.Message; return; }
            if (item.Done && _hideDone.Checked) BeginInvoke(Fill);
            else _status.Text = Status();
        }
    }
}
