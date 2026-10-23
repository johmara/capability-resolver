using System.Text.Json;
using CapabilityResolver.Analysis;
using CapabilityResolver.Engine;
using CapabilityResolver.Formats.Json;
using CapabilityResolver.Model;

namespace CapabilityResolver.Demo;

/// <summary>
/// Interactive view: every scope's feature tree with numbered features. Typing numbers toggles features, and after
/// each change the resolver's verdict is shown: what each scope derives on its own, which capabilities are satisfied
/// or missing (and which scope would be told), global rules, and the overall result.
/// </summary>
internal sealed class Explorer
{
    private const int Width = 100;

    private readonly DecentralizedSystem _system;
    private readonly IReadOnlyList<LoadedModel> _loaded;
    private readonly Analyzer _analyzer;
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly string? _scenarioDir;

    private readonly Dictionary<string, HashSet<string>> _manual = new();
    private readonly Dictionary<int, (string ModelId, string FeatureId)> _byNumber = new();
    private readonly Dictionary<(string ModelId, string FeatureId), int> _numberOf = new();
    private ProviderSuggestion? _lastSuggestion;

    public Explorer(
        DecentralizedSystem system, IReadOnlyList<LoadedModel> loaded, Analyzer analyzer,
        TextReader input, TextWriter output, string? scenarioDir)
    {
        _system = system;
        _loaded = loaded;
        _analyzer = analyzer;
        _in = input;
        _out = output;
        _scenarioDir = scenarioDir;

        foreach (var model in system.Models)
        {
            _manual[model.Id] = [];
            foreach (var id in model.FeatureOrder.Where(id => id != model.Root.Id))
            {
                var number = _byNumber.Count + 1;
                _byNumber[number] = (model.Id, id);
                _numberOf[(model.Id, id)] = number;
            }
        }
    }

    public void SetSelection(Configuration config, List<string> messages)
    {
        foreach (var model in _system.Models)
        {
            var candidate = config.For(model.Id).Where(id => id != model.Root.Id).ToHashSet();
            if (_analyzer.Propagate(model, candidate).Consistent) _manual[model.Id] = candidate;
            else messages.Add($"Ignored the picks for {model.Id}: they cannot be completed into a valid configuration of that scope.");
        }
    }

    public int Run(List<string> messages)
    {
        while (true)
        {
            Render(messages);
            messages.Clear();

            _out.Write("> ");
            var line = _in.ReadLine();
            if (line is null) return 0;

            var words = line.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;

            switch (words[0].ToLowerInvariant())
            {
                case "q" or "quit" or "exit":
                    return 0;
                case "h" or "help" or "?":
                    messages.AddRange(Help().Split('\n'));
                    break;
                case "reset":
                    foreach (var key in _manual.Keys.ToList()) _manual[key] = [];
                    messages.Add("Selection cleared.");
                    break;
                case "scenarios":
                    messages.AddRange(ScenarioList());
                    break;
                case "load":
                    Load(words, messages);
                    break;
                case "save":
                    Save(words, messages);
                    break;
                case "suggest":
                    Suggest(words, messages);
                    break;
                case "apply":
                    Apply(messages);
                    break;
                default:
                    if (words.All(w => int.TryParse(w, out _))) foreach (var w in words) Toggle(int.Parse(w), messages);
                    else messages.Add($"Unknown command \"{words[0]}\". Type help.");
                    break;
            }
        }
    }

    // ---- state ---------------------------------------------------------------------------------------------

    private sealed record State(
        IReadOnlyDictionary<string, ModelPropagation> Propagation,
        Configuration Effective,
        GlobalResult Result);

    private State Compute()
    {
        var propagation = _system.Models.ToDictionary(m => m.Id, m => _analyzer.Propagate(m, _manual[m.Id]));
        var effective = new Configuration(_system.Models.Select(m => KeyValuePair.Create(
            m.Id, (IReadOnlySet<string>)propagation[m.Id].Selection)));
        return new State(propagation, effective, GlobalResolver.Resolve(_system, effective));
    }

    // ---- commands ------------------------------------------------------------------------------------------

