using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SurveyToRevit.Core.TestFit;

namespace SurveyToRevit.Revit;

/// <summary>
/// The test-fit page in a window over Revit, shown in Microsoft Edge WebView2. The page and the floor
/// (site.js) are written to a fresh folder under %LocalAppData%\SurveyToRevit\TestFit and served from
/// there, so the page works offline. The window is modal: Revit waits until it is closed, or until the page's
/// Send to Revit hands back its layout.
/// </summary>
internal sealed class TestFitWindow : Window
{
    const string Host = "testfit.surveytorevit";
    static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurveyToRevit");
    static readonly string Pages = Path.Combine(Local, "TestFit");
    readonly WebView2 _web = new();
    string _folder;

    /// <summary>The layout sent with "Send to Revit" (the page's JSON), or null if the window was closed without it.</summary>
    string? _sent;
    TestFitSite _site;
    readonly Func<string, TestFitSite?> _load;
    readonly Func<string, string>? _measure;
    readonly Dictionary<string, string> _states = new();

    /// <summary>
    /// Shows the page on a floor; the page's floor list browses to others (read with <paramref name="load"/>, by
    /// level id). Returns the layout sent and the floor it was sent from, or null if closed without sending.
    /// </summary>
    /// <remarks><paramref name="measure"/> gives the types and sizes of a family picked on the Assets tab (by its id there), as JSON.</remarks>
    public static (string Layout, TestFitSite Site)? Open(TestFitSite site, Func<string, TestFitSite?> load, Func<string, string>? measure, IntPtr revit)
    {
        LoadWebView2();
        var window = new TestFitWindow(site, load, measure);
        new WindowInteropHelper(window).Owner = revit;
        window.ShowDialog();
        return window._sent == null ? null : (window._sent, window._site);
    }

    TestFitWindow(TestFitSite site, Func<string, TestFitSite?> load, Func<string, string>? measure)
    {
        _site = site;
        _load = load;
        _measure = measure;
        _folder = WritePage(site);
        Title = "Office Test-Fit · " + site.Title;
        Width = Math.Min(1600, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(980, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = _web;
        Loaded += async (_, _) => await Start();
    }

    async Task Start()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Local, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, _folder, CoreWebView2HostResourceAccessKind.Allow);
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                string message;
                try { message = e.TryGetWebMessageAsString(); }
                catch (ArgumentException) { return; }
                if (FloorRequest(message) is { } go) { Browse(go.Id, go.State); return; }
                if (AssetRequest(message) is { } asset) { Measure(asset.Slot, asset.Id); return; }
                _sent = message;
                Close();
            };
            _web.Source = new Uri($"https://{Host}/testfit.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Fail("This PC does not have the Microsoft Edge WebView2 Runtime, which the test-fit window needs. " +
                 "It installs without admin rights from https://developer.microsoft.com/microsoft-edge/webview2/ (Evergreen Bootstrapper).");
        }
        catch (Exception ex) { Fail("The test-fit page could not start: " + ex.Message); }
    }

    /// <summary>{"kind":"floor","id":"…","state":{…}}: the page asks for another floor and hands over its own state.</summary>
    static (string Id, string? State)? FloorRequest(string message)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(message);
            var root = json.RootElement;
            if (!root.TryGetProperty("kind", out var k) || k.GetString() != "floor") return null;
            var state = root.TryGetProperty("state", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Object ? s.GetRawText() : null;
            return (root.GetProperty("id").GetString() ?? "", state);
        }
        catch { return null; }
    }

    /// <summary>{"kind":"asset","slot":"work:desk","id":"…"}: the Assets tab picked a family and wants its sizes.</summary>
    static (string Slot, string Id)? AssetRequest(string message)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(message);
            var root = json.RootElement;
            if (!root.TryGetProperty("kind", out var k) || k.GetString() != "asset") return null;
            return (root.GetProperty("slot").GetString() ?? "", root.GetProperty("id").GetString() ?? "");
        }
        catch { return null; }
    }

    void Measure(string slot, string id)
    {
        string result;
        try { result = _measure?.Invoke(id) ?? "{\"error\":\"Not available.\"}"; }
        catch (Exception ex) { result = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }); }
        _web.CoreWebView2.ExecuteScriptAsync($"window.testfitAsset&&window.testfitAsset({System.Text.Json.JsonSerializer.Serialize(slot)},{System.Text.Json.JsonSerializer.Serialize(id)},{result})");
    }

    void Browse(string levelId, string? state)
    {
        if (state != null) _states[Key(_site)] = state;
        TestFitSite? next;
        try { next = _load(levelId); }
        catch (Exception ex) { Autodesk.Revit.UI.TaskDialog.Show("Office Test-Fit", "That floor could not be read: " + ex.Message); return; }
        if (next == null || next.Faces.Count == 0)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Office Test-Fit", "That floor has no slab, room or wall to lay out.");
            _web.CoreWebView2.ExecuteScriptAsync("window.testfitFloorRefused&&window.testfitFloorRefused()");
            return;
        }
        next.State = _states.GetValueOrDefault(Key(next));
        _site = next;
        _folder = WritePage(next);
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, _folder, CoreWebView2HostResourceAccessKind.Allow);
        Title = "Office Test-Fit · " + next.Title;
        _web.CoreWebView2.Navigate($"https://{Host}/testfit.html?t={DateTime.Now.Ticks}");
    }

    static string Key(TestFitSite s) => string.IsNullOrEmpty(s.Pick) ? s.LevelId : s.Pick;

    void Fail(string why)
    {
        Autodesk.Revit.UI.TaskDialog.Show("Office Test-Fit", why);
        Close();
    }

    /// <summary>
    /// WebView2 finds its native loader next to the program by default (Revit's folder, not ours, since the
    /// engine is loaded from memory), so the loader carried in the engine is written out and pointed to.
    /// </summary>
    static void LoadWebView2()
    {
        var version = typeof(CoreWebView2Environment).Assembly.GetName().Version?.ToString() ?? "0";
        var folder = Path.Combine(Local, "WebView2Loader", version);
        var dll = Path.Combine(folder, "WebView2Loader.dll");
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(folder);
            var temp = dll + "." + Guid.NewGuid().ToString("N");
            using (var from = Resource("TestFit.WebView2Loader.dll"))
            using (var to = File.Create(temp)) from.CopyTo(to);
            try { File.Move(temp, dll); }
            catch (IOException) { File.Delete(temp); } // another Revit wrote it first
        }
        try { CoreWebView2Environment.SetLoaderDllFolderPath(folder); }
        catch (InvalidOperationException) { /* already loaded in this Revit session */ }
    }

    /// <summary>The page, three.js and this floor's site.js in a new folder; older folders are removed.</summary>
    static string WritePage(TestFitSite site)
    {
        Directory.CreateDirectory(Pages);
        foreach (var old in Directory.GetDirectories(Pages))
        {
            try { if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(old, true); }
            catch { /* still open in another Revit */ }
        }
        var folder = Path.Combine(Pages, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "testfit.html", "three.min.js" })
        {
            using var from = Resource("TestFit." + name);
            using var to = File.Create(Path.Combine(folder, name));
            from.CopyTo(to);
        }
        File.WriteAllText(Path.Combine(folder, "site.js"), site.ToScript());
        return folder;
    }

    static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new FileNotFoundException("Missing from this release: " + name);
}
