using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Binding the chosen defense (task 7.4): the alternative the comparison picks enters the plan as a planned site whose ground
/// other planners keep off, survives a save and load, is what Retain retains so it is not re-selected while it is still best,
/// and its work is a chain: a lost piece reopens its build before any removal, a removal waits until the replacement's
/// observed protection covers what it owes, and a refused build spends the kind's refusal budget.
/// </summary>
public sealed class DefenseBindingTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly Resource Wood = new("WoodLog");

    private static DefensePiece.Priced Piece() => new(600, [new Amount(Wood, 5)]);

    /// <summary>A sandbag line at z 28 (x 21 to 23) in front of the workshop's entrance, firing from z 29.</summary>
    private static readonly DefenseCandidate Cover = new()
    {
        Kind = DefenseCandidateKind.TemporaryCover,
        Key = "temporary_cover@21,28",
        Structures = [.. new[] { 21, 22, 23 }.Select(x => new DefenseStructure("Sandbags", new Cell(x, 28), "", false) { Piece = Piece() })],
        Firing = [new Cell(21, 29), new Cell(23, 29)],
        Cells = [new Cell(21, 28), new Cell(22, 28), new Cell(23, 28), new Cell(21, 29), new Cell(23, 29)],
    };

    private static LayoutPlan Workshop() => new LayoutPlan
    {
        Rooms = [new PlannedRoom { Role = PlannedRole.Workshop, Region = RegionRules.RectRegion(new Rectangle(20, 20, 6, 5)), Door = new Cell(22, 25), DoorSide = new DoorSide.Doored(Rotation.North) }],
        Entrances = [new Cell(22, 26)],
    }.WithRoomIDs();

    private static DefenseProtection Guard(string candidate, double covered, int staffing = 2) => new()
    {
        Candidate = candidate,
        Covered = new DefenseTerm.Assessed(covered),
        Shots = new DefenseTerm.Assessed(covered),
        Cover = new DefenseTerm.Assessed(0),
        Staffing = new DefenseTerm.Assessed(staffing),
        Ammunition = new DefenseTerm.Assessed(0),
        Power = new DefenseTerm.Assessed(0),
        Exposure = new DefenseTerm.Assessed(0),
    };

    private static DefenseChoiceInputs Inputs(IReadOnlyDictionary<Cell, string>? standing = null) => new()
    {
        Harm = new DefenseHarm.Read(1),
        Builders = new DefenseTerm.Assessed(1),
        Materials = new MaterialAvailability.Assessed(new Dictionary<Resource, MaterialUnits> { [Wood] = new MaterialUnits.Exact(1000) }),
        Values = new Dictionary<Resource, ConstructionMaterialValue> { [Wood] = new(MaterialValueBasis.Labor, 0.01) },
        Standing = standing ?? new Dictionary<Cell, string>(),
    };

    /// <summary>Retain over the bound plan: the bound node's pieces and firing cells (DefenseCandidateRules.Retain's shape).</summary>
    private static DefenseCandidate Retained(LayoutPlan bound)
    {
        var node = Assert.IsType<PlannedSite>(DefenseBindingRules.Bound(bound));
        return new DefenseCandidate
        {
            Kind = DefenseCandidateKind.Retain,
            Key = "retain",
            Sites = [node],
            Cells = [.. node.Footprint.Cells()],
            Structures = [.. DefenseBindingRules.Structures(node, DefenseLayoutPlanner.DefenseDefinitionNames, DefenseLayoutPlanner.DefenseConduitDefinition).Select(s => s with { Piece = Piece() })],
            Firing = DefenseBindingRules.Firing(node),
        };
    }

    // Tier node: the bound choice is one DefChoice site with a DefChoice reservation over its cells; the build ground the
    // tiler sites rooms on loses them, while the defense ledger still lets an alternative replace it. Another planner's
    // reservation on the same cells is a ground conflict a defense candidate is refused on.
    [Fact]
    public void TheChosenDefenseIsAPlannedSiteWhoseGroundOtherPlannersKeepOff()
    {
        var plan = Workshop();
        var bound = DefenseBindingRules.Bind(plan, Cover, 0);
        Assert.True(bound.Valid());
        var node = Assert.IsType<PlannedSite>(DefenseBindingRules.Bound(bound));
        Assert.Equal(DefenseBindingRules.NodePrefix + Cover.Key, node.Node);
        Assert.Equal(NodeKind.DefChoice, node.Kind);
        Assert.Equal(Cover.Cells.OrderBy(c => c.Z).ThenBy(c => c.X), node.Footprint.Cells().OrderBy(c => c.Z).ThenBy(c => c.X));
        Assert.Equal(Cover.Structures.Select(s => (s.Definition, s.Cell)), node.Tier!.Places.Select(p => (p.Definition, p.Cell)));
        Assert.Equal(Cover.Firing, DefenseBindingRules.Firing(node));
        var reserved = Assert.Single(bound.Reservations, r => r.Kind == ReservationKind.DefChoice);
        Assert.True(reserved.Area.Equal(node.Footprint));

        var core = new LayoutZone { Kind = LayoutZoneKind.Core, Runs = [.. Enumerable.Range(20, 15).Select(z => new RowRun(z, 15, 15))] };
        var ground = BuildGround.Create([core], bound.Reservations);
        Assert.DoesNotContain(new Cell(22, 28), ground.Core);
        Assert.Contains(new Cell(22, 30), ground.Core);
        Assert.Null(DefenseGround.Of(bound).Refusal(Cover.Cells));

        var held = bound with { Reservations = [.. bound.Reservations, new LayoutReservation { Kind = ReservationKind.Geothermal, Area = RegionRules.RectRegion(new Rectangle(22, 28, 1, 1)) }] };
        Assert.Equal("ground:reservation:Geothermal", DefenseGround.Of(held).Refusal(Cover.Cells));

        // Rebinding replaces the node and its reservation; a replan carries both over unchanged.
        var other = Cover with { Key = "temporary_cover@21,32", Structures = [new DefenseStructure("Sandbags", new Cell(21, 32), "", false)], Firing = [], Cells = [new Cell(21, 32)] };
        var rebound = DefenseBindingRules.Bind(bound, other, 0.4);
        Assert.Equal(DefenseBindingRules.NodePrefix + other.Key, Assert.Single(rebound.Sites, s => s.Kind == NodeKind.DefChoice).Node);
        Assert.Single(rebound.Reservations, r => r.Kind == ReservationKind.DefChoice);
        Assert.Equal(0.4, DefenseBindingRules.Bound(rebound)!.Tier!.Owed, 9);
        var replanned = Workshop().WithBoundDefenseOf(rebound);
        Assert.Equal(rebound.Sites.Where(s => s.Kind == NodeKind.DefChoice), replanned.Sites.Where(s => s.Kind == NodeKind.DefChoice));
        Assert.Single(replanned.Reservations, r => r.Kind == ReservationKind.DefChoice);
        Assert.Same(plan, DefenseBindingRules.Bind(plan, new DefenseCandidate { Kind = DefenseCandidateKind.Retain }, 0));
    }

    // Access: a piece that does not block keeps the ordinary door and entrance open; an impassable piece on the entrance is
    // excluded naming the access it closes.
    [Fact]
    public void OrdinaryDoorsAreKeptAndClosingAnAccessPathIsExcluded()
    {
        var plan = Workshop();
        var ground = DefenseGround.Of(plan);
        var open = DefenseCandidateRules.Constrain(Cover with { Structures = [new DefenseStructure("Sandbags", new Cell(22, 26), "", false)] }, ground, plan);
        Assert.Empty(open.Exclusions);
        Assert.Empty(open.Closes);
        var closed = DefenseCandidateRules.Constrain(Cover with { Cells = [new Cell(22, 26)], Structures = [new DefenseStructure("Wall", new Cell(22, 26), "WoodLog", true)] }, ground, plan);
        Assert.Contains("access:entrance@22,26", closed.Exclusions);
        Assert.IsType<DefenseCandidateStanding.Excluded>(closed.Standing);
    }

    // Journal: the bound plan is saved with the governor state and restored on load, so the next review retains it rather
    // than re-selecting.
    [Fact]
    public void ABoundChoiceSurvivesLoadWithoutReselection()
    {
        var ct = TestContext.Current.CancellationToken;
        var bound = DefenseBindingRules.Bind(Workshop(), Cover, 0.25);
        using var source = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-defense-binding-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        source.RecordLayoutPlan(World, new Tick(100), bound, ct);
        var saved = new Dictionary<string, string>(source.GovernorStateBlobs(ct));
        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-defense-binding-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildFamilies(saved, ct);
        var restored = Assert.IsType<LayoutPlan>(loaded.LayoutPlan(World, new Tick(100), ct)?.Plan);
        var node = Assert.IsType<PlannedSite>(DefenseBindingRules.Bound(restored));
        var before = DefenseBindingRules.Bound(bound)!;
        Assert.Equal((before.Node, before.Kind, before.Reason), (node.Node, node.Kind, node.Reason));
        Assert.True(before.Footprint.Equal(node.Footprint));
        Assert.Equal(before.Tier!.Places.Select(p => (p.Definition, p.Cell)), node.Tier!.Places.Select(p => (p.Definition, p.Cell)));
        Assert.Equal(0.25, node.Tier!.Owed, 9);
        Assert.Single(restored.Reservations, r => r.Kind == ReservationKind.DefChoice);

        // The comparison over the restored plan: Retain carries the bound line, which ties the same line generated afresh, and
        // ties go to Retain. Its unbuilt pieces are the chain's next build.
        var retain = Retained(restored);
        var d = DefenseChoiceRules.Choose(
            [new(retain, Guard("retain", 0.8), Guard("retain" + DefenseChoiceRules.StandingSuffix, 0)), new(Cover, Guard(Cover.Key, 0.8), Guard(Cover.Key + DefenseChoiceRules.StandingSuffix, 0))],
            Inputs());
        Assert.Equal(DefenseCandidateKind.Retain, d.Kind);
        Assert.Equal(3, d.Chosen.Unbuilt.Count);
        var build = Assert.IsType<DefenseStep.Build>(DefenseBindingRules.Next(d.Chosen, new Dictionary<Cell, string>(), node.Tier.Owed));
        Assert.Equal(3, build.Pieces.Count);
    }

    // A destroyed piece: two bags stand, the third was lost. The re-derived comparison still retains the bound line, its one
    // unbuilt piece re-priced, and the chain reopens that piece's build.
    [Fact]
    public void ADestroyedPieceReopensItsBuild()
    {
        var bound = DefenseBindingRules.Bind(Workshop(), Cover, 0);
        var retain = Retained(bound);
        var standing = new Dictionary<Cell, string> { [new Cell(21, 28)] = "Sandbags", [new Cell(23, 28)] = "Sandbags" };
        var d = DefenseChoiceRules.Choose(
            [new(retain, Guard("retain", 0.8), Guard("retain" + DefenseChoiceRules.StandingSuffix, 0.5)), new(Cover, Guard(Cover.Key, 0.8), Guard(Cover.Key + DefenseChoiceRules.StandingSuffix, 0.5))],
            Inputs(standing));
        Assert.Equal(DefenseCandidateKind.Retain, d.Kind);
        var lost = Assert.Single(d.Chosen.Unbuilt);
        Assert.Equal(new Cell(22, 28), lost.Cell);
        var build = Assert.IsType<DefenseStep.Build>(DefenseBindingRules.Next(d.Chosen, standing, 0));
        Assert.Equal([new Cell(22, 28)], build.Pieces.Select(p => p.Cell));

        // Every bag stands and the protection names no missing service: the chain is ready.
        var all = Cover.Structures.ToDictionary(s => s.Cell, s => s.Definition);
        var ready = DefenseChoiceRules.Choose([new(retain, Guard("retain", 0.8), Guard("retain" + DefenseChoiceRules.StandingSuffix, 0.8))], Inputs(all));
        Assert.IsType<DefenseStep.Ready>(DefenseBindingRules.Next(ready.Chosen, all, 0));
        var dark = ready.Chosen with { Alternative = ready.Chosen.Alternative with { Protection = ready.Chosen.Alternative.Protection with { States = ["power:turret"] } } };
        Assert.Equal(new DefenseStep.Held(DefenseBindingRules.TurretPower, "power:turret"), DefenseBindingRules.Next(dark, all, 0));
    }

    // A piece whose cell holds a standing wall replaces it. The wall's removal waits while the replacement's observed protection
    // is below what the replaced defense gave; once it covers that, the removal goes. Losing a replacement piece mid-way
    // reopens its build first, so nothing comes down while the replacement is short.
    [Fact]
    public void LosingTheReplacementMidwayHoldsTheRemoval()
    {
        var bound = DefenseBindingRules.Bind(Workshop(), Cover, 0.5);
        var retain = Retained(bound);
        DefenseChoiceRow Row(IReadOnlyDictionary<Cell, string> standing, double stood) =>
            DefenseChoiceRules.Choose([new(retain, Guard("retain", 0.8), Guard("retain" + DefenseChoiceRules.StandingSuffix, stood))], Inputs(standing)).Chosen;

        var walled = new Dictionary<Cell, string> { [new Cell(21, 28)] = "Sandbags", [new Cell(22, 28)] = "Wall", [new Cell(23, 28)] = "Sandbags" };
        var held = DefenseBindingRules.Next(Row(walled, 0.3), walled, 0.5);
        Assert.Equal(new DefenseStep.Held(DefenseBindingRules.RemovalHeld, DefenseBindingRules.RemovalHeld + ":replacement_deficit:0.2"), held);

        var remove = Assert.IsType<DefenseStep.Remove>(DefenseBindingRules.Next(Row(walled, 0.6), walled, 0.5));
        Assert.Equal([new DefenseRemoval(new Cell(22, 28), "Wall")], remove.Removals);

        var lost = new Dictionary<Cell, string> { [new Cell(21, 28)] = "Sandbags", [new Cell(22, 28)] = "Wall" };
        var rebuild = Assert.IsType<DefenseStep.Build>(DefenseBindingRules.Next(Row(lost, 0.6), lost, 0.5));
        Assert.Equal([new Cell(23, 28)], rebuild.Pieces.Select(p => p.Cell));

        var unprobed = Row(walled, 0.6) with { Alternative = Row(walled, 0.6).Alternative with { Standing = null } };
        Assert.Equal(DefenseBindingRules.RemovalHeld + ":replacement_unprobed", DefenseBindingRules.RemovalHold(unprobed, 0.5));
        Assert.Equal(0.3, DefenseBindingRules.Owed(Row(walled, 0.3)), 9);
    }

    // A build Hands reports refused (a permanent native refusal) spends the kind's refusal budget: the next admission of the
    // same kind is refused rather than re-ordered.
    [Fact]
    public void ARefusedBuildSpendsTheRefusalBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-defense-budget-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a") }, ct);
        var prefix = DefenseBindingRules.ChoicePrefix + Wire.Name(DefenseCandidateKind.TemporaryCover) + "-";
        journal.RecordPlannerReasons(new Dictionary<FilingKey, PlannerNote>(),
            [new FamilyProgress(LayoutFamily.FamilyId, true, [new NeedProgress(LayoutFamily.Defense, false, default, new(prefix + "0123abcd"))])], ct);
        var goal = journal.LoadNeed(LayoutFamily.Defense, World, ct);
        Assert.True(ReviewRefusalBudget.AdmitOwnerSubject(journal, goal, prefix, World, ct).Admitted);

        // The piece is the fixture's one priced building (WorkResourceTestFacts), standing in for a sandbag.
        var id = PlanIds.MintPlanId();
        var action = new ActionId(id.Value + "-0");
        var plan = new PlanSpec(id, new(1), [DomainAction.NewBuildingAction(action, new Building("TestWall", new Cell(22, 28), Rotation.North, "Rice"), ConstructionTierRules.PlannerTier(LayoutFamily.Defense.FilingKey, Phase.None))]);
        var commit = journal.CommitOwnerWork(goal, new MethodId(prefix + "0123abcd"), "", plan, ct);
        Assert.True(commit.Outcome == ClaimOutcome.Admitted, $"{commit.Outcome} {commit.Refusal} {commit.Conflict}");
        var scope = World with { Plan = id };
        journal.Prepare(id, action, scope, new(110), ct);
        var sent = journal.Dispatch(id, action, scope, new(110), ct);
        journal.RecordReceipts([new BatchReceipt(id, action, sent.View.Attempt, Receipt.Refused) { Refusal = new NativeRefusal("refused", "no path", RefusalClass.Permanent) }], ct);

        var after = journal.LoadNeed(LayoutFamily.Defense, World, ct);
        var (verdict, admitted) = ReviewRefusalBudget.AdmitOwnerSubject(journal, after, prefix, World, ct);
        Assert.False(admitted);
        Assert.Equal(Outcome.Refused, verdict.Outcome);
        // Another kind's budget is its own.
        Assert.True(ReviewRefusalBudget.AdmitOwnerSubject(journal, after, DefenseBindingRules.RemovalPrefix, World, ct).Admitted);
    }

    // A multi-cell piece's preview is checked against the footprint its definition's size occupies at its cell and rotation.
    [Fact]
    public void AMultiCellPiecesPreviewIsCheckedOverItsWholeFootprint()
    {
        var b = new Building("Turret_Autocannon", new Cell(10, 10), Rotation.North, "");
        var size = new Bounds(2, 2);
        var rect = InteriorRules.OccupiedRect(b.Cell, new Cell(2, 2), Rotation.North);
        var whole = new List<Cell>();
        for (var x = rect.X; x < rect.X + rect.Width; x++)
        {
            for (var z = rect.Z; z < rect.Z + rect.Height; z++)
            {
                whole.Add(new Cell(x, z));
            }
        }
        Assert.Equal(4, whole.Count);
        Assert.True(DefenseLayoutPlanner.ClassifyDefensePreview(Preview(b, whole), b, size).Ok);
        Assert.False(DefenseLayoutPlanner.ClassifyDefensePreview(Preview(b, [b.Cell]), b, size).Ok);
        Assert.True(DefenseLayoutPlanner.ClassifyDefensePreview(Preview(b, [b.Cell]), b, new Bounds(1, 1)).Ok);
        Assert.False(DefenseLayoutPlanner.ClassifyDefensePreview(Preview(b, [new Cell(30, 30)]), b, new Bounds(1, 1)).Ok);
        Assert.False(DefenseLayoutPlanner.ClassifyDefensePreview(Preview(b, whole, canPlace: false), b, size).Ok);
    }

    /// <summary>A native preview row of <paramref name="b"/> occupying <paramref name="cells"/>, with no blocker or interaction cell.</summary>
    private static RimGovernor.Brain.GameAccess.BuildingPreview Preview(Building b, IReadOnlyList<Cell> cells, bool canPlace = true)
    {
        var rotation = new RimGovernor.Brain.GameFacts.Placement.PlacementRotation
        {
            Accepted = true,
            Reason = "",
            OccupiedCells = [.. cells],
            BlockingThings = [],
            WatchCells = RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched,
            InteractionCells = [],
            Wind = new RimGovernor.Brain.GameFacts.Observations.BuildingWind.NotWindTurbine(),
        };
        var evaluated = new RimGovernor.Brain.GameFacts.Placement.PlacementEvaluated
        {
            CanPlace = canPlace,
            MadeFromStuff = false,
            ResearchFinished = true,
            BuildableByPlayer = true,
            CostList = [],
            Materials = new RimGovernor.Brain.GameFacts.Placement.PlacementMaterials { Rows = [] },
            Rotations = [rotation],
        };
        var action = DomainAction.NewBuildingAction(new ActionId("plan-0"), b, ConstructionTier.Sustain);
        return GameClient.PlacementRequests.BuildingPreviewRow(action, new GenerationSnapshot { Plan = new PlanId("plan"), Revision = new PlanRevision(1) }, new Tick(10), evaluated);
    }
}
