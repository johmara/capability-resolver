namespace CapabilityResolver.Solving;

/// <summary>
/// Boolean satisfiability backend. Variables are numbered from 1; a literal is a variable (positive)
/// or its negation (negative), as in DIMACS. Constraints are only ever added, never removed;
/// assumptions are per call.
/// </summary>
public interface ISolver : IDisposable
{
    int NewVar();

    void AddClause(params int[] literals);

    void AddAtMost(int k, IReadOnlyList<int> literals);

    /// <summary>At least <paramref name="k"/> literals hold; when <paramref name="onlyIf"/> is given, only if that literal holds.</summary>
    void AddAtLeast(int k, IReadOnlyList<int> literals, int? onlyIf = null);

    SolveResult Solve(IReadOnlyList<int> assumptions);

    /// <summary>The model with the fewest true <paramref name="objective"/> literals, or unsatisfiable.</summary>
    SolveResult Minimize(IReadOnlyList<int> objective, IReadOnlyList<int> assumptions);
}

/// <summary>Outcome of a solve. <see cref="Model"/> is indexed by variable number (index 0 is unused).</summary>
public sealed record SolveResult(bool Satisfiable, IReadOnlyList<bool> Model)
{
    public static readonly SolveResult Unsatisfiable = new(false, []);

    public bool this[int variable] => Model[variable];
}
