using System.Globalization;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using CapabilityResolver.Formats.Uvl.Grammar;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;
using Group = CapabilityResolver.Model.Group;

namespace CapabilityResolver.Formats.Uvl;

/// <summary>
/// Reads <c>*.uvl</c> files with the official UVL ANTLR grammar (see <c>Grammar/</c>): the Boolean language
/// level with group cardinalities. Capabilities are list attributes on a feature:
/// <c>Email {requires ['InternetConnectivity'], provides ['mail.send']}</c>.
/// <list type="bullet">
/// <item><c>namespace</c> becomes the model id (the root feature's name if absent).</item>
/// <item><c>imports</c> are rejected: models may only be linked through capabilities.</item>
/// <item>The Arithmetic and Type levels (typed features, feature cardinality, numeric constraints) are rejected.</item>
/// <item>Attributes other than <c>requires</c>, <c>provides</c> and <c>abstract</c> are read and ignored.</item>
/// </list>
/// </summary>
public sealed class UvlModelReader : IModelReader
{
    public bool CanRead(string path) => path.EndsWith(".uvl", StringComparison.OrdinalIgnoreCase);

    public FeatureModel Read(string text, string source)
    {
        try
        {
            return UvlMapper.Map(Parse(text));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new ModelFormatException(source, e.Message, e);
        }
    }

    private static UVLCSharpParser.FeatureModelContext Parse(string text)
    {
        var errors = new ThrowingErrorListener();

        var lexer = new UVLCSharpLexer(new AntlrInputStream(text));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);

        var parser = new UVLCSharpParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errors);

        return parser.featureModel();
    }

    /// <summary>Turns the first syntax error into an exception instead of ANTLR's print-and-recover.</summary>
    private sealed class ThrowingErrorListener : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
    {
        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) =>
            throw new FormatException($"line {line}:{charPositionInLine + 1}: {msg}");

        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) =>
            throw new FormatException($"line {line}:{charPositionInLine + 1}: {msg}");
    }
}

/// <summary>Maps the UVL parse tree to the core model, rejecting what the model cannot express.</summary>
internal static partial class UvlMapper
{
    [GeneratedRegex(@"^\[(\d+)(?:\.\.(\d+|\*))?\]$")]
    private static partial Regex CardinalityText();

    public static FeatureModel Map(UVLCSharpParser.FeatureModelContext ctx)
    {
        if (ctx.imports() is { } imports)
        {
            throw Error(imports, "imports are not supported: models are linked only through capabilities.");
        }

        foreach (var line in ctx.includes()?.includeLine() ?? [])
        {
            CheckLanguageLevel(line.languageLevel());
        }

        if (ctx.features() is not { } features) throw new FormatException("no features section.");
        var root = MapFeature(features.feature());

        var constraints = (ctx.constraints()?.constraintLine() ?? [])
            .Select(line => MapConstraint(line.constraint()))
            .ToList();

        var id = ctx.@namespace() is { } ns ? Reference(ns.reference()) : root.Id;
        return new FeatureModel(id, root.Name, root, constraints, new Scope(id));
    }

    private static void CheckLanguageLevel(UVLCSharpParser.LanguageLevelContext level)
    {
        var supported = level.majorLevel().BOOLEAN_KEY() is not null
            && (level.minorLevel() is null || level.minorLevel().GROUP_CARDINALITY_KEY() is not null);
        if (!supported)
        {
            throw Error(level, $"language level \"{level.GetText()}\" is not supported; only Boolean (with group cardinality) is.");
        }
    }

    private static Feature MapFeature(UVLCSharpParser.FeatureContext f)
    {
        if (f.featureType() is not null) throw Error(f, "typed features (Type level) are not supported.");
        if (f.featureCardinality() is not null) throw Error(f, "feature cardinality is not supported.");

        var name = Reference(f.reference());
        var attributes = f.attributes() is { } a ? MapAttributes(a) : [];
        var groups = f.group().Select(MapGroup).ToList();

        return new Feature(
            name,
            name,
            groups,
            Capabilities(attributes, "requires", f),
            Capabilities(attributes, "provides", f),
            attributes.GetValueOrDefault("abstract") is true);
    }

