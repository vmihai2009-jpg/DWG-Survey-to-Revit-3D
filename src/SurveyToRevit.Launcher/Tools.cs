using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Launcher;

/// <summary>
/// Buttons the engine declares (SurveyToRevit.Revit.Tools.Buttons()), shown in fixed slots on the panel.
/// Read at start-up and again after each install, so a release that adds a tool shows its button straight
/// away, without restarting Revit. Each row is plain strings: id, text, tooltip, command class, 16 px and
/// 32 px icon resource names in the engine.
/// </summary>
internal static class Tools
{
    static RibbonPanel? _panel;
    static readonly List<PushButton> Buttons = new();
    static string?[] _ids = Array.Empty<string?>();
    static readonly string?[] Commands = new string?[ToolSlots.Capacity];

    /// <summary>Called once from start-up with the panel the slots go on.</summary>
    public static void Start(RibbonPanel panel)
    {
        _panel = panel;
        Refresh(UserSettings.Load());
    }

    /// <summary>Shows the current engine's tools. Returns false if a new button could not be added (it shows after a restart).</summary>
    public static bool Refresh(UserSettings settings)
    {
        if (_panel == null) return true;
        var rows = Read(settings, out var engine);
        var ids = ToolSlots.Assign(_ids, rows.Select(r => r[0]).ToList());
        var ok = true;
        for (var i = 0; i < ids.Length; i++)
        {
            var row = rows.FirstOrDefault(r => r[0] == ids[i]);
            if (i >= Buttons.Count)
            {
                if (row == null) break;
                try
                {
                    var data = new PushButtonData("SurveyToolSlot" + i, row[1].Replace("\\n", "\n"),
                        Assembly.GetExecutingAssembly().Location, "SurveyToRevit.Launcher.ToolSlot" + i);
                    if (_panel.AddItem(data) is not PushButton b) { ok = false; break; }
                    Buttons.Add(b);
                }
                catch { ok = false; break; }
            }
            var button = Buttons[i];
            Commands[i] = row?[3];
            button.Visible = row != null;
            if (row == null) continue;
            button.ItemText = row[1].Replace("\\n", "\n");
            button.ToolTip = row[2];
            if (engine != null)
            {
                button.Image = App.Image(engine.GetManifestResourceStream(row[4]));
                button.LargeImage = App.Image(engine.GetManifestResourceStream(row[5]));
            }
        }
        _ids = ids.Take(Buttons.Count).ToArray();
        return ok;
    }

    static List<string[]> Read(UserSettings settings, out Assembly? engine)
    {
        engine = null;
        try
        {
            var install = Engine.Current(settings);
            if (install == null) return new();
            engine = Engine.Load(install);
            var list = engine.GetType("SurveyToRevit.Revit.Tools")?.GetMethod("Buttons", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as string[][];
            return list?.Where(r => r.Length >= 6 && !string.IsNullOrEmpty(r[0])).ToList() ?? new();
        }
        catch { return new(); } // an engine without tools (before 0.16.1) or one that fails to load: no extra buttons
    }

    public static Result Run(int slot, ExternalCommandData data, ref string message, ElementSet elements)
    {
        var command = slot < Commands.Length ? Commands[slot] : null;
        if (command == null) { message = "This button is not in the installed version of Survey to Revit."; return Result.Failed; }
        return Engine.Run(command, data, ref message, elements);
    }
}

// The slots themselves: Revit needs a class per button. Manual, so the engine's tools can open their own transactions.
[Transaction(TransactionMode.Manual)] public class ToolSlot0 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(0, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot1 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(1, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot2 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(2, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot3 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(3, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot4 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(4, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot5 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(5, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot6 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(6, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot7 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(7, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot8 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(8, d, ref m, e); }
[Transaction(TransactionMode.Manual)] public class ToolSlot9 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => Tools.Run(9, d, ref m, e); }
