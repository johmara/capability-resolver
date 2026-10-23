using CapabilityResolver.Solving;
using Google.OrTools.Sat;

namespace CapabilityResolver.Solvers.OrTools;

/// <summary>
/// <see cref="ISolver"/> on OR-Tools CP-SAT. One <see cref="CpModel"/> grows as constraints are added;
/// assumptions are set per solve. Single-threaded, so results are deterministic.
/// </summary>
public sealed class OrToolsSolver : ISolver
{
    private readonly CpModel _model = new();
    private readonly List<BoolVar> _vars = [null!]; // index 0 unused: variables are 1-based

    public static ISolver Create() => new OrToolsSolver();

    public int NewVar()
    {
        _vars.Add(_model.NewBoolVar($"v{_vars.Count}"));
        return _vars.Count - 1;
    }

    public void AddClause(params int[] literals) => _model.AddBoolOr(literals.Select(Lit).ToArray());

    public void AddAtMost(int k, IReadOnlyList<int> literals)
    {
        if (k >= literals.Count) return;
        _model.Add(LinearExpr.Sum(literals.Select(Lit)) <= k);
    }

    public void AddAtLeast(int k, IReadOnlyList<int> literals, int? onlyIf = null)
    {
        if (k <= 0) return;
        var constraint = _model.Add(LinearExpr.Sum(literals.Select(Lit)) >= k);
        if (onlyIf is { } condition) constraint.OnlyEnforceIf(Lit(condition));
    }

    public SolveResult Solve(IReadOnlyList<int> assumptions) => Run(assumptions, objective: null);

    public SolveResult Minimize(IReadOnlyList<int> objective, IReadOnlyList<int> assumptions) =>
        Run(assumptions, objective);

    private SolveResult Run(IReadOnlyList<int> assumptions, IReadOnlyList<int>? objective)
    {
        _model.ClearAssumptions();
        _model.AddAssumptions(assumptions.Select(Lit).ToArray());

        _model.Model.Objective = null;
        if (objective is not null) _model.Minimize(LinearExpr.Sum(objective.Select(Lit)));

        var solver = new CpSolver { StringParameters = "num_workers:1" };
        var status = solver.Solve(_model);
        _model.Model.Objective = null;

        if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible)) return SolveResult.Unsatisfiable;

        var values = new bool[_vars.Count];
        for (var v = 1; v < _vars.Count; v++) values[v] = solver.BooleanValue(_vars[v]);
        return new SolveResult(true, values);
    }

    private ILiteral Lit(int literal) => literal > 0 ? _vars[literal] : _vars[-literal].Not();

    public void Dispose()
    {
    }
}
