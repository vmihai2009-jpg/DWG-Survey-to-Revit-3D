using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Launcher;

/// <summary>The Settings window: updates, build defaults, and what is installed where.</summary>
internal sealed class SettingsForm : Form
{
    readonly UserSettings _s = UserSettings.Load();
    readonly TextBox _updates = new() { Width = 380 };
    readonly CheckBox _check = new() { Text = "Show \"Update ready\" on the ribbon when a newer zip is there", AutoSize = true };
    readonly ComboBox _run = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    readonly NumericUpDown _keep = new() { Minimum = 0, Maximum = 20, Width = 60 };
    readonly NumericUpDown _slab = new() { Minimum = 50, Maximum = 1000, Increment = 50, Width = 70 };
    readonly NumericUpDown _radius = new() { Minimum = 50, Maximum = 500, Increment = 50, Width = 70 };
    readonly TextBox _reports = new() { Width = 380 };

    public UserSettings? Saved { get; private set; }

    public SettingsForm()
    {
        Text = "Survey to Revit - Settings";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        _updates.Text = _s.Updates;
        _check.Checked = _s.CheckAtStartup;
        _keep.Value = Math.Clamp(_s.KeepVersions, 0, 20);
        _slab.Value = Math.Clamp(_s.SlabMm, 50, 1000);
        _radius.Value = Math.Clamp(_s.SiteRadiusM, 50, 500);
        _reports.Text = _s.Reports;

        var installs = Engine.Installs();
        _run.Items.Add(new Choice(null, "The newest installed (recommended)"));
        foreach (var i in Enumerable.Reverse(installs))
            _run.Items.Add(new Choice(i.Name, $"{i}  (installed {Stamp(i.Stamp)})"));
        _run.SelectedIndex = Math.Max(0, _run.Items.Cast<Choice>().ToList().FindIndex(c => c.Folder == _s.RunVersion));

        var running = Engine.Current(_s);
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        page.Controls.Add(Group("Updates",
            Row(Label("Release zips are in:"), _updates, Button("Browse...", () => Browse(_updates))),
            Row(_check),
            Row(Label("Version to run:"), _run),
            Row(Label("Older versions to keep:"), _keep, Label("(for going back; extra ones are deleted)"))));
        page.Controls.Add(Group("Build from Survey defaults",
            Row(Label("Floor slab thickness (mm):"), _slab),
            Row(Label("Surroundings radius from LiDAR (m):"), _radius),
            Row(Label("Save reports and logs in:"), _reports, Button("Browse...", () => Browse(_reports)))));
        page.Controls.Add(Group("Installed",
            Row(Label($"Running: {running?.ToString() ?? "none"}. Ribbon and Updater: v{Releases.Short(Engine.LauncherVersion)}.")),
            Row(Label($"Installed in {Releases.Root}")),
            Row(Button("Open install folder", () => Open(Releases.Root)),
                Button("Open reports folder", () => Open(_reports.Text)),
                Button("Open settings file", () => Open(Path.GetDirectoryName(UserSettings.FilePath)!)))));

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Save();
        AcceptButton = ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        page.Controls.Add(buttons);
        Controls.Add(page);
    }

    void Save()
    {
        string? Custom(string text, string fallback) =>
            string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), fallback, StringComparison.OrdinalIgnoreCase) ? null : text.Trim();
        var s = UserSettings.Load(); // keep anything changed elsewhere since this window opened
        s.UpdatesFolder = Custom(_updates.Text, UserSettings.DefaultDownloads);
        s.CheckAtStartup = _check.Checked;
        s.RunVersion = (_run.SelectedItem as Choice)?.Folder;
        s.KeepVersions = (int)_keep.Value;
        s.SlabMm = (int)_slab.Value;
        s.SiteRadiusM = (int)_radius.Value;
        s.ReportsFolder = Custom(_reports.Text, UserSettings.DefaultReports);
        try { s.Save(); Saved = s; }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the settings: " + ex.Message, Text);
            DialogResult = DialogResult.None;
        }
    }

    sealed record Choice(string? Folder, string Label) { public override string ToString() => Label; }

    static string Stamp(string s) =>
        DateTime.TryParseExact(s[..Math.Min(15, s.Length)], "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var d)
            ? d.ToString("d MMM yyyy HH:mm") : s;

    static GroupBox Group(string title, params Control[] rows)
    {
        var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        inner.Controls.AddRange(rows);
        var box = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(620, 0) };
        box.Controls.Add(inner);
        return box;
    }

    static FlowLayoutPanel Row(params Control[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.AddRange(items);
        return row;
    }

    static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

    static Button Button(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => click();
        return b;
    }

    void Browse(TextBox box)
    {
        using var pick = new FolderBrowserDialog { SelectedPath = Directory.Exists(box.Text) ? box.Text : "" };
        if (pick.ShowDialog(this) == DialogResult.OK) box.Text = pick.SelectedPath;
    }

    void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
    }
}