    private static Group MapGroup(UVLCSharpParser.GroupContext g)
    {
        var (kind, spec, min, max) = g switch
        {
            UVLCSharpParser.OrGroupContext o => (GroupKind.Or, o.groupSpec(), 0, int.MaxValue),
            UVLCSharpParser.AlternativeGroupContext a => (GroupKind.Alternative, a.groupSpec(), 0, int.MaxValue),
            UVLCSharpParser.OptionalGroupContext o => (GroupKind.Optional, o.groupSpec(), 0, int.MaxValue),
            UVLCSharpParser.MandatoryGroupContext m => (GroupKind.Mandatory, m.groupSpec(), 0, int.MaxValue),
            UVLCSharpParser.CardinalityGroupContext c => CardinalityOf(c),
            _ => throw Error(g, "unknown group."),
        };
        return new Group(kind, spec.feature().Select(MapFeature).ToList(), min, max);
    }

    private static (GroupKind, UVLCSharpParser.GroupSpecContext, int, int) CardinalityOf(UVLCSharpParser.CardinalityGroupContext c)
    {
        var text = c.CARDINALITY().GetText();
        var m = CardinalityText().Match(text);
        var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var max = !m.Groups[2].Success ? min : m.Groups[2].Value == "*" ? int.MaxValue : int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (max < min) throw Error(c, $"cardinality {text} has max below min.");
        return (GroupKind.Cardinality, c.groupSpec(), min, max);
    }

    private static Dictionary<string, object?> MapAttributes(UVLCSharpParser.AttributesContext attributes)
    {
        var result = new Dictionary<string, object?>();
        foreach (var attribute in attributes.attribute())
        {
            // Constraint attributes are not interpreted.
            if (attribute.valueAttribute() is not { } valueAttribute) continue;

            var key = IdText(valueAttribute.key().id());
            result[key] = valueAttribute.value() is { } value ? MapValue(value) : true; // a bare key is a flag, e.g. {abstract}
        }
        return result;
    }

    private static object? MapValue(UVLCSharpParser.ValueContext v)
    {
        if (v.BOOLEAN() is { } b) return bool.Parse(b.GetText());
        if (v.INTEGER() is { } i) return double.Parse(i.GetText(), CultureInfo.InvariantCulture);
        if (v.FLOAT() is { } f) return double.Parse(f.GetText(), CultureInfo.InvariantCulture);
        if (v.STRING() is { } s) return s.GetText()[1..^1];
        if (v.attributes() is { } a) return MapAttributes(a);
        return v.vector().value().Select(MapValue).ToList();
    }

    private static HashSet<string> Capabilities(Dictionary<string, object?> attributes, string key, UVLCSharpParser.FeatureContext f)
    {
        return attributes.GetValueOrDefault(key) switch
        {
            null => [],
            string s => [s],
            List<object?> list when list.All(v => v is string) => list.Cast<string>().ToHashSet(),
            _ => throw Error(f, $"\"{key}\" must be a string or a list of strings."),
        };
    }

    private static Expr MapConstraint(UVLCSharpParser.ConstraintContext c) => c switch
    {
        UVLCSharpParser.LiteralConstraintContext l => Expr.Ref(Reference(l.reference())),
        UVLCSharpParser.ParenthesisConstraintContext p => MapConstraint(p.constraint()),
        UVLCSharpParser.NotConstraintContext n => Expr.Not(MapConstraint(n.constraint())),
        UVLCSharpParser.AndConstraintContext a => Expr.And(MapConstraint(a.constraint(0)), MapConstraint(a.constraint(1))),
        UVLCSharpParser.OrConstraintContext o => Expr.Or(MapConstraint(o.constraint(0)), MapConstraint(o.constraint(1))),
        UVLCSharpParser.ImplicationConstraintContext i => Expr.Implies(MapConstraint(i.constraint(0)), MapConstraint(i.constraint(1))),
        UVLCSharpParser.EquivalenceConstraintContext e => Expr.Iff(MapConstraint(e.constraint(0)), MapConstraint(e.constraint(1))),
        UVLCSharpParser.EquationConstraintContext q => throw Error(q, "numeric constraints (Arithmetic level) are not supported."),
        _ => throw Error(c, "unknown constraint."),
    };

    private static string Reference(UVLCSharpParser.ReferenceContext r) => string.Join('.', r.id().Select(IdText));

    private static string IdText(UVLCSharpParser.IdContext id) =>
        id.ID_NOT_STRICT() is { } quoted ? quoted.GetText()[1..^1] : id.GetText();

    private static FormatException Error(ParserRuleContext ctx, string message) =>
        new($"line {ctx.Start.Line}: {message}");
}
