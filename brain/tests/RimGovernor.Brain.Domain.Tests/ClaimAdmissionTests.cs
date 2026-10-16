using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Claim-overlap Method admission (#3249): an owner committing through <see cref="Journal.CommitClaimedMethod"/>
/// skips the open-work gate and is refused only when its claims overlap an open Method's, any owner.
/// </summary>
public sealed class ClaimAdmissionTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required RoundsResult Review { get; init; }
        public void Dispose() => Journal.Dispose();
        public StandardState Owner(ConcernId kind) =>
            Journal.LoadStandard(Assert.Single(Review.Standards, s => s.Standard.Kind == kind).Standard.Id, TestContext.Current.CancellationToken);
        /// <summary>Commits under the food/supply Need, admitted by claims in the review's world.</summary>
        public NeedCommit Food(string method, params DomainAction[] actions) =>
            Journal.CommitClaimedMethod(FoodSupplyNeed, World, new(method), "",
                new PlanSpec(new(method + "-plan-" + Guid.NewGuid().ToString("N")[..8]), new(1), EquatableArray.From(actions)), TestContext.Current.CancellationToken);
        public ClaimCommit Commit(ConcernId kind, string method, params DomainAction[] actions)
        {
            var owner = Owner(kind);
            return Journal.CommitClaimedMethod(owner.Standard.Id, owner.Revision, new(method), "",
                new PlanSpec(new(method + "-plan-" + Guid.NewGuid().ToString("N")[..8]), new(1), EquatableArray.From(actions)), TestContext.Current.CancellationToken);
        }
    }

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-claim-admission-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with
        {
            Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 100)]),
            Colonists = Known(1L), IndoorCapacity = Known(0L), BedCapacity = Known(0L), FieldCoverage = Known(0.0),
            WorkAdmission = facts.WorkAdmission with
            {
                Buildings = Known<EquatableArray<WorkBuildingCost>>([
                    new("TestWall", "Rice", Known<EquatableArray<Amount>>([new(new("Rice"), 1)])) { Size = Known(new Cell(1, 1)) },
                    new("TestBed", "Rice", Known<EquatableArray<Amount>>([new(new("Rice"), 1)])) { Size = Known(new Cell(1, 2)) }]),
            },
        };
        var review = journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        return new() { Journal = journal, Review = review };
    }

    private static DomainAction Build(string id, string definition, int x, int z) =>
        DomainAction.NewBuildingAction(new(id), new(definition, new(x, z), Rotation.North, "Rice"), ConstructionTier.Survive);

    private static DomainAction Forage(string id, string source) =>
        DomainAction.NewAcquisitionAction(new(id), new(source, "Plant_Berry", new(1, 1)));

    private static DomainAction Field(string id, params (int X, int Z)[] cells) =>
        DomainAction.NewZoneCreateAction(new(id), new ZoneCreate(ZoneKind.Growing, "Plant_Rice", cells.Select(c => new Cell(c.X, c.Z))));

    private static DomainAction Dress(string id, string pawn) =>
        DomainAction.NewApparelPolicyAction(new(id), new(new ApparelPolicySpec(new(pawn), "token-" + id, "policy-" + id, ["Apparel_Parka"], 0, 1, 0, 6)));

    private static DomainAction Delivery(string id, string pawn, string method) =>
        DomainAction.NewPawnActivitiesAction(new(id), new(1, [new(pawn, "assignment-" + id, method, 0, 1, false,
            [new(PawnActivityKind.Deliver, "stack-" + id, "", "", 1, 500) { Destination = new(id[^1] - '0' + (pawn[0] - 'a') * 10, 20) }])]));

    [Fact]
    public void SecondForageIsAdmittedOnOtherSourcesAndRefusedOnAnOpenOne()
    {
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Food("forage-1", Forage("f1", "berry-1")).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, colony.Food("forage-2", Forage("f2", "berry-2")).Outcome);
        var refused = colony.Food("forage-3", Forage("f3", "berry-1"));
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("berry-1", refused.Conflict!.Claim);
        Assert.Equal("forage-1", refused.Conflict.Method.Value);
        Assert.StartsWith("need ", refused.Conflict.Holder);
        Assert.Contains("claim_conflict: berry-1", refused.Conflict.Reason);
        Assert.Equal(2, colony.Journal.LoadNeed(FoodSupplyNeed, World, TestContext.Current.CancellationToken).Methods.Count);
    }

    [Fact]
    public void SecondFieldBatchIsAdmittedOnOtherCellsAndRefusedOnSharedCells()
    {
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Food("field-1", Field("z1", (10, 10), (10, 11))).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, colony.Food("field-2", Field("z2", (12, 10), (12, 11))).Outcome);
        var refused = colony.Food("field-3", Field("z3", (10, 11), (10, 12)));
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("cell/10/11", refused.Conflict!.Claim);
    }

    [Fact]
    public void GearDressedTwiceForOnePawnIsRefused()
    {
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainWorkLedger, "dress-a", Dress("d1", "a")).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainWorkLedger, "dress-b", Dress("d2", "b")).Outcome);
        var refused = colony.Commit(MaintainWorkLedger, "dress-a-again", Dress("d3", "a"));
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("gear/a", refused.Conflict!.Claim);
    }

    [Fact]
    public void DuplicateHousingOnTheSameCellsIsRefused()
    {
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainHousing, "bunk-1", Build("bed-1", "TestBed", 5, 5)).Outcome);
        var refused = colony.Commit(MaintainHousing, "bunk-2", Build("bed-2", "TestBed", 5, 5));
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("bunk-1", refused.Conflict!.Method.Value);
    }

    [Fact]
    public void OverlappingFootprintsAreRefusedAndAdjacentOnesAdmitted()
    {
        using var colony = Open();
        // A North 1x2 bed at (5,5) occupies (5,5) and (5,6): the anchor-only claim never saw the second cell.
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainHousing, "bed", Build("bed", "TestBed", 5, 5)).Outcome);
        var overlap = colony.Commit(MaintainHousing, "wall-on-bed", Build("wall-1", "TestWall", 5, 6));
        Assert.Equal(ClaimOutcome.Conflict, overlap.Outcome);
        Assert.Equal("cell/5/6", overlap.Conflict!.Claim);
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainHousing, "wall-beside", Build("wall-2", "TestWall", 6, 6)).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainHousing, "wall-above", Build("wall-3", "TestWall", 5, 7)).Outcome);
    }

    [Fact]
    public void ShelterRungsRunTogetherOnDisjointCellsAndClearingAHeldCellIsRefused()
    {
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainHousing, "room-shell-1",
            Build("ring-1", "TestWall", 0, 0), Build("ring-2", "TestWall", 1, 0), Build("ring-3", "TestWall", 2, 0)).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainHousing, "shelter-bunk", Build("bunk", "TestBed", 1, 1)).Outcome);
        var clearing = DomainAction.NewDeconstructionAction(new("cut"), new Deconstruction("old-wall", "TestWall", new(1, 0)));
        var refused = colony.Commit(MaintainHousing, "shelter-clear", clearing);
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("cell/1/0", refused.Conflict!.Claim);
        Assert.Equal("room-shell-1", refused.Conflict.Method.Value);
    }

    [Fact]
    public void ReleasedMethodFreesItsClaimsButKeepsItsMethodId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var first = colony.Commit(MaintainHousing, "bunk", Build("bed", "TestBed", 5, 5));
        Assert.Equal(ClaimOutcome.Admitted, first.Outcome);
        Assert.Equal(ClaimOutcome.Conflict, colony.Commit(MaintainHousing, "bunk-again", Build("bed-2", "TestBed", 5, 5)).Outcome);
        var plan = Assert.Single(first.State.Methods).Plan;
        colony.Journal.Cancel(plan, new("bed"), ct);
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainHousing, "bunk-again", Build("bed-2", "TestBed", 5, 5)).Outcome);
        // A released MethodId stays bound for the episode: the journal key, not the claim check, refuses it.
        Assert.Throws<MethodBoundException>(() => colony.Commit(MaintainHousing, "bunk", Build("bed-3", "TestBed", 8, 8)));
    }

    [Fact]
    public void ReleasedMethodIdIsAdmittedAgainWhenTheOwnerReopens()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var first = colony.Commit(MaintainWorkLedger, "deliver", Delivery("d1", "a", "deliver"));
        Assert.Equal(ClaimOutcome.Admitted, first.Outcome);
        colony.Journal.Cancel(Assert.Single(first.State.Methods).Plan, new("d1"), ct);
        // The deficit clears, then returns: the Standard opens a new episode.
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        var met = colony.Journal.ReviewRounds(new() { Revision = colony.Review.Review.Revision, Current = World, Tick = new(110), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = facts with { Tick = new(110), LedgerOwed = Known(false) } }, ct);
        colony.Journal.ReviewRounds(new() { Revision = met.Review.Revision, Current = World, Tick = new(120), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = facts with { Tick = new(120) } }, ct);
        var owner = colony.Owner(MaintainWorkLedger);
        Assert.Equal(first.State.Standard.Episode + 1, owner.Standard.Episode);
        var reopened = colony.Commit(MaintainWorkLedger, "deliver", Delivery("d2", "a", "deliver"));
        Assert.Equal(ClaimOutcome.Admitted, reopened.Outcome);
        Assert.Equal(owner.Standard.Episode, Assert.Single(reopened.State.Methods).Episode);
    }

    [Fact]
    public void SameMethodIdContinuesTheOpenMethodWithoutANewRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var first = colony.Commit(MaintainWorkLedger, "deliver", Delivery("d1", "a", "deliver"));
        Assert.Equal(ClaimOutcome.Admitted, first.Outcome);
        var again = colony.Commit(MaintainWorkLedger, "deliver", Delivery("d2", "b", "deliver"));
        Assert.Equal(ClaimOutcome.Continued, again.Outcome);
        Assert.Null(again.Conflict);
        Assert.Equal(first.State.Revision, again.State.Revision);
        Assert.Single(colony.Journal.LoadOwnerMethods(colony.Owner(MaintainWorkLedger), ct));
    }

    [Fact]
    public void OverlapIsCheckedAcrossOwnersAndTheGateStillHoldsNonOptedOwners()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainWorkLedger, "ledger", Delivery("l1", "a", "ledger")).Outcome);
        var other = colony.Commit(MaintainArt, "art", Delivery("a1", "a", "art"));
        Assert.Equal(ClaimOutcome.Conflict, other.Outcome);
        Assert.Equal("pawn/a", other.Conflict!.Claim);
        Assert.Equal("ledger", other.Conflict.Method.Value);
        Assert.Empty(colony.Journal.LoadOwnerMethods(colony.Owner(MaintainArt), ct));
        // Opted in, a disjoint second Method beside the owner's own open one is admitted ...
        Assert.Equal(ClaimOutcome.Admitted, colony.Commit(MaintainWorkLedger, "ledger-2", Delivery("l2", "b", "ledger-2")).Outcome);
        // ... while the ordinary commit keeps the one-open-Method gate.
        var owner = colony.Owner(MaintainWorkLedger);
        Assert.Throws<OpenMethodException>(() => colony.Journal.CommitMethod(owner.Standard.Id, owner.Revision, new("ledger-3"),
            new PlanSpec(new("ledger-3-plan"), new(1), [Delivery("l3", "c", "ledger-3")]), ct));
    }

    [Fact]
    public void MethodClaimsNameEachKindsSubjectAndShareCapacityTargets()
    {
        var evidence = new WorkAdmissionEvidence();
        WorkAction Row(DomainAction a) => new(new("p"), a, new() { Action = a.Id, Stage = Stage.Pending });
        var bill = DomainAction.NewProductionBillAction(new("bill"), new("bench", "TestRecipe", BillMode.GearBatch, 1, "Rice"));
        var claims = MethodClaimsOf(evidence, [Row(Dress("d", "a")), Row(Forage("f", "berry")), Row(Field("z", (1, 1)))]);
        Assert.Contains("gear/a", claims.Targets);
        Assert.Contains("berry", claims.Targets);
        Assert.Contains("cell/1/1", claims.Targets);
        Assert.Contains(MethodClaimsOf(evidence, [Row(bill)]).Targets, t => t.StartsWith("bill/bench/", StringComparison.Ordinal));
        // Settled work claims nothing.
        var done = new WorkAction(new("p"), Forage("f", "berry"), new() { Action = new("f"), Stage = Stage.Completed });
        Assert.Empty(MethodClaimsOf(evidence, [done]).Targets);
        // Unpriced buildings fall back to the anchor.
        Assert.Equal(["cell/4/4"], BuildingClaimTargets(evidence, Build("w", "Unknown", 4, 4).Building!));
        var held = new MethodClaims([], ["bed-1"], [new WorkTargetClaim("bed-1", "x", "t", 0)]);
        Assert.Null(MethodClaimOverlap(held with { Shared = [new WorkTargetClaim("bed-1", "y", "u", 0)] }, held));
        Assert.Equal("bed-1", MethodClaimOverlap(held with { Shared = [] }, held));
    }
}
