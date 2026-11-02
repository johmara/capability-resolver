namespace CapabilityResolver.Demo;

/// <summary>Menu of the sample model sets under <c>demo/</c>, so each language can be tried without remembering paths.</summary>
internal static class Launcher
{
    private static readonly (string Name, string Description)[] Sets =
    [
        ("phone/json", "the phone example, every scope written in JSON (*.fm.json)"),
        ("phone/yaml", "the phone example, every scope written in YAML (*.fm.yaml)"),
        ("phone/uvl", "the phone example, every scope written in UVL (*.uvl)"),
        ("phone/clafer", "the phone example, every scope written in Clafer (*.cfr)"),
        ("phone/mixed", "the phone example with one scope per language (UVL, Clafer, YAML, JSON)"),
        ("smart-home-uvl", "a smart home in three UVL scopes (lighting, security, network) with three global rules"),
        ("playground", "a small shop system in mixed languages, and the place for your own files"),
    ];

    /// <summary>Asks which set to open. Returns its directory, or null to quit.</summary>
    public static string? Choose(TextReader input, TextWriter output)
    {
        var root = FindDemoRoot();
        if (root is null)
        {
            output.WriteLine("Could not find the demo directory. Run from the repository root, or pass a directory: fmdemo explore <dir>");
            return null;
        }

        var available = Sets.Where(s => Directory.Exists(Path.Combine(root, s.Name))).ToList();
        while (true)
        {
            output.WriteLine();
            output.WriteLine("Pick a set of feature models to explore (type its number):");
            for (var i = 0; i < available.Count; i++) output.WriteLine($"  {i + 1}  {available[i].Name,-16} {available[i].Description}");
            output.WriteLine("  q  quit");
            output.Write("> ");

            var line = input.ReadLine()?.Trim();
            if (line is null or "q" or "quit" or "exit") return null;
            if (int.TryParse(line, out var n) && n >= 1 && n <= available.Count) return Path.Combine(root, available[n - 1].Name);
            output.WriteLine($"Not a choice: \"{line}\".");
        }
    }

    private static string? FindDemoRoot()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "demo", "phone"))) return Path.Combine(dir.FullName, "demo");
                if (Directory.Exists(Path.Combine(dir.FullName, "phone")) && Directory.Exists(Path.Combine(dir.FullName, "configs"))) return dir.FullName;
            }
        }
        return null;
    }
}
