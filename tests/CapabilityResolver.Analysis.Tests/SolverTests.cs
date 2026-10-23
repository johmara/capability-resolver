using CapabilityResolver.Solvers.OrTools;
using CapabilityResolver.Solving;

namespace CapabilityResolver.Analysis.Tests;

public class SolverTests
{
    [Fact]
    public void ClausesAreSolvedAndAssumptionsAreScopedToOneCall()
    {
        using var solver = OrToolsSolver.Create();
        var a = solver.NewVar();
        var b = solver.NewVar();
        solver.AddClause(a, b);

        Assert.True(solver.Solve([]).Satisfiable);
        Assert.True(solver.Solve([-a]) is { Satisfiable: true } onlyB && onlyB[b]);
        Assert.False(solver.Solve([-a, -b]).Satisfiable);
        Assert.True(solver.Solve([]).Satisfiable);
    }

    [Fact]
    public void ConstraintsAddedAfterASolveStillApply()
    {
        using var solver = OrToolsSolver.Create();
        var a = solver.NewVar();
        Assert.True(solver.Solve([]).Satisfiable);

        solver.AddClause(a);
        solver.AddClause(-a);

        Assert.False(solver.Solve([]).Satisfiable);
    }

    [Fact]
    public void CardinalityBoundsAreEnforced()
    {
        using var solver = OrToolsSolver.Create();
        int[] vars = [solver.NewVar(), solver.NewVar(), solver.NewVar()];
        solver.AddAtMost(1, vars);
        solver.AddAtLeast(1, vars);

        Assert.True(solver.Solve([vars[0]]).Satisfiable);
        Assert.False(solver.Solve([vars[0], vars[1]]).Satisfiable);
        Assert.False(solver.Solve([-vars[0], -vars[1], -vars[2]]).Satisfiable);
    }

    [Fact]
    public void ConditionalAtLeastOnlyBindsWhenTheConditionHolds()
    {
        using var solver = OrToolsSolver.Create();
        var gate = solver.NewVar();
        int[] vars = [solver.NewVar(), solver.NewVar()];
        solver.AddAtLeast(2, vars, onlyIf: gate);

        Assert.True(solver.Solve([-gate, -vars[0], -vars[1]]).Satisfiable);
        Assert.False(solver.Solve([gate, -vars[0]]).Satisfiable);
    }

    [Fact]
    public void MinimizePrefersFewerTrueObjectiveLiterals()
    {
        using var solver = OrToolsSolver.Create();
        int[] vars = [solver.NewVar(), solver.NewVar(), solver.NewVar()];
        solver.AddClause(vars[0], vars[1]);
        solver.AddClause(vars[0], vars[2]);

        var best = solver.Minimize(vars, []);

        Assert.True(best.Satisfiable);
        Assert.Equal(1, vars.Count(v => best[v]));
        Assert.True(best[vars[0]]);

        var again = solver.Solve([]);
        Assert.True(again.Satisfiable);
    }

    [Fact]
    public void MinimizeReportsUnsatisfiable()
    {
        using var solver = OrToolsSolver.Create();
        var a = solver.NewVar();
        solver.AddClause(a);

        Assert.False(solver.Minimize([a], [-a]).Satisfiable);
    }
}
