using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class VoluntaryMethodAdmissionTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Theory]
    [InlineData(RecoveryMethod.Repair)]
    [InlineData(RecoveryMethod.Breakdown)]
    [InlineData(RecoveryMethod.Refuel)]
    public void DisasterServicesRecoveryRetainsItsAdmission(RecoveryMethod method)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-disaster-recovery-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var plan = new PlanSpec(new("emergency-plan"), new(1),
            [DomainAction.NewRecoveryServiceAction(new("emergency-action"), new(new("pawn"), "building", method))]);
        var committed = journal.CommitClaimedMethod(DisasterFamily.Services, World, new("emergency-method"), "", plan, ct);
        Assert.Equal(RimGovernor.Brain.Store.ClaimOutcome.Admitted, committed.Outcome);
        Assert.Equal(plan.Id, Assert.Single(journal.LoadNeed(DisasterFamily.Services, World, ct).Methods).Plan);
    }

    [Theory]
    [InlineData(CaravanDepartureMode.ExistingRoute)]
    [InlineData(CaravanDepartureMode.SiteReform)]
    public void ReturnIntentRetainsExactModeAcrossJournal(CaravanDepartureMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-return-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var departure = new CaravanDeparture([new("pawn")], [], 2, mode);
        var plan = new PlanSpec(new("return-plan"), new(1),
            [DomainAction.NewCaravanDepartureAction(new("return-action"), departure)]);
        TestNeed.Commit(journal, World, new("return-method"), plan, ct);
        Assert.Equal(mode, Assert.Single(journal.LoadPlan(plan.Id, ct).Spec.Actions).CaravanDeparture!.Mode);
    }
}