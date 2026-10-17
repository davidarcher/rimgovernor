using RimGovernor.Brain.Acceptance;
using Xunit;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The typed acceptance case (#3396): registration holds a case to its start's tick cap, the hang guard derives from the
/// ticks, the runner adapter carries the case's declarations, and a tier is what the cases declare.
/// </summary>
public sealed class TypedCaseTests
{
    private static TypedCase Lab(int ticks, Start? start = null) => new()
    {
        Name = "lab/typed-probe",
        Scope = "a probe case",
        Ticks = ticks,
        Tier = CaseTier.OnDemand,
        Start = start ?? new LabStart(),
        Run = (_, _) => Task.CompletedTask,
    };

    [Fact]
    public void LabCaseOverItsTickCapFailsRegistration()
    {
        var registry = new CaseRegistry();

        var e = Assert.Throws<AcceptanceException>(() => registry.Register(Lab(TypedCase.StagedTickCap + 1)));

        Assert.Contains("cap is 30000", e.Message, StringComparison.Ordinal);
        Assert.Null(registry.Lookup("lab/typed-probe"));
    }

    [Fact]
    public void DebugCaseMayRunToTheWiderCapButNotPast()
    {
        var debug = new DebugStart();
        new CaseRegistry().Register(Lab(TypedCase.TickCap, debug));

        Assert.Throws<AcceptanceException>(() => new CaseRegistry().Register(Lab(TypedCase.TickCap + 1, debug)));
        Assert.Throws<AcceptanceException>(() => new CaseRegistry().Register(Lab(TypedCase.StagedTickCap + 1, debug) with { Stage = new Fx.StageSpec() }));
    }

    [Fact]
    public void HangGuardIsTicksAtThreeHundredPerSecondPlusAMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(100 + 60), Lab(30_000).HangGuard);
        Assert.Equal(TimeSpan.FromMinutes(1), Lab(0).HangGuard);
    }

    [Fact]
    public void AdapterStagesOnTheStartAndCarriesTheTier()
    {
        var spec = new Fx.StageSpec { Paused = true };
        var typed = Lab(600) with { Stage = spec, Tier = CaseTier.Nightly, Ops = [FixtureStage.Tool] };

        var c = typed.ToCase();

        var start = Assert.IsType<FixtureStart>(c.Start);
        Assert.Equal(FixtureStage.Tool, start.Op);
        Assert.IsType<LabStart>(start.On);
        Assert.Equal(CaseTier.Nightly, c.Tier);
        Assert.Same(typed, c.Typed);
        Assert.Empty(c.Lint());
    }

    [Fact]
    public void TiersAreWhatTheCasesDeclare()
    {
        var registry = new CaseRegistry();
        registry.Register(Lab(0) with { Name = "lab/nightly-probe", Tier = CaseTier.Nightly });
        registry.Register(Lab(0) with { Name = "lab/smoke-probe", Tier = CaseTier.Smoke });
        registry.Register(Lab(0) with { Name = "lab/off-probe", Tier = CaseTier.Off });
        registry.Register(Lab(0) with { Name = "lab/demand-probe" });

        Assert.Equal(["lab/nightly-probe"], Tiers.CasesOf(registry, "nightly").Select(c => c.Name));
        Assert.Equal(["lab/smoke-probe"], Tiers.CasesOf(registry, "smoke").Select(c => c.Name));
        Assert.DoesNotContain("lab/off-probe", Tiers.Tiered(registry).Select(c => c.Name));
        Assert.Throws<AcceptanceException>(() => Tiers.CasesOf(registry, "weekly"));
    }
}
