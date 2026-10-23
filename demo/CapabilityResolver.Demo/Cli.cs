using CapabilityResolver.Analysis;
using CapabilityResolver.Engine;
using CapabilityResolver.Formats.Clafer;
using CapabilityResolver.Formats.Json;
using CapabilityResolver.Formats.Uvl;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;
using CapabilityResolver.Solvers.OrTools;

namespace CapabilityResolver.Demo;

/// <summary>A model together with the file and language it was read from.</summary>
internal sealed record LoadedModel(FeatureModel Model, string File, string Language);

internal static class Cli
{
    private const string Usage = """
        fmdemo: try capability resolution across decentralized feature models.

        Usage:
          fmdemo explore <dir> [--config <file>]                interactive: see the models, toggle features, watch the resolver
          fmdemo models  <dir>                                  list the scopes found and their feature trees
          fmdemo check   <dir> --config <file>                  resolve a configuration across all scopes
          fmdemo complete <dir> --config <file>                 can the selection be completed? what does it force?
          fmdemo suggest <dir> --config <file> --capability <c> cheapest way to provide a capability
          fmdemo analyze <dir>                                  satisfiability, core and dead features, capabilities

        <dir> is searched recursively for model files:
          *.fm.json, *.fm.yaml, *.fm.yml (JSON/YAML), *.uvl (UVL), *.cfr (Clafer).
        Global rules are read from <dir>/rules.json|yaml|yml, or from --rules <file>.
        A configuration file maps model ids to the features selected by hand (JSON or YAML).
        check applies propagation inside each scope first (mandatory parents and children, forced
        choices), like the companion site; add --exact to resolve the selection exactly as written.
        Exit code: 0 valid / ok, 1 invalid, 2 usage or file error.
        """;

    private static readonly (IModelReader Reader, string Language)[] Readers =
    [
        (new JsonModelReader(), "JSON"),
        (new YamlModelReader(), "YAML"),
        (new UvlModelReader(), "UVL"),
        (new ClaferModelReader(), "Clafer"),
    ];

