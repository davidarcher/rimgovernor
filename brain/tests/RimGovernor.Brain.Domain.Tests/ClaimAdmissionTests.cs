using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Claim-overlap Method admission (#3249): every Need owner commits through <see cref="Journal.CommitClaimedMethod"/>
/// and is refused only when its claims overlap an open Method's, any owner (#3265: there is no open-work gate).
/// </summary>
public sealed class ClaimAdmissionTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required RoundsResult Review { get; init; }
        public void Dispose() => Journal.Dispose();
        /// <summary>Commits under the food/supply Need, admitted by claims in the review's world.</summary>
        public NeedCommit Food(string method, params DomainAction[] actions) =>
            Journal.CommitClaimedMethod(FoodRules.FoodSupplyNeed, World, new(method), "",
                new PlanSpec(new(method + "-plan-" + Guid.NewGuid().ToString("N")[..8]), new(1), EquatableArray.From(actions)), TestContext.Current.CancellationToken);
        /// <summary>Commits under the shelter family's starter Need (#3257): its rungs are admitted by claims.</summary>
        public NeedCommit Shelter(string method, params DomainAction[] actions) =>
            Journal.CommitClaimedMethod(ShelterFamily.Starter, World, new(method), "",
                new PlanSpec(new(method + "-plan-" + Guid.NewGuid().ToString("N")[..8]), new(1), EquatableArray.From(actions)), TestContext.Current.CancellationToken);
        /// <summary>Commits under the generic test Need (admin/work-assignments).</summary>
        public NeedCommit Work(string method, params DomainAction[] actions) =>
            Journal.CommitClaimedMethod(TestNeed.Id, World, new(method), "",
                new PlanSpec(new(method + "-plan-" + Guid.NewGuid().ToString("N")[..8]), new(1), EquatableArray.From(actions)), TestContext.Current.CancellationToken);
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
        var review = journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
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
        Assert.Equal(2, colony.Journal.LoadNeed(FoodRules.FoodSupplyNeed, World, TestContext.Current.CancellationToken).Methods.Count);
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
        Assert.Equal(ClaimOutcome.Admitted, colony.Work("dress-a", Dress("d1", "a")).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, colony.Work("dress-b", Dress("d2", "b")).Outcome);
        var refused = colony.Work("dress-a-again", Dress("d3", "a"));
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("gear/a", refused.Conflict!.Claim);
    }

    [Fact]
    public void DuplicateShelterRungOnTheSameCellsIsRefused()
    {
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("bunk-1", Build("bed-1", "TestBed", 5, 5)).Outcome);
        var refused = colony.Shelter("bunk-2", Build("bed-2", "TestBed", 5, 5));
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("bunk-1", refused.Conflict!.Method.Value);
    }

    [Fact]
    public void OverlappingFootprintsAreRefusedAndAdjacentOnesAdmitted()
    {
        using var colony = Open();
        // A North 1x2 bed at (5,5) occupies (5,5) and (5,6): the anchor-only claim never saw the second cell.
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("bed", Build("bed", "TestBed", 5, 5)).Outcome);
        var overlap = colony.Shelter("wall-on-bed", Build("wall-1", "TestWall", 5, 6));
        Assert.Equal(ClaimOutcome.Conflict, overlap.Outcome);
        Assert.Equal("cell/5/6", overlap.Conflict!.Claim);
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("wall-beside", Build("wall-2", "TestWall", 6, 6)).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("wall-above", Build("wall-3", "TestWall", 5, 7)).Outcome);
    }

    [Fact]
    public void ShelterRungsRunTogetherOnDisjointCellsAndClearingAHeldCellIsRefused()
    {
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("room-shell-1",
            Build("ring-1", "TestWall", 0, 0), Build("ring-2", "TestWall", 1, 0), Build("ring-3", "TestWall", 2, 0)).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("shelter-bunk", Build("bunk", "TestBed", 1, 1)).Outcome);
        var clearing = DomainAction.NewDeconstructionAction(new("cut"), new Deconstruction("old-wall", "TestWall", new(1, 0)));
        var refused = colony.Shelter("shelter-clear", clearing);
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("cell/1/0", refused.Conflict!.Claim);
        Assert.Equal("room-shell-1", refused.Conflict.Method.Value);
    }

    /// <summary>
    /// #3557: a deconstruction claims its cell only, so the pawn activity order that works the designation it stood (a
    /// Work/Deconstruct task claiming the building) is admitted beside it; a second deconstruction of the cell is not.
    /// </summary>
    [Fact]
    public void ADeconstructionClaimsItsCellAndTheOrderWorkingItsBuildingIsAdmitted()
    {
        using var colony = Open();
        var clearing = DomainAction.NewDeconstructionAction(new("cut"), new Deconstruction("old-wall", "TestWall", new(1, 0)));
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("room-clear", clearing).Outcome);
        var step = new PawnActivityStep(PawnActivityKind.Work, "old-wall", "Deconstruct", "Deconstruct", 0, 1000);
        var batch = new PawnActivities(1, [new("a", "removal/a", "remove", 0, 1, false, [step]) { Task = "removal/a" }])
        { Tasks = [new SelectedActivityTask("removal/a", "a", [], step, new(1, 0), 100) { SiteDefinition = "TestWall" }] };
        Assert.Equal(ClaimOutcome.Admitted, colony.Work("remove", DomainAction.NewPawnActivitiesAction(new("order"), batch)).Outcome);
        var again = colony.Shelter("room-clear-again", DomainAction.NewDeconstructionAction(new("cut-again"), new Deconstruction("old-wall", "TestWall", new(1, 0))));
        Assert.Equal(ClaimOutcome.Conflict, again.Outcome);
        Assert.Equal("cell/1/0", again.Conflict!.Claim);
    }

    /// <summary>
    /// #3612: the reconcile's UninstallBuilding (Pack) claims its cell only, so the pawn activity order that works the
    /// Uninstall designation it stood (a Work/Uninstall task claiming the building) is admitted beside it.
    /// </summary>
    [Fact]
    public void AnUninstallClaimsItsCellAndTheOrderWorkingItsBuildingIsAdmitted()
    {
        using var colony = Open();
        var pack = DomainAction.NewUninstallBuildingAction(new("pack"), new MoveBuilding("old-bed", "TestBed", new(1, 0), Rotation.South));
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("room-pack", pack).Outcome);
        var step = new PawnActivityStep(PawnActivityKind.Work, "old-bed", "Uninstall", "Uninstall", 0, 1000);
        var batch = new PawnActivities(1, [new("a", "removal/a", "remove", 0, 1, false, [step]) { Task = "removal/a" }])
        { Tasks = [new SelectedActivityTask("removal/a", "a", [], step, new(1, 0), 100) { SiteDefinition = "TestBed" }] };
        Assert.Equal(ClaimOutcome.Admitted, colony.Work("remove", DomainAction.NewPawnActivitiesAction(new("order"), batch)).Outcome);
        var again = colony.Shelter("room-pack-again", DomainAction.NewUninstallBuildingAction(new("pack-again"), new MoveBuilding("old-bed", "TestBed", new(1, 0), Rotation.South)));
        Assert.Equal(ClaimOutcome.Conflict, again.Outcome);
        Assert.Equal("cell/1/0", again.Conflict!.Claim);
    }

    /// <summary>
    /// #3614: the reconcile's CoverClearance (HaulOut) claims its cell only, so the Deliver order that works the Haul
    /// designation it stood (claiming the stack) is admitted beside it; a second clearance of the cell is not.
    /// </summary>
    [Fact]
    public void AHaulOutClaimsItsCellAndTheDeliverOrderOfItsStackIsAdmitted()
    {
        using var colony = Open();
        var haul = DomainAction.NewCoverClearanceAction(new("haul"), new CoverClearance("stack", "Steel", CoverClearanceDesignation.Haul, new(1, 0)));
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("room-haul", haul).Outcome);
        var step = new PawnActivityStep(PawnActivityKind.Deliver, "stack", "", "", 40, 1000) { Destination = new(5, 5) };
        var batch = new PawnActivities(1, [new("a", "removal/a", "remove", 0, 1, false, [step]) { Task = "removal/a" }])
        { Tasks = [new SelectedActivityTask("removal/a", "a", [], step, new(1, 0), 100)] };
        Assert.Equal(ClaimOutcome.Admitted, colony.Work("remove", DomainAction.NewPawnActivitiesAction(new("order"), batch)).Outcome);
        var again = colony.Shelter("room-haul-again", DomainAction.NewCoverClearanceAction(new("haul-again"), new CoverClearance("stack", "Steel", CoverClearanceDesignation.Haul, new(1, 0))));
        Assert.Equal(ClaimOutcome.Conflict, again.Outcome);
        Assert.Equal("cell/1/0", again.Conflict!.Claim);
    }

    [Fact]
    public void ReleasedShelterMethodFreesItsClaimsAndTheStoreLeavesReissueToThePlanner()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var first = colony.Shelter("bunk", Build("bed", "TestBed", 5, 5));
        Assert.Equal(ClaimOutcome.Admitted, first.Outcome);
        Assert.Equal(ClaimOutcome.Conflict, colony.Shelter("bunk-again", Build("bed-2", "TestBed", 5, 5)).Outcome);
        var plan = Assert.Single(first.Owner.Methods).Plan;
        colony.Journal.Cancel(plan, new("bed"), ct);
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("bunk-again", Build("bed-2", "TestBed", 5, 5)).Outcome);
        // A Need's released MethodId is the planner's to issue again once its product is gone (decision 19): the store
        // admits a new attempt, and the history reads that latest attempt of the MethodId.
        Assert.Equal(ClaimOutcome.Admitted, colony.Shelter("bunk", Build("bed-3", "TestBed", 8, 8)).Outcome);
        var owner = colony.Journal.LoadNeed(ShelterFamily.Starter, World, ct);
        var latest = Assert.Single(colony.Journal.OwnerMethodHistory(owner, ct), m => m.Method.Value == "bunk").Plan;
        Assert.NotEqual(plan, latest);
        Assert.Equal(latest, colony.Journal.OwnerMethodPlan(owner, new("bunk"), ct));
    }

    [Fact]
    public void SameMethodIdContinuesTheOpenMethodWithoutANewRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var first = colony.Work("deliver", Delivery("d1", "a", "deliver"));
        Assert.Equal(ClaimOutcome.Admitted, first.Outcome);
        var again = colony.Work("deliver", Delivery("d2", "b", "deliver"));
        Assert.Equal(ClaimOutcome.Continued, again.Outcome);
        Assert.Null(again.Conflict);
        Assert.Single(colony.Journal.LoadNeed(TestNeed.Id, World, ct).Methods);
    }

    [Fact]
    public void OverlapIsCheckedAcrossOwnersAndAnOwnerHoldsDisjointMethodsTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, colony.Work("ledger", Delivery("l1", "a", "ledger")).Outcome);
        var other = colony.Food("art", Delivery("a1", "a", "art"));
        Assert.Equal(ClaimOutcome.Conflict, other.Outcome);
        Assert.Equal("pawn/a", other.Conflict!.Claim);
        Assert.Equal("ledger", other.Conflict.Method.Value);
        Assert.Empty(colony.Journal.LoadNeed(FoodRules.FoodSupplyNeed, World, ct).Methods);
        // A disjoint second Method beside the owner's own open one is admitted.
        Assert.Equal(ClaimOutcome.Admitted, colony.Work("ledger-2", Delivery("l2", "b", "ledger-2")).Outcome);
        Assert.Equal(2, colony.Journal.LoadNeed(TestNeed.Id, World, ct).Methods.Count);
    }

    [Fact]
    public void MethodClaimsNameEachKindsSubjectAndShareCapacityTargets()
    {
        var evidence = new WorkAdmissionEvidence();
        WorkAction Row(DomainAction a) => new(new("p"), a, new() { Action = a.Id, Stage = Stage.Pending });
        var bill = DomainAction.NewProductionBillAction(new("bill"), new("bench", "TestRecipe", BillMode.GearBatch, 1, "Rice"));
        var claims = MethodClaimsRules.MethodClaimsOf(evidence, [Row(Dress("d", "a")), Row(Forage("f", "berry")), Row(Field("z", (1, 1)))]);
        Assert.Contains("gear/a", claims.Targets);
        Assert.Contains("berry", claims.Targets);
        Assert.Contains("cell/1/1", claims.Targets);
        Assert.Contains(MethodClaimsRules.MethodClaimsOf(evidence, [Row(bill)]).Targets, t => t.StartsWith("bill/bench/", StringComparison.Ordinal));
        // Settled work claims nothing.
        var done = new WorkAction(new("p"), Forage("f", "berry"), new() { Action = new("f"), Stage = Stage.Completed });
        Assert.Empty(MethodClaimsRules.MethodClaimsOf(evidence, [done]).Targets);
        // Unpriced buildings fall back to the anchor.
        Assert.Equal(["cell/4/4"], MethodClaimsRules.BuildingClaimTargets(evidence, Build("w", "Unknown", 4, 4).Building!));
        var held = new MethodClaims([], ["bed-1"], [new WorkTargetClaim("bed-1", "x", "t", 0)]);
        Assert.Null(MethodClaimsRules.MethodClaimOverlap(held with { Shared = [new WorkTargetClaim("bed-1", "y", "u", 0)] }, held));
        Assert.Equal("bed-1", MethodClaimsRules.MethodClaimOverlap(held with { Shared = [] }, held));
    }
}
