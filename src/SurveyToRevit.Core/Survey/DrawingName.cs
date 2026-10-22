using System.Text.RegularExpressions;

namespace SurveyToRevit.Core.Survey;

/// <summary>
/// Reads the level from the file name: an ISO 19650 / BS 1192 code
/// (000002-PCL-ZZZ-101-M2-G-01000 → "101"; ZZZ/XXX mean "several levels"), or else the
/// words surveyors use ("29463B-2 GROUND FLOOR PLAN", "Basement", "Level 3", "LG", "Roof Plan").
/// Sections and elevations are never plans.
/// </summary>
public sealed record DrawingName(string FileName, string? LevelCode)
{
    /// <param name="sheetTitles">words on the sheet (title block): used when the file name says no level ("16382-02")</param>
    public static DrawingName Parse(string path, IEnumerable<string>? sheetTitles = null)
    {
        var parsed = Parse(path);
        if (parsed.LevelCode != null || sheetTitles == null) return parsed;
        // a sheet titled "... Plan" names its floor; one titled as a section or elevation is not a plan
        var titles = sheetTitles.Select(t => Regex.Replace(t, @"\\[A-Za-z][^;]*;|[{}]|\\P", " ")).ToList();
        if (titles.Any(t => Regex.IsMatch(t, @"\b(sections?|elevations?)\b", RegexOptions.IgnoreCase))) return parsed;
        var level = titles.Where(t => Regex.IsMatch(t, @"\bplan\b", RegexOptions.IgnoreCase)).Select(FromWords).FirstOrDefault(c => c != null);
        return level == null ? parsed : parsed with { LevelCode = level };
    }

    static DrawingName Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('-');
        string? level = null;
        if (parts.Length >= 7)
        {
            var code = parts[3].Trim().ToUpperInvariant();
            if (code is not ("ZZZ" or "XXX" or "ZZ" or "XX")) level = code;
        }
        return new DrawingName(name, level ?? FromWords(name));
    }

    static readonly string[] OrdinalWords = { "ground", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth" };

    static string? FromWords(string name)
    {
        var n = Regex.Replace(name.ToLowerInvariant(), @"[_\-().]+", " ");
        if (Regex.IsMatch(n, @"\b(section|sections|elevation|elevations|axo|3d)\b")) return null;
        bool mezz = Regex.IsMatch(n, @"\bmezz(anine)?\b");
        string? code = null;
        if (Regex.IsMatch(n, @"\blower ground\b|\blg\b|\blgf\b")) code = "LG";
        else if (Regex.IsMatch(n, @"\bupper ground\b|\bug\b|\bugf\b")) code = "UG";
        else if (Regex.Match(n, @"\b(sub ?)?basement( (\d))?\b") is { Success: true } b)
            code = "B" + (b.Groups[1].Success ? "2" : b.Groups[3].Success ? b.Groups[3].Value : "1");
        else if (Regex.Match(n, @"\b(b|lb)(\d)\b") is { Success: true } bn) code = "B" + bn.Groups[2].Value;
        else if (Regex.IsMatch(n, @"\broof\b")) code = "RF";
        else if (Regex.IsMatch(n, @"\b(ground|gf)\b")) code = "00";
        else
        {
            for (int i = 1; i < OrdinalWords.Length && code == null; i++)
                if (Regex.IsMatch(n, $@"\b{OrdinalWords[i]}\b")) code = i.ToString("00");
            if (code == null && Regex.Match(n, @"\b(level|lvl|l|floor)\s?(\d{1,2})\b") is { Success: true } l) code = int.Parse(l.Groups[2].Value).ToString("00");
        }
        if (code == null) return null;
        return mezz && !code.EndsWith("M") ? code + "M" : code;
    }

    static readonly string[] Ordinals = { "Ground", "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth", "Tenth" };

    /// <summary>
    /// Sort key and readable name for a level code. Understands the survey
    /// convention 100 = ground, 101 = first, 099 = basement, "M" suffix = mezzanine,
    /// and simple codes such as 00, 01, B1.
    /// </summary>
    public static (double Order, string Name) DescribeLevel(string code)
    {
        switch (code.ToUpperInvariant().TrimEnd('M'))
        {
            case "LG": return (-0.5 + (code.EndsWith("M", StringComparison.OrdinalIgnoreCase) ? 0.25 : 0), "Lower Ground Floor" + (code.EndsWith("M", StringComparison.OrdinalIgnoreCase) ? " Mezzanine" : ""));
            case "UG": return (0.25, "Upper Ground Floor");
            case "RF": return (999, "Roof");
        }
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
