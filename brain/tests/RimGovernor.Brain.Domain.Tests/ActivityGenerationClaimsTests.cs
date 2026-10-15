using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ActivityGenerationClaimsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthorityRolloverEndsActivityClaimsWithoutDroppingPersistentConstruction(bool accepted)
    {
        var ct = TestContext.Current.CancellationToken;
        BridgeFunctions.RegisterIntentKinds();
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-generation-claims-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("pawn") with
        { Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 20)]),
            Colonists = Known(1L), IndoorCapacity = Known(0L), BedCapacity = Known(0L) };
        var review = journal.ReviewRounds(new() { Current = world, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
        var activity = DomainAction.NewPawnActivitiesAction(new("delivery"), new(1,
            [new("pawn", "assignment", "method", 0, 1, false,
                [new(PawnActivityKind.Deliver, "rice-stack", "", "", 6, 500) { Destination = new(2, 2) }])
                { OutputDelivery = new("Rice", new(1, 1), new(2, 2), 6, 500, 3) }]));
        var building = DomainAction.NewBuildingAction(new("wall"),
            new("TestWall", new(3, 3), Rotation.North, "Rice"), ConstructionTier.Survive);
        var plan = new PlanSpec(new("plan"), new(1), [activity]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("method"), plan, ct);
        var housing = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainHousing);
        var construction = new PlanSpec(new("construction"), new(1), [building]);
        journal.CommitMethod(housing.Standard.Id, housing.Revision, new("shelter-method"), construction, ct);
        if (accepted)
        {
            var scope = world with { Plan = plan.Id };
            journal.Prepare(plan.Id, activity.Id, scope, new(100), ct);
            var sent = journal.Dispatch(plan.Id, activity.Id, scope, new(100), ct);
            journal.RecordReceipt(plan.Id, activity.Id, sent.View.Attempt, Receipt.Accepted, ct);
        }
        Assert.Contains("pawn", journal.WorkResources(world, ct).Claims.Pawns);
        Assert.Equal(8, journal.WorkResources(world, ct).Materials.Value[new("Rice")].Value);
        var changed = world with { Native = new(2) };
        journal.ReviewRounds(new() { Revision = review.Review.Revision, Current = changed, Tick = new(110), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = facts with { Tick = new(110) } }, ct);
        var remaining = journal.WorkResources(changed, ct);
        Assert.Empty(remaining.Claims.Pawns);
        Assert.DoesNotContain("rice-stack", remaining.Claims.Targets);
        Assert.Contains(BuildingClaimTarget(building.Building!), remaining.Claims.Targets);
        Assert.Equal(14, remaining.Materials.Value[new("Rice")].Value);
        Assert.False(journal.LoadPlan(plan.Id, ct).Retired); // Historical intent is not an unauthorized release order.
        Assert.False(journal.LoadPlan(construction.Id, ct).Retired);
    }
}
