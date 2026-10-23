using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Revit;

/// <summary>
/// The first window of Build from Survey: what the tool does, which version is running, and the
/// survey files to read (added by file or by whole folder, LiDAR tiles optional), before anything is analysed.
/// </summary>
public sealed class StartDialog : Form
{
    static readonly Color Accent = Color.FromArgb(0, 84, 147), Soft = Color.FromArgb(242, 245, 249);
    const string Plans = "Survey DWGs (*.dwg)|*.dwg";
    const string Tiles = "LiDAR tiles, download zips and footprints (*.zip;*.tif;*.tiff;*.asc;*.shp)|*.zip;*.tif;*.tiff;*.asc;*.shp";

    readonly ListView _files = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly Label _count = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 8, 3, 3) };
    readonly Button _next = new() { Text = "Next: read the survey  ›", AutoSize = true, Padding = new Padding(10, 4, 10, 4), DialogResult = DialogResult.OK };
    string? _folder;

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "" : $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public IReadOnlyList<string> Files => _files.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    public StartDialog(string projectName, bool overlay = false)
    {
        Text = "Survey to Revit " + Version;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 600);
        MinimumSize = new Size(620, 480);
        BackColor = Color.White;
        _folder = UserSettings.Load().LastSurveyFolder;

        // header: icon, name, version
        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Accent, Padding = new Padding(16, 12, 16, 12) };
        var logo = new PictureBox { Image = Logo(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64), Location = new Point(16, 14), BackColor = Color.White };
        header.Controls.Add(logo);
        header.Controls.Add(new Label
        {
            Text = "Survey to Revit", ForeColor = Color.White, Font = new Font("Segoe UI Semilight", 18f),
            AutoSize = true, Location = new Point(94, 12),
        });
        header.Controls.Add(new Label
        {
            Text = $"{Version}   ·   Barr Gazetas   ·   building into: {projectName}", ForeColor = Color.FromArgb(205, 225, 245),
            AutoSize = true, Location = new Point(97, 52),
        });

        // the three steps
        var steps = new TableLayoutPanel { Dock = DockStyle.Top, Height = 78, ColumnCount = 3, BackColor = Soft, Padding = new Padding(12, 8, 12, 8) };
        for (int i = 0; i < 3; i++) steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        steps.Controls.Add(Step("1  Choose the survey", "The floor plan DWGs. Sections, elevations and duplicates are set aside on their own."), 0, 0);
        if (overlay)
        {
            steps.Controls.Add(Step("2  Check the levels", "The floors and their heights found in the survey. Nothing changes until you press OK."), 1, 0);
            steps.Controls.Add(Step("3  Overlay", "Coordinates, levels (Ground Floor at 0) and every drawing linked and lined up. Nothing is built."), 2, 0);
        }
        else
        {
            steps.Controls.Add(Step("2  Check what was read", "Floors, levels, ceiling heights and what to build. Nothing changes until you press Build."), 1, 0);
            steps.Controls.Add(Step("3  Build", "Coordinates, levels, linked plans, walls, windows, doors, floors, ceilings and rooms, in the Existing phase."), 2, 0);
        }

        // the file list
        _files.Columns.Add("File", 260);
        _files.Columns.Add("Used as", 120);
        _files.Columns.Add("Folder", 320);
        _files.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        _files.AllowDrop = true;
        _files.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _files.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) Add(paths.SelectMany(Expand)); };

        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
        tools.Controls.Add(Button("Add survey DWGs...", () => Pick(Plans)));
        tools.Controls.Add(Button("Add a whole folder...", PickFolder));
        tools.Controls.Add(Button("Add LiDAR tiles (optional)...", () => Pick(Tiles)));
        tools.Controls.Add(Button("Remove", RemoveSelected));
        tools.Controls.Add(Button("Clear", () => { _files.Items.Clear(); Changed(); }));

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 6) };
        body.Controls.Add(_files);
        body.Controls.Add(tools);
        body.Controls.Add(new Label
        {
            Dock = DockStyle.Top, Height = 40, ForeColor = Color.FromArgb(60, 60, 60),
            Text = "Survey files to read. Add the plans of every floor (all the DWGs in the survey folder is fine), or drag them in here. " +
                   "LiDAR tiles add the surrounding buildings and ground.",
        });

        // bottom bar
        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(10, 4, 10, 4), DialogResult = DialogResult.Cancel };
        _next.BackColor = Accent;
        _next.ForeColor = Color.White;
        _next.FlatStyle = FlatStyle.Flat;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(12, 8, 12, 10), BackColor = Soft };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(_next);
        bottom.Controls.Add(_count);
        AcceptButton = _next;
        CancelButton = cancel;

        Controls.Add(body);
        Controls.Add(bottom);
        Controls.Add(steps);
        Controls.Add(header);
        Changed();
    }

    static Control Step(string title, string text)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(4, 0, 4, 0) };
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = Accent });
        panel.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(220, 0), ForeColor = Color.FromArgb(70, 70, 70) });
        return panel;
    }

    static Button Button(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
        b.Click += (_, _) => click();
        return b;
    }

    void Pick(string filter)
    {
        using var pick = new OpenFileDialog { Filter = filter + "|All files (*.*)|*.*", Multiselect = true, Title = "Survey to Revit" };
        if (Directory.Exists(_folder)) pick.InitialDirectory = _folder;
        if (pick.ShowDialog(this) == DialogResult.OK) Add(pick.FileNames);
    }

    void PickFolder()
    {
        using var pick = new FolderBrowserDialog { Description = "The survey folder: every DWG in it (and its subfolders) is added", UseDescriptionForTitle = true };
        if (Directory.Exists(_folder)) pick.SelectedPath = _folder;
        if (pick.ShowDialog(this) == DialogResult.OK) Add(Expand(pick.SelectedPath));
    }

    static IEnumerable<string> Expand(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Where(f => Kind(f) != null)
            : new[] { path };

    static string? Kind(string file) =>
        SiteContextBuilder.IsSiteFile(file) ? "LiDAR / site"
        : Path.GetExtension(file).Equals(".dwg", StringComparison.OrdinalIgnoreCase) ? "Survey drawing" : null;

    void Add(IEnumerable<string> paths)
    {
        var have = new HashSet<string>(Files, StringComparer.OrdinalIgnoreCase);
        foreach (var f in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var kind = Kind(f);
            if (kind == null || !have.Add(f)) continue;
            _files.Items.Add(new ListViewItem(new[] { Path.GetFileName(f), kind, Path.GetDirectoryName(f) ?? "" }) { Tag = f });
            _folder = Path.GetDirectoryName(f);
        }
        Changed();
    }

    void RemoveSelected()
    {
        foreach (ListViewItem item in _files.SelectedItems) _files.Items.Remove(item);
        Changed();
    }

    void Changed()
    {
        var files = Files;
        int site = files.Count(SiteContextBuilder.IsSiteFile), dwg = files.Count - site;
        _count.Text = dwg == 0 ? "Add the survey DWGs to continue." : $"{dwg} drawings" + (site > 0 ? $", {site} LiDAR files" : "");
        _next.Enabled = dwg > 0;
    }

    static Image? Logo()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon96.png");
            return s == null ? null : new Bitmap(s);
        }
        catch { return null; }
    }
}
