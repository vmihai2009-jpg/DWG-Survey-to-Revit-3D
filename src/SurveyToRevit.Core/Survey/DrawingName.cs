using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Survey;

/// <summary>
/// Reads the level from an ISO 19650 / BS 1192 style file name,
/// e.g. 000002-PCL-ZZZ-101-M2-G-01000 → level "101". ZZZ/XXX mean "several levels".
/// </summary>
public sealed record DrawingName(string FileName, string? LevelCode)
{
    public static DrawingName Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('-');
        string? level = null;
        if (parts.Length >= 7)
        {
            var code = parts[3].Trim().ToUpperInvariant();
            if (code is not ("ZZZ" or "XXX" or "ZZ" or "XX")) level = code;
        }
        return new DrawingName(name, level);
    }

    static readonly string[] Ordinals = { "Ground", "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth", "Tenth" };

    /// <summary>
    /// Sort key and readable name for a level code. Understands the survey
    /// convention 100 = ground, 101 = first, 099 = basement, "M" suffix = mezzanine,
    /// and simple codes such as 00, 01, B1.
    /// </summary>
    public static (double Order, string Name) DescribeLevel(string code)
    {
        var m = Regex.Match(code, @"^(B)?(\d+)(M)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return (double.MaxValue, $"Level {code}");
        int n = int.Parse(m.Groups[2].Value);
        bool basementPrefix = m.Groups[1].Success, mezz = m.Groups[3].Success;
        int floor = basementPrefix ? -n : (m.Groups[2].Value.Length == 3 ? n - 100 : n);
        string name = floor switch
        {
            < 0 when floor == -1 => "Basement",
            < 0 => $"Basement {-floor}",
            _ when floor < Ordinals.Length => $"{Ordinals[floor]} Floor",
            _ => $"Level {floor:00}",
        };
        if (mezz) name += " Mezzanine";
        return (floor + (mezz ? 0.5 : 0), name);
    }
}
