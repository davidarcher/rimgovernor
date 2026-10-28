using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Store;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FirstLayoutPrerequisiteTests
{
    [Fact]
    public async Task FamilyRunnerHonorsThePolicyHoldWhileWorkingAnIndependentReadyNeed()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        using var journal = RimGovernor.Brain.Store.Store.Open(Path.Combine(Path.GetTempPath(),
            "rg-first-layout-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new() { Current = world, Tick = new(100), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var family = new AdminFamily(new AdminNeeds { Burial = true, WorkAssignments = true });
        var facts = new AdminFacts { BurialPlanPending = true, WorkAssignments = WorkAssignmentsReview.Owed };
        var worked = new List<NeedId>();
        var runner = new FamilyRunner<AdminFacts>(family, (_, _) => Task.FromResult(facts), work: (work, _, _) =>
        {
            worked.Add(work.Need.Id);
            return Task.FromResult(Verdict.BuildingReasonNoDeficit);
        });
        var result = await runner.RunAsync(new FamilyStep { Journal = journal, World = world }, ct);
        Assert.Equal([AdminFamily.WorkAssignments], worked);
        var held = Assert.Single(result.Reports, r => r.Need == AdminFamily.Burial);
        Assert.True(held.Verdict.Is(Cause.AwaitingPlan));
        Assert.Empty(result.Methods);
    }

    [Fact]
    public void SameTickCachedCensusReceivesTheLayoutDerivedAfterItsNativeRead()
    {
        var observed = ColonyFactsCoreTests.Reading(new ObservationIdentity
        {
            Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 1 },
                Tick = 100, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(7) },
        });
        var cache = new CensusStore();
        cache.Retain(new ReviewReading { Reading = observed, Frame = new(), Emergency = new() });
        var plan = new LayoutPlan();
        cache.RememberLayout(observed.Identity, plan);
        var source = new object();
        Assert.True(cache.Lookup(source, source, observed.Identity, [], out var served));
        Assert.Same(plan, served.Reading.Facts.LayoutPlan);
        Assert.Same(observed.Facts.Census, served.Reading.Facts.Census);
        Assert.Null(observed.Facts.LayoutPlan);
    }

    [Fact]
    public void BurialWaitsForTheFirstPlanWithoutBlockingWorkAssignments()
    {
        var family = new AdminFamily(new AdminNeeds { Burial = true, WorkAssignments = true });
        var facts = new AdminFacts { BurialPlanPending = true, WorkAssignments = WorkAssignmentsReview.Owed };
        var needs = family.Derive(facts);
        Assert.All(needs, n => Assert.Equal(Satisfaction.Unmet, n.Satisfaction));
        var held = Assert.Single(family.Propose(needs, facts));
        Assert.Equal(AdminFamily.Burial, held.Need);
        Assert.Equal("layout_plan", held.Hold!.Subject);
        Assert.Equal(Satisfaction.Met, family.Derive(facts with
            { BurialPlanPending = false, Burial = AdminWorkReview.Settled }).Single(n => n.Id == AdminFamily.Burial).Satisfaction);
    }

    [Fact]
    public void ClimateWorkWaitsForThePlanRatherThanClaimingAnUnreadEnvelopeOrCompletion()
    {
        var family = new LayoutFamily(new() { ClimateEnvelope = true });
        var facts = new LayoutFacts { LayoutPlanPending = true };
        var need = Assert.Single(family.Derive(facts));
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        Assert.Equal("layout_plan", Assert.Single(family.Propose([need], facts)).Hold!.Subject);
        Assert.Equal(Satisfaction.Met, Assert.Single(family.Derive(new() { ClimateEnvelope = [] })).Satisfaction);
        Assert.Throws<RequiredFactException>(() => family.Derive(new()));
    }

    [Fact]
    public void RoomPlanPrerequisitesRemainUnmetAndHeldWithoutReadingAbsentPlanResults()
    {
        var family = new RoomsFamily(true, false, false, false, false);
        var facts = new RoomsFacts { LayoutPlanPending = true };
        var needs = family.Derive(facts);
        Assert.Equal(Satisfaction.Unmet, Assert.Single(needs).Satisfaction);
        Assert.Equal("layout_plan", Assert.Single(family.Propose(needs, facts)).Hold!.Subject);
        Assert.Equal(Satisfaction.Unmet, RoomsFamily.WorkChairsNeed(facts).Satisfaction);
        Assert.Throws<RequiredFactException>(() => family.Derive(new()));
    }

    [Fact]
    public void AnimalRoomsWaitForPlannedGeometryAndReassessWhenItArrives()
    {
        var family = new AnimalsFamily(false, true);
        var facts = new AnimalsFacts { LayoutPlanPending = true };
        var needs = family.Derive(facts);
        Assert.Equal(2, needs.Count);
        Assert.All(needs, n => Assert.Equal(Satisfaction.Unmet, n.Satisfaction));
        Assert.All(family.Propose(needs, facts), m => Assert.Equal("layout_plan", m.Hold!.Subject));
        Assert.All(family.Derive(facts with { LayoutPlanPending = false }), n => Assert.Equal(Satisfaction.Met, n.Satisfaction));
    }
}
