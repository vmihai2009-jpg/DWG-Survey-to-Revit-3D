namespace SurveyToRevit.Launcher;

/// <summary>
/// Which ribbon slot shows which engine tool. Revit cannot remove a button once added, but it can hide one
/// and change its text, picture and what it runs, so slots are kept and reused: a tool keeps its slot, a
/// tool the engine no longer has hides its slot, and a new tool takes a hidden slot or a new one.
/// No Revit here, so it can be tested anywhere.
/// </summary>
internal static class ToolSlots
{
    public const int Capacity = 10;

    /// <summary>The tool id each slot shows (null = hidden) for the wanted tools, in the engine's order where slots allow.</summary>
    public static string?[] Assign(IReadOnlyList<string?> current, IReadOnlyList<string> wanted)
    {
        var slots = current.ToList();
        for (var i = 0; i < slots.Count; i++)
            if (slots[i] != null && !wanted.Contains(slots[i])) slots[i] = null;
        foreach (var id in wanted.Distinct())
        {
            if (slots.Contains(id)) continue;
            var free = slots.IndexOf(null);
            if (free >= 0) slots[free] = id;
            else if (slots.Count < Capacity) slots.Add(id);
        }
        return slots.ToArray();
    }
}
