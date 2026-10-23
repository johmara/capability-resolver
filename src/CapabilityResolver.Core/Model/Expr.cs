namespace CapabilityResolver.Model;

/// <summary>Propositional cross-tree constraint over the features of one model.</summary>
public abstract record Expr
{
    public static Expr Ref(string featureId) => new FeatureRefExpr(featureId);
    public static Expr Not(Expr e) => new NotExpr(e);
    public static Expr And(Expr l, Expr r) => new AndExpr(l, r);
    public static Expr Or(Expr l, Expr r) => new OrExpr(l, r);
    public static Expr Implies(Expr l, Expr r) => new ImpliesExpr(l, r);
    public static Expr Iff(Expr l, Expr r) => new IffExpr(l, r);

    public bool Evaluate(Func<string, bool> isSelected) => this switch
    {
        FeatureRefExpr r => isSelected(r.FeatureId),
        NotExpr n => !n.Operand.Evaluate(isSelected),
        AndExpr a => a.Left.Evaluate(isSelected) && a.Right.Evaluate(isSelected),
        OrExpr o => o.Left.Evaluate(isSelected) || o.Right.Evaluate(isSelected),
        ImpliesExpr i => !i.Left.Evaluate(isSelected) || i.Right.Evaluate(isSelected),
        IffExpr f => f.Left.Evaluate(isSelected) == f.Right.Evaluate(isSelected),
        _ => throw new InvalidOperationException($"Unknown expression {GetType().Name}."),
    };

    public IEnumerable<string> Features() => this switch
    {
        FeatureRefExpr r => [r.FeatureId],
        NotExpr n => n.Operand.Features(),
        AndExpr a => a.Left.Features().Concat(a.Right.Features()),
        OrExpr o => o.Left.Features().Concat(o.Right.Features()),
        ImpliesExpr i => i.Left.Features().Concat(i.Right.Features()),
        IffExpr f => f.Left.Features().Concat(f.Right.Features()),
        _ => [],
    };

    /// <summary>Readable form, e.g. <c>(A =&gt; B)</c>, used in diagnostics.</summary>
    public sealed override string ToString() => this switch
    {
        FeatureRefExpr r => ExprParser.Quote(r.FeatureId),
        NotExpr n => $"!{n.Operand}",
        AndExpr a => $"({a.Left} & {a.Right})",
        OrExpr o => $"({o.Left} | {o.Right})",
        ImpliesExpr i => $"({i.Left} => {i.Right})",
        IffExpr f => $"({f.Left} <=> {f.Right})",
        _ => base.ToString()!,
    };
}

public sealed record FeatureRefExpr(string FeatureId) : Expr;
public sealed record NotExpr(Expr Operand) : Expr;
public sealed record AndExpr(Expr Left, Expr Right) : Expr;
public sealed record OrExpr(Expr Left, Expr Right) : Expr;
public sealed record ImpliesExpr(Expr Left, Expr Right) : Expr;
public sealed record IffExpr(Expr Left, Expr Right) : Expr;