    private static readonly Analyzer Analyzer = new(OrToolsSolver.Create);

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length < 2 || args[0] is "-h" or "--help" or "help")
            {
                (args.Length == 0 || args[0] is "-h" or "--help" or "help" ? output : error).WriteLine(Usage);
                return args.Length == 0 || args[0] is "-h" or "--help" or "help" ? 0 : 2;
            }

            var options = Options.Parse(args[2..]);
            var dir = args[1];
            if (!Directory.Exists(dir)) return Fail(error, $"directory not found: {dir}");

            var (loaded, problems) = Load(dir);
            if (args[0] == "models") return Models(dir, loaded, problems, output);
            if (problems.Count > 0)
            {
                foreach (var problem in problems) error.WriteLine($"error: {problem}");
                return 2;
            }
            if (loaded.Count == 0) return Fail(error, $"no model files found under {dir}");

            var system = new DecentralizedSystem(loaded.Select(l => l.Model).ToList(), LoadRules(dir, options));

            return args[0] switch
            {
                "explore" => Explore(dir, loaded, system, options, input, output),
                "check" => Check(system, RequireConfig(system, options), options.Exact, output),
                "complete" => Complete(system, RequireConfig(system, options), output),
                "suggest" => Suggest(system, RequireConfig(system, options), options.Capability ?? throw new UsageException("suggest needs --capability <name>."), output),
                "analyze" => AnalyzeSystem(system, output),
                var other => throw new UsageException($"unknown command \"{other}\"."),
            };
        }
        catch (Exception e) when (e is UsageException or ModelFormatException or FormatException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Fail(error, e.Message);
        }
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine($"error: {message}");
        return 2;
    }

    private static (List<LoadedModel> Loaded, List<string> Problems) Load(string dir)
    {
        var loaded = new List<LoadedModel>();
        var problems = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var (reader, language) = Readers.FirstOrDefault(r => r.Reader.CanRead(file));
            if (reader is null) continue;

            try
            {
                loaded.Add(new LoadedModel(reader.Read(File.ReadAllText(file), file), file, language));
            }
            catch (ModelFormatException e)
            {
                problems.Add(e.Message);
            }
        }

        var duplicate = loaded.GroupBy(l => l.Model.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            problems.Add($"model id \"{duplicate.Key}\" is defined in more than one file: {string.Join(", ", duplicate.Select(d => d.File))}");
        }
        return (loaded, problems);
    }

    private static IReadOnlyList<GlobalRule> LoadRules(string dir, Options options)
    {
        var file = options.Rules
            ?? new[] { "rules.json", "rules.yaml", "rules.yml" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
        return file is null ? [] : RulesReader.Load(file);
    }

    private static Configuration RequireConfig(DecentralizedSystem system, Options options) =>
        LoadConfig(system, options.Config ?? throw new UsageException("this command needs --config <file>."));

    /// <summary>Reads a configuration file and checks every model id and feature in it exists.</summary>
    internal static Configuration LoadConfig(DecentralizedSystem system, string path)
    {
        var config = ConfigurationReader.Load(path);

        var known = system.Models.Select(m => m.Id).ToHashSet();

        // A typo in a model id would otherwise silently select nothing.
        var unknownModels = config.ModelIds.Where(id => !known.Contains(id)).ToList();
        if (unknownModels.Count > 0)
        {
            throw new UsageException($"{path}: unknown model id(s) {string.Join(", ", unknownModels.Select(i => $"\"{i}\""))}; known: {string.Join(", ", known.Order())}.");
        }

        foreach (var model in system.Models)
        {
            var unknownFeatures = config.For(model.Id).Where(f => !model.Contains(f)).ToList();
            if (unknownFeatures.Count > 0)
            {
                throw new UsageException($"{path}: model \"{model.Id}\" has no feature(s) {string.Join(", ", unknownFeatures.Select(f => $"\"{f}\""))}.");
            }
        }
        return config;
    }

    // ---- explore ------------------------------------------------------------------------------------------

    private static int Explore(string dir, List<LoadedModel> loaded, DecentralizedSystem system, Options options, TextReader input, TextWriter output)
    {
        var scenarios = options.Configs ?? Path.GetFullPath(Path.Combine(dir, "..", "..", "configs"));
        var explorer = new Explorer(system, loaded, Analyzer, input, output, Directory.Exists(scenarios) ? scenarios : null);

        var messages = new List<string> { "Start by typing a feature number, or  load missing-internet  (type scenarios to list them, help for more)." };
        if (options.Config is not null)
        {
            messages.Clear();
            explorer.SetSelection(LoadConfig(system, options.Config), messages);
            messages.Insert(0, $"Loaded {Path.GetFileName(options.Config)}.");
        }
        return explorer.Run(messages);
    }

    // ---- models -------------------------------------------------------------------------------------------

    private static int Models(string dir, List<LoadedModel> loaded, List<string> problems, TextWriter output)
    {
        output.WriteLine($"{loaded.Count} scope(s) under {dir}");
        foreach (var l in loaded)
        {
            var model = l.Model;
            output.WriteLine();
            output.WriteLine($"{model.Id}  [{l.Language}: {Path.GetFileName(l.File)}]");
            PrintFeature(model.Root, 1, output);
            foreach (var constraint in model.Constraints) output.WriteLine($"  constraint: {constraint}");
        }

        if (problems.Count == 0) return 0;

        output.WriteLine();
        output.WriteLine($"{problems.Count} file(s) could not be read:");
        foreach (var problem in problems) output.WriteLine($"  {problem}");
        return 2;
    }

    private static void PrintFeature(Feature feature, int depth, TextWriter output)
    {
        var pad = new string(' ', depth * 2);
        var label = feature.Name == feature.Id ? feature.Id : $"{feature.Id} (\"{feature.Name}\")";
        if (feature.IsAbstract) label += " [abstract]";
        output.WriteLine($"{pad}{label}{Capabilities(feature)}");

        foreach (var group in feature.Groups)
        {
            output.WriteLine($"{pad}  {GroupLabel(group)}:");
            foreach (var child in group.Children) PrintFeature(child, depth + 2, output);
        }
    }

    private static string Capabilities(Feature feature) =>
        (feature.Requires.Count > 0 ? $"  requires {string.Join(", ", feature.Requires.Order())}" : "")
        + (feature.Provides.Count > 0 ? $"  provides {string.Join(", ", feature.Provides.Order())}" : "");

    private static string GroupLabel(Group group) => group.Kind switch
    {
        GroupKind.Mandatory => "mandatory",
        GroupKind.Optional => "optional",
        GroupKind.Or => "or (at least one)",
        GroupKind.Alternative => "alternative (exactly one)",
        _ => $"[{group.Min}..{(group.Max == int.MaxValue ? "*" : group.Max)}] (between {group.Min} and {(group.Max == int.MaxValue ? "all" : group.Max)})",
    };

    // ---- check --------------------------------------------------------------------------------------------

    private static int Check(DecentralizedSystem system, Configuration manual, bool exact, TextWriter output)
    {
        var effective = manual;
        output.WriteLine(exact ? "Selection (exactly as written):" : "Selection (propagated inside each scope):");

        if (!exact)
        {
            var (propagated, byModel) = PropagateWithinScopes(system, manual);
            effective = propagated;
            foreach (var model in system.Models)
            {
                var propagation = byModel[model.Id];
                PrintSelection(model, effective.For(model.Id), propagation.Derived, output);
                if (!propagation.Consistent) output.WriteLine("    this scope's selection cannot be completed into a valid configuration (shown as written)");
            }
        }
        else
        {
            foreach (var model in system.Models) PrintSelection(model, manual.For(model.Id), new HashSet<string>(), output);
        }

        var result = GlobalResolver.Resolve(system, effective);

        output.WriteLine();
        output.WriteLine("Local validity:");
        foreach (var model in system.Models)
        {
            var local = result.LocalResults[model.Id];
            output.WriteLine($"  {model.Id}: {(local.Valid ? "valid" : "INVALID")}");
            foreach (var violation in local.Violations) output.WriteLine($"    - {violation.Detail}");
        }

        output.WriteLine();
        output.WriteLine("Capabilities:");
        var capabilities = result.Ledger.Required.Keys.Concat(result.Ledger.Provided.Keys).Distinct().Order().ToList();
        if (capabilities.Count == 0) output.WriteLine("  none required or provided");
        foreach (var capability in capabilities)
        {
            var requiredBy = result.Ledger.Required.GetValueOrDefault(capability) ?? [];
            var providedBy = result.Ledger.Provided.GetValueOrDefault(capability) ?? [];
            var status = requiredBy.Count == 0 ? "provided, not required" : providedBy.Count > 0 ? "satisfied" : "MISSING";
            output.WriteLine($"  {capability}: {status}");
            if (requiredBy.Count > 0) output.WriteLine($"    required by: {string.Join(", ", requiredBy)}");
            if (providedBy.Count > 0) output.WriteLine($"    provided by: {string.Join(", ", providedBy)}");
        }

        foreach (var missing in result.Missing)
        {
            output.WriteLine();
            output.WriteLine($"Missing \"{missing.Capability}\": {GlobalResolver.Explain(missing).Detail}");
            if (missing.Diagnosis == MissingDiagnosis.Unprovisioned)
            {
                output.WriteLine($"  notify only: {string.Join(", ", missing.NotifyModels)} (the scopes that can provide it)");
                output.WriteLine($"  could be provided by: {string.Join(", ", missing.PotentialProviders)}");
            }
        }

        output.WriteLine();
        output.WriteLine("Global rules:");
        if (system.Rules.Count == 0) output.WriteLine("  none");
        foreach (var rule in system.Rules)
        {
            var violation = result.RuleViolations.FirstOrDefault(v => v.Rule == rule);
            output.WriteLine($"  {rule.Notation}: {(violation is null ? "ok" : "VIOLATED")}");
            if (violation is not null)
            {
                output.WriteLine($"    {rule.Rationale}");
                output.WriteLine($"    involved: {string.Join(", ", violation.InvolvedProviders)}");
            }
        }

        output.WriteLine();
        output.WriteLine(result.Valid ? "Result: VALID" : "Result: INVALID");
        return result.Valid ? 0 : 1;
    }

    /// <summary>
    /// Completes each scope's hand-picked features using that scope's own model only (mandatory parents and
    /// children, forced choices). A scope whose selection cannot be completed is kept as written.
    /// </summary>
    private static (Configuration Effective, Dictionary<string, ModelPropagation> ByModel) PropagateWithinScopes(
        DecentralizedSystem system, Configuration manual)
    {
        var byModel = system.Models.ToDictionary(m => m.Id, m => Analyzer.Propagate(m, manual.For(m.Id)));
        var effective = new Configuration(system.Models.Select(m => KeyValuePair.Create(
            m.Id,
            byModel[m.Id].Consistent ? byModel[m.Id].Selection : manual.For(m.Id))));
        return (effective, byModel);
    }

    private static void PrintSelection(FeatureModel model, IReadOnlySet<string> selection, IReadOnlySet<string> derived, TextWriter output)
    {
        var chosen = model.FeatureOrder.Where(id => selection.Contains(id) && !derived.Contains(id)).ToList();
        var forced = model.FeatureOrder.Where(derived.Contains).ToList();
        output.WriteLine($"  {model.Id}: {string.Join(", ", chosen.Prepend(model.Root.Id).Distinct())}");
        if (forced.Count > 0) output.WriteLine($"    derived: {string.Join(", ", forced)}");
    }

    // ---- complete -----------------------------------------------------------------------------------------

    private static int Complete(DecentralizedSystem system, Configuration manual, TextWriter output)
    {
        var result = Analyzer.CanComplete(system, manual);
        if (!result.Consistent)
        {
            output.WriteLine("This selection cannot be completed into a globally valid configuration.");
            output.WriteLine("(Local structure, capability requirements or global rules make that impossible.)");
            return 1;
        }

        output.WriteLine("This selection can be completed. Across all scopes, together with capability requirements and global rules:");
        foreach (var model in system.Models)
        {
            var propagation = result.ByModel[model.Id];
            output.WriteLine();
            output.WriteLine($"  {model.Id}");
            output.WriteLine($"    selected or forced: {string.Join(", ", model.FeatureOrder.Where(propagation.Selection.Contains))}");
            if (propagation.Derived.Count > 0)
            {
                output.WriteLine($"    forced for you:     {string.Join(", ", model.FeatureOrder.Where(propagation.Derived.Contains))}");
            }
            if (propagation.Dead.Count > 0)
            {
                output.WriteLine($"    no longer possible: {string.Join(", ", model.FeatureOrder.Where(propagation.Dead.Contains))}");
            }
        }
        return 0;
    }

    // ---- suggest ------------------------------------------------------------------------------------------

    private static int Suggest(DecentralizedSystem system, Configuration manual, string capability, TextWriter output)
    {
        // Start from the scope-propagated selection, so features a scope forces itself are not reported as additions.
        var (effective, _) = PropagateWithinScopes(system, manual);
        var suggestion = Analyzer.SuggestProviders(system, effective, capability);
        if (suggestion is null)
        {
            var anyProvider = system.Models.Any(m => m.Features.Any(f => f.Provides.Contains(capability)));
            output.WriteLine(anyProvider
                ? $"\"{capability}\" cannot be provided without breaking the current selection, a local constraint or a global rule."
                : $"No feature in any scope provides \"{capability}\".");
            return 1;
        }

        if (suggestion.Add.Count == 0)
        {
            output.WriteLine($"\"{capability}\" is already provided by the completed selection; nothing to add.");
            return 0;
        }

        output.WriteLine($"Fewest features to add so that \"{capability}\" is provided and the whole system is valid:");
        foreach (var add in suggestion.Add) output.WriteLine($"  {add}");
        output.WriteLine();
        output.WriteLine($"Scopes to notify: {string.Join(", ", suggestion.NotifyModels)}");
        return 0;
    }

    // ---- analyze ------------------------------------------------------------------------------------------

    private static int AnalyzeSystem(DecentralizedSystem system, TextWriter output)
    {
        output.WriteLine("Per scope (each model analyzed on its own):");
        foreach (var model in system.Models)
        {
            var satisfiable = Analyzer.IsSatisfiable(model);
            output.WriteLine($"  {model.Id}: {(satisfiable ? "satisfiable" : "UNSATISFIABLE")}, {model.FeatureOrder.Count} features");
            if (!satisfiable) continue;
            output.WriteLine($"    core (always selected): {string.Join(", ", model.FeatureOrder.Where(Analyzer.CoreFeatures(model).Contains))}");
            var dead = Analyzer.DeadFeatures(model);
            output.WriteLine($"    dead (never selectable): {(dead.Count == 0 ? "none" : string.Join(", ", model.FeatureOrder.Where(dead.Contains)))}");
        }

        var decomposed = Analyzer.AnalyzeDecomposed(system);
        output.WriteLine();
        output.WriteLine($"Capability layer alone: {(decomposed.CapabilityLayerSatisfiable ? "satisfiable" : "UNSATISFIABLE")}");

        output.WriteLine();
        output.WriteLine("Capability catalog:");
        var providers = system.Models.SelectMany(m => m.Features.SelectMany(f => f.Provides.Select(c => (Capability: c, Ref: new FeatureRef(m.Id, f.Id))))).ToLookup(x => x.Capability, x => x.Ref);
        var requirers = system.Models.SelectMany(m => m.Features.SelectMany(f => f.Requires.Select(c => (Capability: c, Ref: new FeatureRef(m.Id, f.Id))))).ToLookup(x => x.Capability, x => x.Ref);
        var unprovidable = new List<string>();
        foreach (var capability in providers.Select(g => g.Key).Concat(requirers.Select(g => g.Key)).Distinct().Order())
        {
            output.WriteLine($"  {capability}");
            output.WriteLine($"    provided by: {(providers[capability].Any() ? string.Join(", ", providers[capability]) : "nobody")}");
            output.WriteLine($"    required by: {(requirers[capability].Any() ? string.Join(", ", requirers[capability]) : "nobody")}");
            if (!providers[capability].Any()) unprovidable.Add(capability);
        }

        if (unprovidable.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Governance gap: required but never provided: {string.Join(", ", unprovidable)}");
        }

        output.WriteLine();
        output.WriteLine("Global rules:");
        if (system.Rules.Count == 0) output.WriteLine("  none");
        foreach (var rule in system.Rules) output.WriteLine($"  {rule.Notation}  ({rule.Rationale})");

        return decomposed.AllSatisfiable ? 0 : 1;
    }
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed record Options(string? Config, string? Rules, string? Capability, bool Exact, string? Configs = null)
{
    public static Options Parse(string[] args)
    {
        string? config = null, rules = null, capability = null, configs = null;
        var exact = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--config": config = Value(); break;
                case "--rules": rules = Value(); break;
                case "--capability": capability = Value(); break;
                case "--configs": configs = Value(); break;
                case "--exact": exact = true; break;
                default: throw new UsageException($"unknown option \"{args[i]}\".");
            }
        }
        return new Options(config, rules, capability, exact, configs);
    }
}