    private void Toggle(int number, List<string> messages)
    {
        if (!_byNumber.TryGetValue(number, out var key))
        {
            messages.Add($"There is no feature {number}.");
            return;
        }

        var model = _system.Model(key.ModelId);
        var manual = _manual[key.ModelId];
        var current = _analyzer.Propagate(model, manual);

        if (manual.Contains(key.FeatureId))
        {
            foreach (var id in Descendants(model[key.FeatureId]).Prepend(key.FeatureId)) manual.Remove(id);
            var after = _analyzer.Propagate(model, manual);
            messages.Add(after.Selection.Contains(key.FeatureId)
                ? $"Unpicked {key.FeatureId}, but {model.Id} still selects it: it is forced by the structure of {model.Id}."
                : $"Unpicked {key.FeatureId} in {model.Id}.");
        }
        else if (current.Selection.Contains(key.FeatureId))
        {
            messages.Add($"{key.FeatureId} is already selected because {model.Id} forces it (mandatory, or the only option left). Unpick what causes it first.");
        }
        else
        {
            var candidate = new HashSet<string>(manual) { key.FeatureId };
            var next = _analyzer.Propagate(model, candidate);
            if (!next.Consistent)
            {
                messages.Add($"Cannot select {key.FeatureId}: no valid configuration of {model.Id} contains it together with your other picks there.");
                return;
            }

            _manual[key.ModelId] = candidate;
            var forced = model.FeatureOrder.Where(id => next.Derived.Contains(id) && !current.Selection.Contains(id)).ToList();
            messages.Add($"Picked {key.FeatureId} in {model.Id}." + (forced.Count > 0 ? $" {model.Id} also selects: {string.Join(", ", forced)}." : ""));
        }
    }

    private void Suggest(string[] words, List<string> messages)
    {
        if (words.Length < 2)
        {
            messages.Add("Usage: suggest <capability>   (names are listed under Capabilities)");
            return;
        }

        var capability = words[1];
        var state = Compute();
        _lastSuggestion = null;

        if (state.Result.Ledger.ProvidersOf(capability) is { Count: > 0 } providers)
        {
            messages.Add($"\"{capability}\" is already provided by {string.Join(", ", providers)}; any remaining problem is elsewhere (see the resolver).");
            return;
        }

        var suggestion = _analyzer.SuggestProviders(_system, state.Effective, capability);

        if (suggestion is null)
        {
            var exists = _system.Models.Any(m => m.Features.Any(f => f.Provides.Contains(capability)));
            messages.Add(exists
                ? $"\"{capability}\" cannot be provided without breaking your current picks, a scope's own constraints, or a global rule."
                : $"No feature in any scope provides \"{capability}\".");
        }
        else if (suggestion.Add.Count == 0)
        {
            messages.Add($"\"{capability}\" is already provided; nothing to add.");
        }
        else
        {
            _lastSuggestion = suggestion;
            var adds = suggestion.Add.Select(a => $"{_numberOf[(a.ModelId, a.FeatureId)]} {a.FeatureId} ({a.ModelId})");
            messages.Add($"Fewest features to add for \"{capability}\" (whole system stays valid): {string.Join(", ", adds)}.");
            messages.Add($"Scopes that would be notified: {string.Join(", ", suggestion.NotifyModels)}. Type apply to pick them.");
        }
    }

    private void Apply(List<string> messages)
    {
        if (_lastSuggestion is null)
        {
            messages.Add("Nothing to apply. Run suggest <capability> first.");
            return;
        }

        foreach (var add in _lastSuggestion.Add) _manual[add.ModelId].Add(add.FeatureId);
        messages.Add($"Picked: {string.Join(", ", _lastSuggestion.Add)}.");
        _lastSuggestion = null;
    }

    private void Load(string[] words, List<string> messages)
    {
        if (words.Length < 2)
        {
            messages.Add("Usage: load <scenario name or config file>");
            messages.AddRange(ScenarioList());
            return;
        }

        var path = ResolveScenario(words[1]);
        if (path is null)
        {
            messages.Add($"No scenario or file called \"{words[1]}\".");
            messages.AddRange(ScenarioList());
            return;
        }

        try
        {
            var config = Cli.LoadConfig(_system, path);
            foreach (var key in _manual.Keys.ToList()) _manual[key] = [];
            messages.Add($"Loaded {Path.GetFileName(path)}.");
            SetSelection(config, messages);
        }
        catch (Exception e) when (e is UsageException or Loading.ModelFormatException or IOException)
        {
            messages.Add($"Could not load: {e.Message}");
        }
    }

