using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
namespace RimGovernor.Brain.Domain.Tests;

public sealed class SowingProviderTests
{
    private const double SowWork = 400d;

    private static SowingWorker Worker(string pawn, int cells, double perCell = SowWork, string zone = "Zone_Growing12") =>
        new(pawn, zone, "GrowerSow", 7d, perCell,
            EquatableArray.From(Enumerable.Range(0, cells).Select(i => new Cell(10 + i, 20))))
        { SkillWork = ProductiveLearningTests.Work() with { Rates = [new(1, Known(1 / perCell))] } };

    private static ForecastCoupledCandidates Generate(double horizon, params SowingWorker[] workers)
    {
        var facts = ForecastPortfolioChoiceTests.Observed([], [], helper: true);
        facts = facts with { ForecastObserved = facts.ForecastObserved with
        {
            Coupled = facts.ForecastObserved.Coupled! with { Sowing = CoupledFixtures.NoSowing with { Workers = EquatableArray.From(workers) } }
        } };
        return ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)!;
    }

    [Fact]
    public void ACapablePawnsZoneIsOneRootWhoseWorkIsTheCellsTimesSowWorkOverSpeed()
    {
        // Plants 10 sows devilstrand (12 cells at 400 work / 0.8 speed); the Plants-8 helper's read has no cells for it.
        var generated = Generate(GameTime.TicksPerDay, Worker("pawn", 12, SowWork / .8), Worker("helper", 0));
        var task = Assert.Single(generated.Context.Activities.Activities, t => t.Id.StartsWith("sow/", StringComparison.Ordinal));
        var option = Assert.Single(generated.Context.Activities.Options, o => o.Activity == task.Id);
        Assert.Equal("pawn", option.Pawn);
        Assert.Equal(12 * SowWork / .8, option.WorkTicks.Value, 6);
        var root = Assert.Single(generated.Alternatives, c => c.ExclusiveOutcome == "sow/Zone_Growing12");
        Assert.Equal([task.Id], root.ActivityCompletions);
        Assert.Contains(generated.Context.Activities.Routes, r => r.Pawn == "pawn" && r.From == "start" && r.To == "cell/10/20" && r.Ticks == new RouteTicks.Reachable(7d));
        var binding = generated.Context.Bindings[(task.Id, "pawn")];
        Assert.Equal((PawnActivityKind.Work, "Zone_Growing12", "GrowerSow", "Sow", 12, new Cell(10, 20)),
            (binding.Kind, binding.Target, binding.WorkGiver, binding.Job, binding.Count, binding.Destination!.Value));
        Assert.DoesNotContain("sowing_cells_truncated", generated.Findings);
    }

    [Fact]
    public void CellsBeyondTheHorizonAreCutAndReported()
    {
        var generated = Generate(350, Worker("pawn", 12, 100));
        var task = Assert.Single(generated.Context.Activities.Activities, t => t.Id.StartsWith("sow/", StringComparison.Ordinal));
        Assert.Equal(300, Assert.Single(generated.Context.Activities.Options, o => o.Activity == task.Id).WorkTicks.Value);
        Assert.Equal(3, generated.Context.Bindings[(task.Id, "pawn")].Count);
        Assert.Contains("sowing_cells_truncated", generated.Findings);
    }

    [Fact]
    public void TwoPawnsOnOneZoneAreExclusiveAlternativesAndAnInvalidSpeedIsReportedNotInvented()
    {
        var generated = Generate(GameTime.TicksPerDay, Worker("pawn", 4), Worker("helper", 4),
            Worker("pawn", 4, zone: "Zone_Growing13") with { TicksPerCell = 0 });
        Assert.Equal(2, generated.Alternatives.Count(c => c.ExclusiveOutcome == "sow/Zone_Growing12"));
        Assert.DoesNotContain(generated.Alternatives, c => c.ExclusiveOutcome == "sow/Zone_Growing13");
        Assert.Contains("sowing_mechanics_invalid:pawn:Zone_Growing13", generated.Findings);
    }

    [Fact]
    public void ASowStepNeedsACellAndACount()
    {
        PawnActivities Batch(PawnActivityStep step) => new(1, [new("pawn", "a", "m", 0, 1, false, [step])]);
        var sow = new PawnActivityStep(PawnActivityKind.Work, "Zone_Growing12", "GrowerSow", "Sow", 12, 1000) { Destination = new(10, 20) };
        Batch(sow).Validate();
        Assert.Throws<DomainException>(() => Batch(sow with { Destination = null }).Validate());
        Assert.Throws<DomainException>(() => Batch(sow with { Count = 0 }).Validate());
        Assert.Throws<DomainException>(() => Batch(sow with { Target = "" }).Validate());
        var wire = RimGovernor.Contracts.Wire.WireJson.Deserialize<SowingActivityRead>(RimGovernor.Contracts.Wire.WireJson.Serialize(
            CoupledFixtures.NoSowing with { Workers = [Worker("pawn", 2)], Findings = ["sowing_zone_without_crop:Zone_Growing14"] }));
        Assert.Equal(Worker("pawn", 2), Assert.Single(wire!.Workers));
    }
}
