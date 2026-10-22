using SurveyToRevit.Launcher;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Core.Tests;

public class LauncherTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "s2r-test-" + Guid.NewGuid().ToString("N"));

    public LauncherTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void The_newest_install_runs_unless_another_is_chosen()
    {
        var engines = Path.Combine(_dir, "engine");
        foreach (var name in new[] { "20261020-101010000_v0.13.0", "20261022-090000000_v0.13.2", "20261021-120000000_v0.13.1", "junk", "20261023-000000000_v0.14.0.partial" })
            Directory.CreateDirectory(Path.Combine(engines, name));
        var list = Releases.List(engines);
        Assert.Equal(new[] { "0.13.0", "0.13.1", "0.13.2" }, list.Select(i => Releases.Short(i.Version)));
        Assert.Equal("0.13.2", Releases.Short(Releases.Pick(list, null)!.Version));
        Assert.Equal("0.13.0", Releases.Short(Releases.Pick(list, "20261020-101010000_v0.13.0")!.Version));
        Assert.Equal("0.13.2", Releases.Short(Releases.Pick(list, "gone")!.Version));
        // keep one older: the oldest goes, unless it is the chosen one
        Assert.Equal("0.13.0", Releases.Short(Assert.Single(Releases.ToRemove(list, 1)).Version));
        Assert.Empty(Releases.ToRemove(list, 1, "20261020-101010000_v0.13.0"));
    }

    [Fact]
    public void Release_zips_are_found_by_version_not_date()
    {
        foreach (var f in new[] { "SurveyToRevit-0.9.1.zip", "SurveyToRevit-0.13.0.zip", "SurveyToRevit-0.12.6.zip", "Other-1.0.zip" })
            File.WriteAllText(Path.Combine(_dir, f), "");
        var newest = Releases.NewestZip(_dir);
        Assert.Equal("0.13.0", Releases.Short(newest!.Value.Version));
        Assert.Null(Releases.NewestZip(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Installing_adds_a_new_engine_folder_and_only_a_newer_launcher()
    {
        // any .NET assembly will do as stand-ins: what matters is their version numbers
        var source = Path.Combine(_dir, "release", "SurveyToRevit");
        Directory.CreateDirectory(source);
        File.Copy(typeof(Assert).Assembly.Location, Path.Combine(source, Installer.EngineDll));
        File.Copy(typeof(LauncherTests).Assembly.Location, Path.Combine(source, Installer.LauncherDll));
        File.WriteAllText(Path.Combine(source, "notes.txt"), "not copied");
        var addins = Path.Combine(_dir, "Addins");
        var root = Path.Combine(addins, "SurveyToRevit");
        var zip = Path.Combine(_dir, "SurveyToRevit-9.9.9.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(_dir, "release"), zip);

        var first = Installer.FromZip(zip, null, new DateTime(2026, 10, 22, 16, 0, 0), root);
        Assert.Equal(typeof(Assert).Assembly.GetName().Version, first.Engine);
        Assert.True(File.Exists(Path.Combine(first.EngineFolder, Installer.EngineDll)));
        Assert.False(File.Exists(Path.Combine(first.EngineFolder, Installer.LauncherDll)));
        Assert.False(File.Exists(Path.Combine(first.EngineFolder, "notes.txt")));
        Assert.NotNull(first.Launcher);
        var manifest = File.ReadAllText(Path.Combine(addins, "SurveyToRevit.addin"));
        Assert.Contains("SurveyToRevit.Launcher.App", manifest);
        Assert.Contains("launcher", manifest);

        // the same launcher is running now: a second install leaves it alone
        var second = Installer.FromZip(zip, first.Launcher, new DateTime(2026, 10, 22, 16, 5, 0), root);
        Assert.Null(second.Launcher);
        Assert.Equal(2, Releases.List(Path.Combine(root, "engine")).Count);
        Assert.Single(Releases.List(Path.Combine(root, "launcher")));
        Assert.Equal(second.EngineFolder, Releases.Pick(Releases.List(Path.Combine(root, "engine")), null)!.Folder);
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "engine"), "*.partial"));

        // tidy: no older copies kept; v0.12-style folders go too
        Directory.CreateDirectory(Path.Combine(root, "20261021-101010"));
        Installer.Tidy(root, 0, Path.GetFileName(second.EngineFolder), null, null);
        Assert.Single(Releases.List(Path.Combine(root, "engine")));
        Assert.False(Directory.Exists(Path.Combine(root, "20261021-101010")));
    }

    [Fact]
    public void A_zip_without_the_add_in_is_refused()
    {
        var source = Path.Combine(_dir, "other");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "x");
        var zip = Path.Combine(_dir, "SurveyToRevit-1.0.0.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, zip);
        Assert.Throws<InvalidDataException>(() => Installer.FromZip(zip, null, DateTime.Now, Path.Combine(_dir, "root")));
    }

    [Fact]
    public void An_engine_loads_beside_the_one_already_running_and_leaves_its_files_free()
    {
        var core = typeof(OpeningCuts).Assembly;
        var folder = Path.Combine(_dir, "engine");
        Directory.CreateDirectory(folder);
        File.Copy(core.Location, Path.Combine(folder, Path.GetFileName(core.Location)));
        var loaded = new EngineContext(folder).LoadMain(core.GetName().Name!);
        Assert.NotSame(core, loaded);
        Assert.Equal(core.GetName().Version, loaded.GetName().Version);
        Assert.Equal("", loaded.Location); // read into memory: the folder is not held open
        Assert.NotNull(loaded.GetType(typeof(OpeningCuts).FullName!));
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Settings_survive_a_round_trip_and_a_damaged_file()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "SlabMm": 250, "FutureOption": [1, 2] }""");
        var s = UserSettings.Load(path);
        Assert.Equal(250, s.SlabMm);
        Assert.True(s.CheckAtStartup);
        s.UpdatesFolder = @"D:\Releases";
        s.Save(path);
        var again = UserSettings.Load(path);
        Assert.Equal(@"D:\Releases", again.Updates);
        Assert.Contains("FutureOption", File.ReadAllText(path)); // kept for the version that knows it
        File.WriteAllText(path, "{ not json");
        Assert.Equal(300, UserSettings.Load(path).SlabMm);
    }
}
