using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>
/// The window shown after the drawings are read: one row per floor (build it or not,
/// its level and ceiling height, what was found) and what to build.
/// Floors flagged for checking (split levels, few agreeing spot heights) are highlighted.
/// </summary>
public sealed class SurveyDialog : Form
{
    readonly SurveyModel _model;
    readonly DataGridView _grid = new();
    readonly CheckBox _link = new() { Text = "Link each plan at its level (shared coordinates)", Checked = true, AutoSize = true };
    readonly CheckBox _walls = new() { Text = "Walls (Concept Wall types)", Checked = true, AutoSize = true };
    readonly CheckBox _floors = new() { Text = "Floor slabs, thickness (mm):", Checked = true, AutoSize = true };
    readonly NumericUpDown _slab = new() { Minimum = 50, Maximum = 1000, Increment = 50, Value = 300, Width = 70 };
    readonly CheckBox _ceilings = new() { Text = "Ceilings (height per floor in the table)", Checked = true, AutoSize = true };
    readonly CheckBox _generic = new() { Text = "Generic models where the drawing is unclear (to check)", Checked = true, AutoSize = true };

    public BuildOptions Options { get; } = new();
    public bool OpenReport { get; private set; }

    public SurveyDialog(SurveyModel model, int skippedFiles)
    {
        _model = model;
        Text = "Survey to Revit";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 620);
        MinimumSize = new Size(760, 480);

        var f = model.Frame;
        var header = new Label
        {
            Dock = DockStyle.Top, Height = 58, Padding = new Padding(8, 8, 8, 0),
            Text = $"{model.Levels.Count} floor plans read. Survey base point E {f.BasePoint.X / 1000:0.000} N {f.BasePoint.Y / 1000:0.000} becomes the internal origin; " +
                   $"project north turned {f.RotationDeg:0.00}° from true north.\n" +
                   (skippedFiles > 0 ? $"{skippedFiles} files not used (elevations, sections, duplicates; see the report). " : "") +
                   "Edit levels or ceiling heights below if needed. Highlighted floors need checking.",
        };

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Build", HeaderText = "Build", FillWeight = 30 });
        AddText("Floor", "Floor", 90, true);
        AddText("Level", "Level (m)", 50, false);
        AddText("Ceiling", "Ceiling above floor (mm)", 70, false);
        AddText("Walls", "Walls", 40, true);
        AddText("Area", "Floor area (m²)", 50, true);
        AddText("Unclear", "Areas to check", 50, true);
        AddText("Drawing", "Drawing", 160, true);
        foreach (var l in model.Levels)
        {
            int r = _grid.Rows.Add(l.Include, l.Name, double.IsNaN(l.ElevationMm) ? "" : (l.ElevationMm / 1000).ToString("0.000", CultureInfo.InvariantCulture),
                l.CeilingHeightMm.ToString("0", CultureInfo.InvariantCulture), l.Walls.Walls.Count,
                l.Outline == null ? "-" : (l.Outline.Area / 1e6).ToString("0"), l.Unclear.Count, l.Plan.Name.FileName);
            if (model.Notes.Any(n => n.StartsWith("CHECK " + l.Name + ":")) || double.IsNaN(l.ElevationMm))
            {
                _grid.Rows[r].DefaultCellStyle.BackColor = Color.LightYellow;
                _grid.Rows[r].Cells["Level"].ToolTipText = model.Notes.FirstOrDefault(n => n.StartsWith("CHECK " + l.Name + ":")) ?? "No spot heights: enter the level.";
            }
        }

        var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 110, FlowDirection = FlowDirection.TopDown, WrapContents = true, Padding = new Padding(8) };
        var slabRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        slabRow.Controls.Add(_floors);
        slabRow.Controls.Add(_slab);
        options.Controls.AddRange(new Control[] { _link, _walls, slabRow, _ceilings, _generic });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 6) };
        var build = new Button { Text = "Build", Width = 100 };
        var cancel = new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel };
        var report = new Button { Text = "Open report", Width = 110 };
        build.Click += (_, _) => { if (Apply()) { DialogResult = DialogResult.OK; Close(); } };
        report.Click += (_, _) => { OpenReport = true; DialogResult = DialogResult.Retry; Close(); };
        buttons.Controls.AddRange(new Control[] { build, cancel, report });
        AcceptButton = build;
        CancelButton = cancel;

        var footer = new Label
        {
            Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(8, 0, 8, 0), ForeColor = Color.DimGray,
            Text = "Elements from an earlier run on the same floors are replaced; nothing else in the model is touched. Undo reverses the whole run.",
        };

        Controls.Add(_grid);
        Controls.Add(header);
        Controls.Add(options);
        Controls.Add(footer);
        Controls.Add(buttons);
    }

    void AddText(string name, string header, float weight, bool readOnly) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, ReadOnly = readOnly });

    /// <summary>Copies the edits back into the model; false (with a message) if a value cannot be read.</summary>
    bool Apply()
    {
        _grid.EndEdit();
        for (int i = 0; i < _model.Levels.Count; i++)
        {
            var row = _grid.Rows[i];
            var l = _model.Levels[i];
            l.Include = row.Cells["Build"].Value is true;
            if (!l.Include) continue;
            if (!TryRead(row.Cells["Level"].Value, out var levelM))
            {
                MessageBox.Show(this, $"{l.Name}: enter the floor level in metres (e.g. 26.47).", Text);
                return false;
            }
            if (!TryRead(row.Cells["Ceiling"].Value, out var ceiling) || ceiling <= 0)
            {
                MessageBox.Show(this, $"{l.Name}: enter the ceiling height in mm (e.g. 2700).", Text);
                return false;
            }
            l.ElevationMm = Math.Round(levelM * 1000 / 10) * 10;
            l.CeilingHeightMm = Math.Round(ceiling / 10) * 10;
        }
        // Wall heights follow the (possibly edited) levels: each floor's walls run to the next one up.
        var built = _model.Levels.Where(l => l.Include).OrderBy(l => l.ElevationMm).ToList();
        for (int i = 0; i < built.Count; i++)
            built[i].WallHeightMm = i + 1 < built.Count ? built[i + 1].ElevationMm - built[i].ElevationMm : SurveyAnalyzer.DefaultTopWallHeightMm;

        Options.LinkPlans = _link.Checked;
        Options.Walls = _walls.Checked;
        Options.Floors = _floors.Checked;
        Options.SlabThicknessMm = (double)_slab.Value;
        Options.Ceilings = _ceilings.Checked;
        Options.UnclearAsGenericModels = _generic.Checked;
        return true;
    }

    static bool TryRead(object? value, out double result)
    {
        var s = value?.ToString()?.Trim() ?? "";
        return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}
