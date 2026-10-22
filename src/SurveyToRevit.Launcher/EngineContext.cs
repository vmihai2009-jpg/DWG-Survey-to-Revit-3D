using System.Reflection;
using System.Runtime.Loader;

namespace SurveyToRevit.Launcher;

/// <summary>
/// Loads one installed engine folder on its own, so a newer one can be loaded next to it in the same
/// Revit session (.NET cannot unload or replace an assembly that is already in use). Files are read
/// into memory rather than opened in place, so installing, updating and deleting folders is never
/// blocked by Revit. Anything not in the folder (Revit's API, .NET itself) comes from Revit as usual.
/// </summary>
internal sealed class EngineContext : AssemblyLoadContext
{
    readonly string _folder;

    public EngineContext(string folder) : base("SurveyToRevit " + Path.GetFileName(folder)) => _folder = folder;

    protected override Assembly? Load(AssemblyName name)
    {
        // Revit's own assemblies must be the ones Revit already loaded, or its types would not match.
        if (name.Name == null || name.Name.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)
            || name.Name.StartsWith("AdWindows", StringComparison.OrdinalIgnoreCase)) return null;
        var dll = Path.Combine(_folder, name.Name + ".dll");
        if (!File.Exists(dll)) return null;
        var pdb = Path.ChangeExtension(dll, ".pdb");
        using var code = new MemoryStream(File.ReadAllBytes(dll));
        using var symbols = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null;
        return LoadFromStream(code, symbols);
    }

    public Assembly LoadMain(string name) => LoadFromAssemblyName(new AssemblyName(name));
}