    private void Save(string[] words, List<string> messages)
    {
        if (words.Length < 2)
        {
            messages.Add("Usage: save <file.json>");
            return;
        }

        var content = _system.Models.ToDictionary(
            m => m.Id,
            m => m.FeatureOrder.Where(_manual[m.Id].Contains).ToList());
        File.WriteAllText(words[1], JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true }));
        messages.Add($"Saved your picks to {words[1]} (use it with check --config, or load it here).");
    }

    private string? ResolveScenario(string name)
    {
        if (File.Exists(name)) return name;
        if (_scenarioDir is null) return null;

        return new[] { ".json", ".yaml", ".yml" }.Select(ext => Path.Combine(_scenarioDir, name + ext)).FirstOrDefault(File.Exists);
    }

    private List<string> ScenarioList()
    {
        var files = _scenarioDir is null
            ? []
            : Directory.EnumerateFiles(_scenarioDir).Where(f => Path.GetExtension(f) is ".json" or ".yaml" or ".yml").Order().ToList();
        return files.Count == 0
            ? ["No scenarios found. You can load any config file by path."]
            : ["Scenarios (type load <name>): " + string.Join(", ", files.Select(Path.GetFileNameWithoutExtension))];
    }

    private static string Help() => """
        Type feature numbers to pick or unpick them, e.g.  3   or  3 7 12
          picking a feature also selects what its own scope forces (mandatory parents and children, forced choices)
          unpicking a feature unpicks the picks below it
        Other commands:
          suggest <capability>   cheapest features to add so that capability is provided; apply picks them
          load <scenario>        replace your picks with a scenario (scenarios lists them) or a config file
          save <file.json>       write your picks to a file
          reset                  clear all picks
          quit
        Markers:  [x] picked by you   [+] selected because its scope forces it   [ ] not selected   [-] no longer possible
        """;

    // ---- rendering -----------------------------------------------------------------------------------------

    private void Render(List<string> messages)
    {
        var state = Compute();

        if (_out == Console.Out && !Console.IsOutputRedirected) Console.Clear();
        else _out.WriteLine(new string('=', Width));

        _out.WriteLine($"FEATURE MODELS  ({_system.Models.Count} scopes; each is owned and configured on its own)");
        _out.WriteLine("Markers: [x] picked by you   [+] forced by its own scope   [ ] not selected   [-] no longer possible");

        foreach (var loaded in _loaded)
        {
            var model = loaded.Model;
            _out.WriteLine();
            _out.WriteLine($"{model.Id}   [{loaded.Language}: {Path.GetFileName(loaded.File)}]");
            PrintFeature(model, model.Root, 0, state);
        }

        _out.WriteLine();
        PrintResolver(state);

        _out.WriteLine();
        foreach (var message in messages) _out.WriteLine($"* {message}");
        _out.WriteLine("Type feature numbers to toggle, or: suggest <capability> | load <scenario> | reset | help | quit");
    }

    private void PrintFeature(FeatureModel model, Feature feature, int depth, State state)
    {
        var propagation = state.Propagation[model.Id];
        var isRoot = feature.Id == model.Root.Id;
        var selected = state.Effective.For(model.Id).Contains(feature.Id) || isRoot;

        var box = isRoot ? "[+]"
            : _manual[model.Id].Contains(feature.Id) ? "[x]"
            : selected ? "[+]"
            : propagation.Dead.Contains(feature.Id) ? "[-]"
            : "[ ]";
        var number = isRoot ? "" : _numberOf[(model.Id, feature.Id)].ToString();

        var requires = feature.Requires.Order().Select(c =>
            selected ? $"{c} [{(state.Result.Ledger.ProvidersOf(c).Count > 0 ? "ok" : "MISSING")}]" : c).ToList();
        var notes = new List<string>();
        if (requires.Count > 0) notes.Add($"requires {string.Join(", ", requires)}");
        if (feature.Provides.Count > 0) notes.Add($"provides {string.Join(", ", feature.Provides.Order())}");

        var pad = new string(' ', depth * 2);
        var label = feature.Name == feature.Id ? feature.Id : $"{feature.Id} (\"{feature.Name}\")";
        _out.WriteLine($"{number,4}  {pad}{box} {label}{(notes.Count > 0 ? "   " + string.Join("   ", notes) : "")}");

        foreach (var group in feature.Groups)
        {
            _out.WriteLine($"      {pad}  {GroupLabel(group)}:");
            foreach (var child in group.Children) PrintFeature(model, child, depth + 2, state);
        }
    }

    private static string GroupLabel(Group group) => group.Kind switch
    {
        GroupKind.Mandatory => "mandatory",
        GroupKind.Optional => "optional",
        GroupKind.Or => "choose at least one",
        GroupKind.Alternative => "choose exactly one",
        _ => $"choose between {group.Min} and {(group.Max == int.MaxValue ? "all" : group.Max)}",
    };

    private void PrintResolver(State state)
    {
        var result = state.Result;
        _out.WriteLine(new string('-', Width));
        _out.WriteLine("RESOLVER   (combines the scopes: they only meet through capabilities)");

        _out.WriteLine();
        _out.WriteLine("Each scope on its own:");
        foreach (var model in _system.Models)
        {
            var local = result.LocalResults[model.Id];
            _out.WriteLine($"  {model.Id}: {(local.Valid ? "valid" : "INVALID")}");
            foreach (var violation in local.Violations) _out.WriteLine($"      {violation.Detail}");
        }

        _out.WriteLine();
        _out.WriteLine("Capabilities (what scopes require from each other):");
        var catalog = _system.Models.SelectMany(m => m.Features.SelectMany(f => f.Provides.Concat(f.Requires))).Distinct().Order();
        foreach (var capability in catalog)
        {
            var requiredBy = result.Ledger.Required.GetValueOrDefault(capability) ?? [];
            var providedBy = result.Ledger.ProvidersOf(capability);
            var status = requiredBy.Count == 0
                ? (providedBy.Count > 0 ? "provided, not needed" : "not in use")
                : providedBy.Count > 0 ? "satisfied" : "MISSING";
            _out.WriteLine($"  {capability,-22} {status}");
            if (requiredBy.Count > 0) _out.WriteLine($"      required by {string.Join(", ", requiredBy)}");
            if (providedBy.Count > 0) _out.WriteLine($"      provided by {string.Join(", ", providedBy)}");
        }

        foreach (var missing in result.Missing.Where(m => m.Diagnosis == MissingDiagnosis.Unprovisioned))
        {
            var providers = string.Join(", ", missing.PotentialProviders.Select(p => p.FeatureId));
            _out.WriteLine($"  -> {missing.Capability} is missing: tell {string.Join(", ", missing.NotifyModels)} only (it can provide it via {providers}). Try: suggest {missing.Capability}");
        }
        foreach (var missing in result.Missing.Where(m => m.Diagnosis == MissingDiagnosis.Unprovidable))
        {
            _out.WriteLine($"  -> {missing.Capability} is required but no scope provides it at all (a gap in the models).");
        }

        _out.WriteLine();
        _out.WriteLine("Global rules (direct constraints between scopes, phrased over capabilities):");
        if (_system.Rules.Count == 0) _out.WriteLine("  none");
        foreach (var rule in _system.Rules)
        {
            var violation = result.RuleViolations.FirstOrDefault(v => v.Rule == rule);
            _out.WriteLine($"  {rule.Notation}: {(violation is null ? "ok" : "VIOLATED")}");
            if (violation is not null)
            {
                _out.WriteLine($"      {rule.Rationale}");
                _out.WriteLine($"      caused by {string.Join(", ", violation.InvolvedProviders)}");
            }
        }

        var problems = result.LocalResults.Values.Sum(r => r.Violations.Count) + result.Missing.Count + result.RuleViolations.Count;
        _out.WriteLine();
        _out.WriteLine(result.Valid
            ? "RESULT: VALID  (every scope is valid, every required capability is provided, no rule is violated)"
            : $"RESULT: INVALID  ({problems} problem{(problems == 1 ? "" : "s")} above)");
    }

    private static IEnumerable<string> Descendants(Feature feature) =>
        feature.Children.SelectMany(c => Descendants(c).Prepend(c.Id));
}
