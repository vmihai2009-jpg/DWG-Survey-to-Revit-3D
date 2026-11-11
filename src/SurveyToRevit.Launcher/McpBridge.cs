using System.Reflection;
using Autodesk.Revit.UI;
using SurveyToRevit.Shared;

namespace SurveyToRevit.Launcher;

/// <summary>
/// How an assistant reaches Survey to Revit. The Barr Gazetas MCP add-in (the hub Claude talks to) looks through
/// the add-ins Revit has loaded for a public static class called McpBridge with these members, and offers its
/// tools next to its own. Nothing here listens or runs by itself: with no hub installed, or its switch off,
/// this class is never called and the add-in works exactly as before.
///
/// Plain strings only cross the boundary (the hub shares no types with us, nor we with the engine):
///   Name                         what to call this add-in
///   List()                       one row per tool: name, description, input schema (JSON), flags
///   Call(app, name, argsJson)    runs a tool, returns JSON; a problem is thrown with a readable message
///   Prompts()                    optional: name, description, text
/// Every call goes to the engine the next click would run (SurveyToRevit.Revit.McpTools), so a release that
/// adds or changes a tool takes effect without restarting Revit, like the ribbon tools.
/// </summary>
public static class McpBridge
{
    public static string Name => "Survey to Revit";

    public static string[][] List() => Rows("List");

    public static string[][] Prompts() => Rows("Prompts");

    public static string Call(UIApplication? app, string name, string argsJson)
    {
        var tools = EngineTools() ?? throw new InvalidOperationException(
            "The installed Survey to Revit has no tools for an assistant yet. Install a newer release with the Updater.");
        var call = tools.GetMethod("Call", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("The installed Survey to Revit cannot be called. Install a newer release with the Updater.");
        try { return (string)call.Invoke(null, new object?[] { app, name, argsJson })!; }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            // the tool's own message, not reflection's wrapper around it
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    static string[][] Rows(string method)
    {
        try
        {
            return EngineTools()?.GetMethod(method, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as string[][]
                   ?? Array.Empty<string[]>();
        }
        catch { return Array.Empty<string[]>(); } // an engine from before the tools, or one that fails to load: nothing to offer
    }

    static readonly object Gate = new();
    static string? _folder;
    static Type? _tools;

    /// <summary>
    /// The engine's tool class. Looked up again only when the engine to run changes (an update, or another
    /// version chosen in Settings): the hub may ask from a background thread, and the engine is loaded once.
    /// </summary>
    static Type? EngineTools()
    {
        var install = Engine.Current(UserSettings.Load());
        if (install == null) return null;
        lock (Gate)
        {
            if (_folder != install.Folder)
            {
                _tools = Engine.Load(install).GetType("SurveyToRevit.Revit.McpTools");
                _folder = install.Folder;
            }
            return _tools;
        }
    }
}
