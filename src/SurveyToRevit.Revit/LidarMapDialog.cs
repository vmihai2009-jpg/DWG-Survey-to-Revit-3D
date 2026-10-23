using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SurveyToRevit.Core;
using SurveyToRevit.Core.Site;
using SurveyToRevit.Core.Survey;

namespace SurveyToRevit.Revit;

/// <summary>
/// Gets the LiDAR for the site from the Environment Agency without leaving Revit: a shaded-relief map of
/// the area round the survey (from the EA's own surface model), the building's outline in red and the
/// area to download as a blue square. Click or drag on the map to move the square, set its size with
/// the radius; "Download and use" fetches the surface (DSM) and ground (DTM) models for that square.
/// </summary>
public sealed class LidarMapDialog : Form
{
    readonly SurveyFrame _frame;
    readonly List<List<PointF>> _outlines;     // grid metres
    readonly PointF _home;                     // survey base point, grid metres
    readonly double _mapHalf;                  // the map shows this far round the base point (m)
    readonly PictureBox _map = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    readonly NumericUpDown _radius = new() { Minimum = 50, Maximum = 500, Increment = 50, Width = 70 };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 4, 8, 0), ForeColor = Color.DimGray };
    readonly Button _use = new() { Text = "Download and use", Width = 140 };
    readonly string _folder = Path.Combine(Path.GetTempPath(), "SurveyToRevit-lidar-download");
    Bitmap? _relief;
    PointF _centre;
    bool _dragging;

    /// <summary>The downloaded DSM and DTM files (GeoTIFF), once "Download and use" has finished.</summary>
    public List<string> Files { get; } = new();
    public double RadiusM => (double)_radius.Value;
    /// <summary>Centre of the downloaded square, grid metres (the base point unless moved).</summary>
    public (double E, double N) Centre => (_centre.X, _centre.Y);

    public LidarMapDialog(SurveyFrame frame, IEnumerable<Polygon2> outlines, double radiusM)
    {
        _frame = frame;
        _home = _centre = new PointF((float)(frame.BasePoint.X / 1000), (float)(frame.BasePoint.Y / 1000));
        _outlines = outlines.Select(o => o.Vertices.Select(v => { var s = frame.ToSurvey(v); return new PointF((float)(s.X / 1000), (float)(s.Y / 1000)); }).ToList()).ToList();
        _radius.Value = (decimal)Math.Clamp(radiusM, 50, 500);
        _mapHalf = 750;

        Text = "LiDAR from the Environment Agency";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 820);
        MinimumSize = new Size(520, 560);

        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 56, Padding = new Padding(8, 8, 8, 0),
            Text = "The site is found from the survey's coordinates (red outline). The blue square is the area to download: " +
                   "click or drag on the map to move it, set its size with the radius. Data: Environment Agency LIDAR Composite DSM and DTM 1 m, " +
                   "Open Government Licence.",
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 6) };
        var cancel = new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel };
        var centre = new Button { Text = "Back to the site", Width = 120 };
        bar.Controls.AddRange(new Control[] { cancel, _use, centre, _radius, new Label { Text = "Radius (m):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) } });
        Controls.Add(_map);
        Controls.Add(intro);
        Controls.Add(_status);
        Controls.Add(bar);
        CancelButton = cancel;

        _map.Paint += (_, e) => Draw(e.Graphics);
        _map.Resize += (_, _) => _map.Invalidate();
        _map.MouseDown += (_, e) => { _dragging = true; MoveTo(e.Location); };
        _map.MouseMove += (_, e) => { if (_dragging) MoveTo(e.Location); };
        _map.MouseUp += (_, _) => _dragging = false;
        _radius.ValueChanged += (_, _) => { Clamp(); _map.Invalidate(); };
        centre.Click += (_, _) => { _centre = _home; _map.Invalidate(); };
        _use.Click += async (_, _) => await Download();
        Shown += async (_, _) => await LoadMap();
    }

    static HttpClient Client() => new(new HttpClientHandler { DefaultProxyCredentials = CredentialCache.DefaultCredentials })
    { Timeout = TimeSpan.FromMinutes(3) };

    async Task LoadMap()
    {
        _status.Text = "Loading the map from the Environment Agency…";
        try
        {
            using var http = Client();
            double minE = Math.Floor(_home.X - _mapHalf), minN = Math.Floor(_home.Y - _mapHalf), maxE = Math.Ceiling(_home.X + _mapHalf), maxN = Math.Ceiling(_home.Y + _mapHalf);
            var path = await LidarService.Download(http, LidarService.Dsm, minE, minN, maxE, maxN, Path.Combine(_folder, "map"));
            var grid = await Task.Run(() => GeoRaster.Read(path, minE, minN, maxE, maxN));
            if (grid == null) { _status.Text = "The Environment Agency has no LiDAR here. You can still try the download, or use a zip from their website."; return; }
            var shade = await Task.Run(() => LidarService.ShadedRelief(grid));
            _relief = ToBitmap(shade, grid.Width, grid.Height);
            _reliefBox = (grid.OriginX, grid.OriginY - grid.Height * grid.CellSize, grid.OriginX + grid.Width * grid.CellSize, grid.OriginY);
            _status.Text = "Map: Environment Agency LIDAR Composite DSM 1 m (white where it has no data).";
        }
        catch (Exception ex)
        {
            _status.Text = "The map could not be loaded (" + ex.Message + "). The download may still work; or get a zip from the Environment Agency website.";
        }
        _map.Invalidate();
    }

    (double MinE, double MinN, double MaxE, double MaxN) _reliefBox;

    static Bitmap ToBitmap(byte[] grey, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new int[width];
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++) { int g = grey[r * width + c]; row[c] = unchecked((int)0xFF000000) | g << 16 | g << 8 | g; }
            Marshal.Copy(row, 0, data.Scan0 + r * data.Stride, width);
        }
        bmp.UnlockBits(data);
        return bmp;
    }

    // map view: grid metres <-> pixels (north up, the whole map area fitted in the box)
    float Scale => Math.Min(_map.Width, _map.Height) / (float)(2 * _mapHalf);
    PointF ToPx(PointF g)
    {
        float s = Scale;
        return new PointF(_map.Width / 2f + (g.X - _home.X) * s, _map.Height / 2f - (g.Y - _home.Y) * s);
    }
    PointF ToGrid(Point p)
    {
        float s = Scale;
        return new PointF(_home.X + (p.X - _map.Width / 2f) / s, _home.Y - (p.Y - _map.Height / 2f) / s);
    }

    void MoveTo(Point p) { _centre = ToGrid(p); Clamp(); _map.Invalidate(); }

    // the square stays within the map
    void Clamp()
    {
        float lim = (float)(_mapHalf - RadiusM);
        _centre = new PointF(Math.Clamp(_centre.X, _home.X - lim, _home.X + lim), Math.Clamp(_centre.Y, _home.Y - lim, _home.Y + lim));
    }

    void Draw(Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (_relief != null)
        {
            var a = ToPx(new PointF((float)_reliefBox.MinE, (float)_reliefBox.MaxN));
            var b = ToPx(new PointF((float)_reliefBox.MaxE, (float)_reliefBox.MinN));
            g.DrawImage(_relief, RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y));
        }
        else g.DrawString("No map yet", Font, Brushes.Gray, 10, 10);
        using (var red = new Pen(Color.Red, 2))
            foreach (var o in _outlines.Where(o => o.Count >= 3))
                g.DrawPolygon(red, o.Select(ToPx).ToArray());
        float r = (float)RadiusM;
        var tl = ToPx(new PointF(_centre.X - r, _centre.Y + r));
        var br = ToPx(new PointF(_centre.X + r, _centre.Y - r));
        using (var fill = new SolidBrush(Color.FromArgb(40, 30, 110, 220))) g.FillRectangle(fill, RectangleF.FromLTRB(tl.X, tl.Y, br.X, br.Y));
        using (var blue = new Pen(Color.FromArgb(30, 110, 220), 2)) g.DrawRectangle(blue, tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
        g.DrawString("N ↑", Font, Brushes.Black, _map.Width - 36, 6);
        // 100 m scale bar
        float bar = 100 * Scale;
        g.FillRectangle(Brushes.Black, 10, _map.Height - 16, bar, 4);
        g.DrawString("100 m", Font, Brushes.Black, 10, _map.Height - 34);
    }

    async Task Download()
    {
        _use.Enabled = false;
        try
        {
            using var http = Client();
            double r = RadiusM;
            double minE = Math.Floor(_centre.X - r), minN = Math.Floor(_centre.Y - r), maxE = Math.Ceiling(_centre.X + r), maxN = Math.Ceiling(_centre.Y + r);
            Files.Clear();
            _status.Text = "Downloading the surface model (DSM)…";
            Files.Add(await LidarService.Download(http, LidarService.Dsm, minE, minN, maxE, maxN, _folder));
            _status.Text = "Downloading the ground model (DTM)…";
            Files.Add(await LidarService.Download(http, LidarService.Dtm, minE, minN, maxE, maxN, _folder));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "The download failed: " + ex.Message +
                           " You can instead download a zip from the Environment Agency's LiDAR website and add it with the DWGs.";
            _use.Enabled = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _relief?.Dispose();
        base.Dispose(disposing);
    }
}
