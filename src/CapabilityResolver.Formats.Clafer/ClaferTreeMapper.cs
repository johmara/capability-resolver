using System.Globalization;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using CapabilityResolver.Formats.Clafer.Grammar;
using CapabilityResolver.Model;

namespace CapabilityResolver.Formats.Clafer;

/// <summary>
/// Parses Clafer text with the ANTLR grammar generated from the official LBNF grammar (see <c>Grammar/</c>) and maps
/// the tree to the core model. Context class names are the LBNF constructor names of <c>clafer.cf</c>.
/// </summary>
internal static class ClaferTreeMapper
{
    public static FeatureModel Read(string text, string id)
    {
        var errors = new ThrowingErrorListener();

        var lexer = new ClaferCSharpLexer(new AntlrInputStream(text));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);

        var parser = new ClaferCSharpParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errors);

        // start_Module is `module EOF`: unlike module itself it rejects trailing text.
        return Map((ClaferCSharpParser.ModuleAltContext)parser.start_Module().module(), id);
    }

    private static FeatureModel Map(ClaferCSharpParser.ModuleAltContext module, string id)
    {
        var constraints = new List<Expr>();
        var roots = new List<Item>();

        foreach (var declaration in LeftList(module.listDeclaration(), l => l.listDeclaration(), l => l.declaration()))
        {
            if (declaration is ClaferCSharpParser.EnumDeclContext enumDecl)
            {
                throw Error(enumDecl, "enum declarations are not supported.");
            }

            switch (((ClaferCSharpParser.ElementDeclContext)declaration).element())
            {
                case ClaferCSharpParser.SubclaferContext clafer:
                    roots.Add(MapClafer(AsClafer(clafer.clafer()), constraints));
                    break;
                case ClaferCSharpParser.SubconstraintContext constraint:
                    constraints.AddRange(MapConstraint((ClaferCSharpParser.ConstraintAltContext)constraint.constraint()));
                    break;
                case var other:
                    throw Error(other, "only clafers and constraints are supported at the top level.");
            }
        }

        if (roots.Count == 0) throw new FormatException("no clafer found.");
        if (roots.Count > 1)
        {
            throw new FormatException(
                $"only one root clafer is supported, found {roots.Count}: {string.Join(", ", roots.Select(r => r.Feature.Name))}.");
        }

        var root = roots[0].Feature;
        return new FeatureModel(id, root.Name, root, constraints, new Scope(id));
    }

    /// <summary>The grammar's list rules are recursive: <c>listX: | listX x</c>. Returns the items in source order.</summary>
    private static List<TItem> LeftList<TList, TItem>(TList? list, Func<TList, TList?> rest, Func<TList, TItem?> item)
        where TList : class where TItem : class
    {
        var items = new List<TItem>();
        for (var l = list; l is not null; l = rest(l))
        {
            if (item(l) is { } i) items.Add(i);
        }
        items.Reverse();
        return items;
    }

    private static ClaferCSharpParser.ClaferAltContext AsClafer(ClaferCSharpParser.ClaferContext c) =>
        (ClaferCSharpParser.ClaferAltContext)c;

    /// <summary>A mapped clafer, and whether its instance cardinality makes it optional.</summary>
    private sealed record Item(Feature Feature, bool Optional);

    private static Item MapClafer(ClaferCSharpParser.ClaferAltContext c, List<Expr> constraints)
    {
        var name = c.PosIdent().GetText();
        if (LeftList(c.listTempModifier(), l => l.listTempModifier(), l => l.tempModifier()).Count > 0)
        {
            throw Error(c, "temporal modifiers are not supported.");
        }
        if (c.super_() is not ClaferCSharpParser.SuperEmptyContext) throw Error(c, "inheritance is not supported.");
        if (c.reference() is not ClaferCSharpParser.ReferenceEmptyContext) throw Error(c, "references are not supported (except provides/requires capability strings).");
        if (c.init() is not ClaferCSharpParser.InitEmptyContext) throw Error(c, "initializers are not supported.");
        if (c.transition() is not ClaferCSharpParser.TransitionEmptyContext) throw Error(c, "transitions are not supported.");

        var optional = InstanceCardinalityIsOptional(c);
        (GroupKind Kind, int Min, int Max)? group = c.gCard() is ClaferCSharpParser.GCardEmptyContext ? null : GroupOf(c.gCard());

        var children = new List<Item>();
        var requires = new HashSet<string>();
        var provides = new HashSet<string>();

        var elements = c.elements() is ClaferCSharpParser.ElementsListContext list
            ? LeftList(list.listElement(), l => l.listElement(), l => l.element())
            : [];

        foreach (var element in elements)
        {
            switch (element)
            {
                case ClaferCSharpParser.SubclaferContext sub when CapabilityOf(AsClafer(sub.clafer())) is var (isProvides, capability):
                    (isProvides ? provides : requires).Add(capability);
                    break;
                case ClaferCSharpParser.SubclaferContext sub:
                    children.Add(MapClafer(AsClafer(sub.clafer()), constraints));
                    break;
                case ClaferCSharpParser.SubconstraintContext constraint:
                    constraints.AddRange(MapConstraint((ClaferCSharpParser.ConstraintAltContext)constraint.constraint()));
                    break;
                default:
                    throw Error(element, "only clafers, constraints and provides/requires strings are supported inside a clafer.");
            }
        }

        if (group is not null && children.Count == 0) throw Error(c, $"group clafer \"{name}\" has no children.");

        var features = children.Select(i => i.Feature).ToList();
        List<Model.Group> groups;
        if (group is { } grp)
        {
            groups = [new Model.Group(grp.Kind, features, grp.Min, grp.Max)];
        }
        else
        {
            groups = [];
            var mandatory = children.Where(i => !i.Optional).Select(i => i.Feature).ToList();
            var optionalChildren = children.Where(i => i.Optional).Select(i => i.Feature).ToList();
            if (mandatory.Count > 0) groups.Add(new Model.Group(GroupKind.Mandatory, mandatory));
            if (optionalChildren.Count > 0) groups.Add(new Model.Group(GroupKind.Optional, optionalChildren));
        }

        var feature = new Feature(name, name, groups, requires, provides, c.abstract_() is ClaferCSharpParser.AbstractContext);
        return new Item(feature, optional);
    }

    /// <summary>Only 0..1 (optional) and 1 (mandatory) instance cardinalities describe a feature.</summary>
    private static bool InstanceCardinalityIsOptional(ClaferCSharpParser.ClaferAltContext c)
    {
        switch (c.card())
        {
            case ClaferCSharpParser.CardEmptyContext:
                return false;
            case ClaferCSharpParser.CardLoneContext:
                return true;
            case ClaferCSharpParser.CardSomeContext:
            case ClaferCSharpParser.CardAnyContext:
                throw Error(c.card(), $"multiple instances ('{c.card().GetText()}') are not supported.");
            case ClaferCSharpParser.CardNumContext num:
                var n = int.Parse(num.PosInteger().GetText(), CultureInfo.InvariantCulture);
                return CheckInstanceBounds(num, n, n);
            case ClaferCSharpParser.CardIntervalContext interval:
                var (min, max) = Bounds(interval.nCard());
                return CheckInstanceBounds(interval, min, max);
            default:
                throw Error(c.card(), "unknown cardinality.");
        }
    }

    private static bool CheckInstanceBounds(ParserRuleContext at, int min, int max)
    {
        if (min == 0 && max == 1) return true;
        if (min == 1 && max == 1) return false;
        throw Error(at, $"cardinality {at.GetText()} is not supported; use ?, 0..1 or 1.");
    }

    private static (int Min, int Max) Bounds(ClaferCSharpParser.NCardContext nCard)
    {
        var n = (ClaferCSharpParser.NCardAltContext)nCard;
        var min = int.Parse(n.PosInteger().GetText(), CultureInfo.InvariantCulture);
        var max = n.exInteger() is ClaferCSharpParser.ExIntegerNumContext num
            ? int.Parse(num.PosInteger().GetText(), CultureInfo.InvariantCulture)
            : int.MaxValue;
        if (max < min) throw Error(n, $"cardinality {n.GetText()} has max below min.");
        return (min, max);
    }

    private static (GroupKind Kind, int Min, int Max) GroupOf(ClaferCSharpParser.GCardContext g) => g switch
    {
        ClaferCSharpParser.GCardXorContext => (GroupKind.Alternative, 1, 1),
        ClaferCSharpParser.GCardOrContext => (GroupKind.Or, 1, int.MaxValue),
        ClaferCSharpParser.GCardMuxContext => (GroupKind.Cardinality, 0, 1),
        ClaferCSharpParser.GCardOptContext => (GroupKind.Optional, 0, int.MaxValue),
        ClaferCSharpParser.GCardIntervalContext i => (GroupKind.Cardinality, Bounds(i.nCard()).Min, Bounds(i.nCard()).Max),
        _ => throw Error(g, "unknown group cardinality."),
    };

    /// <summary>
    /// A capability line: <c>provides -> string = "cap"</c> or <c>requires -> string = "cap"</c>. Returns null for any
    /// other clafer. A clafer named provides/requires with a reference must match this shape exactly.
    /// </summary>
    private static (bool IsProvides, string Capability)? CapabilityOf(ClaferCSharpParser.ClaferAltContext c)
    {
        var name = c.PosIdent().GetText();
        if (name is not ("provides" or "requires") || c.reference() is ClaferCSharpParser.ReferenceEmptyContext) return null;

        var shapeOk =
            c.abstract_() is ClaferCSharpParser.AbstractEmptyContext
            && LeftList(c.listTempModifier(), l => l.listTempModifier(), l => l.tempModifier()).Count == 0
            && c.gCard() is ClaferCSharpParser.GCardEmptyContext
            && c.super_() is ClaferCSharpParser.SuperEmptyContext
            && c.card() is ClaferCSharpParser.CardEmptyContext
            && c.transition() is ClaferCSharpParser.TransitionEmptyContext
            && c.elements() is ClaferCSharpParser.ElementsEmptyContext
            && c.reference() is ClaferCSharpParser.ReferenceSetContext reference
            && Unwrap(reference.exp23()) is ClaferCSharpParser.ClaferIdContext type && type.GetText() == "string"
            && c.init() is ClaferCSharpParser.InitSomeContext init
            && init.initHow() is ClaferCSharpParser.InitConstantContext
            && Unwrap(init.exp()) is ClaferCSharpParser.EStrContext;
        if (!shapeOk) throw Error(c, $"\"{name}\" must be written as: {name} -> string = \"capability\".");

        var literal = ((ClaferCSharpParser.EStrContext)Unwrap(((ClaferCSharpParser.InitSomeContext)c.init()).exp())).PosString().GetText();
        return (name == "provides", Unescape(literal[1..^1]));
    }

    private static string Unescape(string s) =>
        s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");

    // ---- constraints ----

    private static IEnumerable<Expr> MapConstraint(ClaferCSharpParser.ConstraintAltContext c) =>
        LeftList(c.listExp(), l => l.listExp(), l => l.exp()).Select(MapExp);

    private static Expr MapExp(IParseTree node)
    {
        node = Unwrap(node);
        return node switch
        {
            ClaferCSharpParser.ClaferIdContext id => Expr.Ref(SingleName(id)),
            ClaferCSharpParser.ENegContext n => Expr.Not(MapExp(n.exp11())),
            ClaferCSharpParser.EAndContext a => Expr.And(MapExp(a.exp7()), MapExp(a.exp8())),
            ClaferCSharpParser.EOrContext o => Expr.Or(MapExp(o.exp5()), MapExp(o.exp6())),
            ClaferCSharpParser.EImpliesContext i => Expr.Implies(MapExp(i.exp4()), MapExp(i.exp5())),
            ClaferCSharpParser.EIffContext f => Expr.Iff(MapExp(f.exp3()), MapExp(f.exp4())),
            ClaferCSharpParser.EJoinContext j => MapJoin(j),
            ClaferCSharpParser.EQuantExpContext q => MapQuantified(q),
            ParserRuleContext other => throw Error(other, $"{Describe(other)} is not supported in constraints."),
            _ => throw new FormatException("unexpected node in constraint."),
        };
    }

    /// <summary>A dotted path such as <c>Shop.Card</c> or <c>this.Cart</c> refers to its last segment.</summary>
    private static Expr MapJoin(ClaferCSharpParser.EJoinContext j)
    {
        if (!IsPath(j.exp26())) throw Error(j, "navigation through anything but plain names is not supported in constraints.");
        return MapExp(j.exp27());
    }

    private static bool IsPath(IParseTree node) => Unwrap(node) switch
    {
        ClaferCSharpParser.ClaferIdContext => true,
        ClaferCSharpParser.EJoinContext j => IsPath(j.exp26()) && IsPath(j.exp27()),
        _ => false,
    };

    private static Expr MapQuantified(ClaferCSharpParser.EQuantExpContext q) => q.quant() switch
    {
        ClaferCSharpParser.QuantSomeContext => MapExp(q.exp20()),
        ClaferCSharpParser.QuantNoContext => Expr.Not(MapExp(q.exp20())),
        var other => throw Error(q, $"quantifier '{other.GetText()}' is not supported in constraints."),
    };

    private static string SingleName(ClaferCSharpParser.ClaferIdContext id)
    {
        var path = (ClaferCSharpParser.PathContext)id.name();
        var parts = path.listModId();
        if (parts.listModId() is not null) throw Error(id, $"path \"{id.GetText()}\" is not supported; use plain names.");
        return parts.modId().GetText();
    }

    /// <summary>
    /// Skips the pass-through alternatives (every <c>Coerce*</c> label): the levels of the precedence ladder and
    /// <c>( exp )</c>, whose single rule child is the expression to continue with.
    /// </summary>
    private static IParseTree Unwrap(IParseTree node)
    {
        while (node is ParserRuleContext && node.GetType().Name.StartsWith("Coerce", StringComparison.Ordinal))
        {
            node = Enumerable.Range(0, node.ChildCount).Select(node.GetChild).OfType<ParserRuleContext>().Single();
        }
        return node;
    }

    private static string Describe(ParserRuleContext ctx) => ctx switch
    {
        ClaferCSharpParser.EInContext or ClaferCSharpParser.ENinContext => "set membership",
        ClaferCSharpParser.EXorContext => "'xor'",
        ClaferCSharpParser.EAddContext or ClaferCSharpParser.ESubContext or ClaferCSharpParser.EMulContext
            or ClaferCSharpParser.EDivContext or ClaferCSharpParser.ERemContext or ClaferCSharpParser.EMinExpContext
            or ClaferCSharpParser.ELtContext or ClaferCSharpParser.EGtContext or ClaferCSharpParser.EEqContext
            or ClaferCSharpParser.ELteContext or ClaferCSharpParser.EGteContext or ClaferCSharpParser.ENeqContext
            or ClaferCSharpParser.EIntContext or ClaferCSharpParser.EDoubleContext or ClaferCSharpParser.ERealContext
            or ClaferCSharpParser.EStrContext or ClaferCSharpParser.ESumContext or ClaferCSharpParser.EProdContext
            or ClaferCSharpParser.ECardContext => "arithmetic and comparisons",
        ClaferCSharpParser.EDeclAllContext or ClaferCSharpParser.EDeclAllDisjContext or ClaferCSharpParser.EDeclQuantContext
            or ClaferCSharpParser.EDeclQuantDisjContext => "quantifiers over declarations",
        _ => $"this construct ({ctx.GetType().Name.Replace("Context", "")})",
    };

    private static FormatException Error(ParserRuleContext ctx, string message) =>
        new($"line {ctx.Start.Line}: {message}");

    /// <summary>Turns the first syntax error into an exception instead of ANTLR's print-and-recover.</summary>
    private sealed class ThrowingErrorListener : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
    {
        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) =>
            throw new FormatException($"line {line}:{charPositionInLine + 1}: {msg}");

        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) =>
            throw new FormatException($"line {line}:{charPositionInLine + 1}: {msg}");
    }
}
