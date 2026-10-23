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
/// there, so the page works offline. The window is modal: Revit waits until it is closed.
/// </summary>
internal sealed class TestFitWindow : Window
{
    const string Host = "testfit.surveytorevit";
    static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurveyToRevit");
    static readonly string Pages = Path.Combine(Local, "TestFit");
    readonly WebView2 _web = new();
    readonly string _folder;

    public static void Open(TestFitSite site, IntPtr revit)
    {
        LoadWebView2();
        var folder = WritePage(site);
        var window = new TestFitWindow(folder, site.Title);
        new WindowInteropHelper(window).Owner = revit;
        window.ShowDialog();
    }

    TestFitWindow(string folder, string title)
    {
        _folder = folder;
        Title = "Office Test-Fit · " + title;
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
            _web.Source = new Uri($"https://{Host}/testfit.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Fail("This PC does not have the Microsoft Edge WebView2 Runtime, which the test-fit window needs. " +
                 "It installs without admin rights from https://developer.microsoft.com/microsoft-edge/webview2/ (Evergreen Bootstrapper).");
        }
        catch (Exception ex) { Fail("The test-fit page could not start: " + ex.Message); }
    }

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
