namespace SurveyToRevit.Core.Survey;

/// <summary>
/// One DWG holding several floor plans side by side (and often the sections too), as many surveyors deliver
/// a small building. The wall linework falls into separate islands; each island takes the floor named by the
/// plan title nearest it ("GROUND FLOOR PLAN", "1ST FLOOR"). The floors are then laid over one another: each
/// island is moved onto the reference floor (the ground floor, else the largest) by the shift that lines its
/// walls up best with it, searched around the shift the titles suggest. What is left of the sheet (sections,
/// key, title block) is kept apart for the section reader.
/// </summary>
public static class SheetSplitter
{
    const double Cell = 2000, MinLines = 30;

    public sealed record Result(List<SurveyDrawing> Plans, SurveyDrawing Rest, List<string> Notes);

    public static Result? Split(SurveyDrawing d)
    {
        if (d.WallLines.Count < 2 * MinLines) return null;
        var islands = Islands(d.WallLines);
        if (islands.Count(b => b.Lines >= MinLines) < 2) return null;

        // floor titles: "... plan" texts first, then bare floor names ("1ST FLOOR", "BASEMENT")
        var titled = d.Texts.Select(t => (Text: t, Code: DrawingName.LevelFromWords(t.Value), Plan: System.Text.RegularExpressions.Regex.IsMatch(t.Value, @"\bplan\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Where(x => x.Code != null && x.Text.Value.Length <= 40).ToList();
        var assigned = new List<(Box Box, string Code, SurveyText Title)>();
        foreach (var box in islands)
        {
            // the nearest title outside the island (a note inside it is not its title); plan titles before bare names
            double Gap(Vec2 t)
            {
                double dx = t.X < box.MinX ? box.MinX - t.X : t.X > box.MaxX ? t.X - box.MaxX : 0;
                double dy = t.Y < box.MinY ? box.MinY - t.Y : t.Y > box.MaxY ? t.Y - box.MaxY : 0;
                return Math.Sqrt(dx * dx + dy * dy);
            }
            var pick = titled.Select(x => (x, Gap: Gap(x.Text.Position)))
                .Where(x => x.Gap > 0 && x.Gap <= 60000)
                .OrderBy(x => x.x.Plan ? 0 : 1).ThenBy(x => x.Gap).FirstOrDefault();
            if (pick.x.Code != null) assigned.Add((box, pick.x.Code!, pick.x.Text));
        }
        // a floor drawn in separate pieces (vaults, a detached wing) is one plan: its islands under the same title together
        var plans = assigned.GroupBy(a => a.Code).Where(g => g.Max(a => a.Box.Lines) >= MinLines).Select(g =>
        {
            var main = g.OrderByDescending(a => a.Box.Lines).First();
            var parts = g.Where(a => a.Title == main.Title).ToList();
            var box = new Box(parts.Min(a => a.Box.MinX), parts.Min(a => a.Box.MinY), parts.Max(a => a.Box.MaxX), parts.Max(a => a.Box.MaxY), parts.Sum(a => a.Box.Lines));
            return (Box: box, main.Code, main.Title);
        }).ToList();
        if (plans.Count < 2) return null;

        var notes = new List<string>();
        var reference = plans.FirstOrDefault(p => p.Code == "00");
        if (reference.Code == null) reference = plans.OrderByDescending(p => p.Box.Lines).First();
        var refLines = d.WallLines.Where(l => reference.Box.Holds(l.Mid)).ToList();
        var result = new List<SurveyDrawing>();
        foreach (var p in plans)
        {
            // every title naming both floors in the same way ("GROUND FLOOR PLAN" / "1ST FLOOR PLAN", "GROUND" / "1ST FLOOR") gives a shift
            var guesses = new List<Vec2> { reference.Title.Position - p.Title.Position };
            foreach (var t in titled.Where(x => x.Code == p.Code))
                foreach (var r in titled.Where(x => x.Code == reference.Code && x.Plan == t.Plan && Math.Abs(x.Text.Position.Y - t.Text.Position.Y) < 1))
                    guesses.Add(r.Text.Position - t.Text.Position);
            var shift = p.Code == reference.Code ? new Vec2(0, 0) : Register(d.WallLines.Where(l => p.Box.Holds(l.Mid)).ToList(), refLines, guesses, p.Box, reference.Box);
            result.Add(Part(d, p.Box, shift, $"{d.Name.FileName} ({p.Title.Value.Trim()})", p.Code));
            if (p.Code != reference.Code) notes.Add($"{d.Name.FileName}: \"{p.Title.Value.Trim()}\" laid over \"{reference.Title.Value.Trim()}\" (moved {shift.X / 1000:0.000}, {shift.Y / 1000:0.000} m on the sheet).");
        }
        notes.Insert(0, $"{d.Name.FileName}: {result.Count} floor plans on one sheet, read as separate floors.");
        var rest = Rest(d, plans.Select(p => p.Box).ToList());
        return new Result(result, rest, notes);
    }

    internal readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY, int Lines)
    {
        public bool Holds(Vec2 p, double margin = 1000) => p.X >= MinX - margin && p.X <= MaxX + margin && p.Y >= MinY - margin && p.Y <= MaxY + margin;
    }

    /// <summary>Groups of wall lines with no more than about a cell between them.</summary>
    internal static List<Box> Islands(IReadOnlyList<Segment2> lines)
    {
        var cells = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            int n = Math.Max(1, (int)(l.Length / (Cell / 2)));
            for (int k = 0; k <= n; k++)
            {
                var p = l.A + (l.B - l.A) * ((double)k / n);
                var key = ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell));
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                if (list.Count == 0 || list[^1] != i) list.Add(i);
            }
        }
        var comp = new Dictionary<(int, int), int>();
        int next = 0;
        foreach (var start in cells.Keys)
        {
            if (comp.ContainsKey(start)) continue;
            var stack = new Stack<(int, int)>(); stack.Push(start); comp[start] = next;
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var k = (x + dx, y + dy);
                        if (cells.ContainsKey(k) && !comp.ContainsKey(k)) { comp[k] = next; stack.Push(k); }
                    }
            }
            next++;
        }
        var boxes = new List<Box>();
        foreach (var g in cells.GroupBy(c => comp[c.Key]))
        {
            var idx = g.SelectMany(c => c.Value).Distinct().ToList();
            if (idx.Count < 5) continue; // small pieces join a floor whose title they share
            var pts = idx.SelectMany(i => new[] { lines[i].A, lines[i].B }).ToList();
            boxes.Add(new Box(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y), idx.Count));
        }
        return boxes;
    }

    static HashSet<(int, int)> Occupied(IEnumerable<Segment2> lines, double cell) =>
        Samples(lines, cell / 2).Select(p => ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell))).ToHashSet();

    /// <summary>The shift putting <paramref name="lines"/> best over the reference walls: coarse around the guesses, then fine.</summary>
    static Vec2 Register(List<Segment2> lines, List<Segment2> refLines, List<Vec2> titleGuesses, Box box, Box refBox)
    {
        var guess = titleGuesses[0];
        // a floor much smaller than the reference (a vault, a plant room) fits in many places: a shift the
        // titles give exactly is kept when it fits nearly as well as the best
        // the titles can sit anywhere by their plans: also try lining up the islands' corners and centres
        var guesses = titleGuesses.Concat(new[]
        {
            new Vec2(refBox.MinX - box.MinX, refBox.MinY - box.MinY),
            new Vec2(refBox.MaxX - box.MaxX, refBox.MaxY - box.MaxY),
            new Vec2((refBox.MinX + refBox.MaxX - box.MinX - box.MaxX) / 2, (refBox.MinY + refBox.MaxY - box.MinY - box.MaxY) / 2),
        }).ToList();
        int Fit(Vec2 sh, double cell)
        {
            var set = Occupied(refLines, cell);
            return Samples(lines, cell).Count(p => set.Contains(((int)Math.Floor((p.X + sh.X) / cell), (int)Math.Floor((p.Y + sh.Y) / cell))));
        }
        Vec2 Search(IEnumerable<Vec2> starts, double cell, double range, double step)
        {
            var set = Occupied(refLines, cell);
            var pts = Samples(lines, cell);
            Vec2 best = guess; int bestScore = -1;
            foreach (var g in starts)
                for (double dx = -range; dx <= range + 1e-6; dx += step)
                    for (double dy = -range; dy <= range + 1e-6; dy += step)
                    {
                        var sh = g + new Vec2(dx, dy);
                        int sc = 0;
                        foreach (var p in pts) if (set.Contains(((int)Math.Floor((p.X + sh.X) / cell), (int)Math.Floor((p.Y + sh.Y) / cell)))) sc++;
                        if (sc > bestScore) { bestScore = sc; best = sh; }
                    }
            return best;
        }
        // two kinds of title (plan titles, a row of floor names) agreeing on the shift: the floors are laid out
        // on a regular grid, so look only there (a small floor fits the reference in many wrong places too)
        bool agree = titleGuesses.Count >= 2 && titleGuesses.All(g => titleGuesses.All(h => (g - h).Length <= 3000));
        var coarse = agree ? Search(titleGuesses, 500, 1500, 250) : Search(guesses, 500, 6000, 500);
        // a title shift (with its own small search) that fits nearly as well wins: two plans titled alike sit alike
        int top = Fit(coarse, 500);
        foreach (var g in agree ? Enumerable.Empty<Vec2>() : titleGuesses.Skip(1))
        {
            var near = Search(new[] { g }, 500, 1000, 250);
            if (Fit(near, 500) >= 0.85 * top) { coarse = near; break; }
        }
        var mid = Search(new[] { coarse }, 200, 600, 100);
        var fine = Search(new[] { mid }, 50, 150, 10);
        // on a regular sheet grid the titles give the shift exactly: kept when it fits as well
        if (agree)
            foreach (var g in titleGuesses)
                if ((g - fine).Length <= 500 && Fit(g, 50) >= 0.9 * Fit(fine, 50)) return g;
        return fine;
    }

    static List<Vec2> Samples(IEnumerable<Segment2> lines, double step)
    {
        var pts = new List<Vec2>();
        foreach (var l in lines)
        {
            int n = Math.Max(1, (int)(l.Length / step));
            for (int k = 0; k <= n; k++) pts.Add(l.A + (l.B - l.A) * ((double)k / n));
        }
        return pts;
    }

    static SurveyDrawing Part(SurveyDrawing d, Box b, Vec2 shift, string name, string code)
    {
        Segment2 M(Segment2 s) => s with { A = s.A + shift, B = s.B + shift };
        Arc2 A(Arc2 a) => a with { Center = a.Center + shift };
        bool In(Segment2 s) => b.Holds(s.Mid);
        return new SurveyDrawing
        {
            Path = d.Path,
            Name = new DrawingName(name, code),
            MmPerUnit = d.MmPerUnit,
            UnitsNote = d.UnitsNote,
            WallLines = d.WallLines.Where(In).Select(M).ToList(),
            DoorBlockLines = d.DoorBlockLines.Where(In).Select(M).ToList(),
            HiddenLines = d.HiddenLines.Where(In).Select(M).ToList(),
            OpeningLines = d.OpeningLines.Where(In).Select(M).ToList(),
            WallArcs = d.WallArcs.Where(a => b.Holds(a.Center)).Select(A).ToList(),
            SmallWallArcs = d.SmallWallArcs.Where(a => b.Holds(a.Center)).Select(A).ToList(),
            DoorArcs = d.DoorArcs.Where(a => b.Holds(a.Center)).Select(A).ToList(),
            Texts = d.Texts.Where(t => b.Holds(t.Position, 500)).Select(t => t with { Position = t.Position + shift }).ToList(),
            FeatureLines = d.FeatureLines.Where(In).Select(M).ToList(),
            LayerCounts = d.LayerCounts,
        };
    }

    static SurveyDrawing Rest(SurveyDrawing d, List<Box> plans)
    {
        bool Out(Vec2 p) => !plans.Any(b => b.Holds(p, 2000));
        return new SurveyDrawing
        {
            Path = d.Path, Name = d.Name, MmPerUnit = d.MmPerUnit, UnitsNote = d.UnitsNote,
            WallLines = d.WallLines.Where(s => Out(s.Mid)).ToList(),
            OpeningLines = d.OpeningLines.Where(s => Out(s.Mid)).ToList(),
            FeatureLines = d.FeatureLines.Where(s => Out(s.Mid)).ToList(),
            Texts = d.Texts.Where(t => Out(t.Position)).ToList(),
            LayerCounts = d.LayerCounts,
        };
    }
}
