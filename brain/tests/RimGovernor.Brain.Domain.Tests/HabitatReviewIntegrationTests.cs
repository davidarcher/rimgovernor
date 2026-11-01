using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Rectangle = RimGovernor.Brain.Policy.Rectangle;
using Rig = RimGovernor.Brain.Domain.Tests.ColonyReviewRig;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 9.1 of redefine-habitat-requirements: normal-review integration regressions over one journal (<see cref="ColonyReviewRig"/>)
/// for accommodation, care, production, utility and defense work competing in the same colony. Each test is written against
/// the rule it names (RG-HABITAT-003, -004, -005, -009, -010, -011): a canonical cell, worker or input is spent once across
/// every family; a temporary provision answers the immediate need while the functional deficit stays open; a measured zero
/// and a missing fact decide differently; and bound chains survive review N+1, save and load with their ids while a changed
/// input re-selects only its own chain, and a rewind leaves no order, claim or receipt of what it rewound.
/// </summary>
public sealed class HabitatReviewIntegrationTests
{
    private static CancellationToken Ct => Rig.Ct;
    private static readonly PawnId P = new("p");

    /// <summary>Asserts a commit's outcome, naming its Method, conflict and refusal when it differs.</summary>
    private static NeedCommit Expect(ClaimOutcome expected, NeedCommit commit, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(commit))] string what = "")
    {
        Assert.True(commit.Outcome == expected, $"{what}: expected {expected}, got {commit.Outcome}: conflict={commit.Conflict?.Reason} refusal={commit.Refusal?.Kind}:{commit.Refusal?.Reason}");
        return commit;
    }

    private static DomainAction Rescue(string id, string rescuer, string bed = "ward") =>
        DomainAction.NewRescueAction(new(id), new Rescue(new(rescuer), P, bed));

    private static DomainAction BedRest(string id, string bed = "ward") =>
        DomainAction.NewBedRestAction(new(id), new BedRest(new("q"), bed));

    private static DomainAction Refuel(string id, string pawn, string generator) =>
        DomainAction.NewRecoveryServiceAction(new(id), new RecoveryService(new(pawn), generator, RecoveryMethod.Refuel));

    private static PowerFacts Empty(string pawn) => new()
    {
        Standing = false,
        Restoration = PowerRestorationTests.Request(PowerRestorationTests.Topology(PowerRestorationTests.Generator("gen")),
            PowerRestorationTests.Census(500, 1, PowerRestorationTests.Refueling("gen", pawn))),
    };

    // ---- (a) competing ground --------------------------------------------------------------------------------------------

    /// <summary>
    /// A bedroom bed, a hospital bed, a generator pad, a defense tier and a workshop table compete for one patch. Each cell goes
    /// to one claim whatever family asks: the tier and the table lose to the generator and the bed, named by the holder, and no
    /// two admitted orders share a cell. A dispatched bed keeps its cells until the native answers; its refusal releases
    /// them at once and the table is admitted, and still holds them at review N+1; re-committing an open Method continues it.
    /// </summary>
    [Fact]
    public void CompetingGroundGoesToOneClaimAndARefusedOrderReleasesIt()
    {
        using var rig = Rig.Open();
        var bed = Rig.Build("bed-0", Rig.Bed, 30, 30);
        var ward = Rig.Build("ward-0", Rig.HospitalBed, 32, 30);
        var pad = Rig.Build("gen-0", Rig.Generator, 31, 30);
        var tier = Rig.Build("bags-0", Rig.Sandbags, 31, 30);
        var table = Rig.Build("bench-0", Rig.Bench, 29, 31);
        // The geometry the competition rests on, read from the review's own footprints.
        Assert.Equal(["cell/30/31"], rig.Footprint(table).Intersect(rig.Footprint(bed)));
        Assert.Equal(["cell/31/30"], rig.Footprint(tier).Intersect(rig.Footprint(pad)));
        Assert.Empty(rig.Footprint(table).Intersect(rig.Footprint(ward)));
        Assert.Empty(rig.Footprint(table).Intersect(rig.Footprint(pad)));

        Expect(ClaimOutcome.Admitted, rig.Commit(ShelterFamily.Sleeping, "bedroom-bed", bed));
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.MedicalCareNeed, "ward-bed", ward));
        Expect(ClaimOutcome.Admitted, rig.Commit(PowerFamily.Basic, "generator-pad", pad));
        var defense = rig.Commit(LayoutFamily.Defense, "defense-tier", tier);
        Expect(ClaimOutcome.Conflict, defense);
        Assert.Equal(("cell/31/30", "generator-pad"), (defense.Conflict!.Claim, defense.Conflict.Method.Value));
        Assert.StartsWith("claim_conflict: cell/31/30 held by need ", defense.Conflict.Reason, StringComparison.Ordinal);
        var workshop = rig.Commit(EconomyFamily.Resource, "workshop-table", table);
        Expect(ClaimOutcome.Conflict, workshop);
        Assert.Equal(("cell/30/31", "bedroom-bed"), (workshop.Conflict!.Claim, workshop.Conflict.Method.Value));
        // The losers are deferred: nothing was written for them.
        Assert.Empty(rig.Journal.LoadNeed(LayoutFamily.Defense, rig.World, Ct).Methods);
        Assert.Empty(rig.Journal.LoadNeed(EconomyFamily.Resource, rig.World, Ct).Methods);
        DomainAction[] admitted = [bed, ward, pad];
        foreach (var (x, i) in admitted.Select((a, i) => (a, i)))
            foreach (var y in admitted.Skip(i + 1))
                Assert.Empty(rig.Footprint(x).Intersect(rig.Footprint(y)));

        // While the bed is only dispatched (no receipt yet) its cells stay claimed.
        var bedPlan = rig.PlanOf(ShelterFamily.Sleeping, "bedroom-bed");
        var attempt = rig.Dispatch(bedPlan, bed.Id);
        Expect(ClaimOutcome.Conflict, rig.Commit(EconomyFamily.Resource, "workshop-table", table));
        // The native refuses the bed: the work claims read the refused order as owning nothing (WorkRules.ActionAbsent), so the table takes its cells.
        Assert.Null(rig.Receive(bedPlan, bed.Id, attempt, Receipt.Refused).Error);
        Expect(ClaimOutcome.Admitted, rig.Commit(EconomyFamily.Resource, "workshop-table", table));

        // Review N+1: the refusal stays released and the table keeps the cells; the generator still holds its pad.
        rig.Stage(new Tick(110));
        Expect(ClaimOutcome.Continued, rig.Commit(EconomyFamily.Resource, "workshop-table", table));
        Expect(ClaimOutcome.Conflict, rig.Commit(LayoutFamily.Defense, "defense-tier", tier));
        Expect(ClaimOutcome.Continued, rig.Commit(PowerFamily.Basic, "generator-pad", pad));
        Expect(ClaimOutcome.Continued, rig.Commit(MedicalRules.MedicalCareNeed, "ward-bed", ward));
    }

    /// <summary>
    /// The refusal release isolated. A building order is an intent: its refused receipt ends it Unsuccessful and observed absent,
    /// its plan not retired. Claim admission skips the plan as no longer open work (its stage). The review's work claims release a
    /// closed building's cells by stage only once a later review's evidence is newer than the order (WorkResourceClaims' stage and
    /// tick clause); a review staged at the refusal's own tick is released by the refusal alone (WorkRules.ActionAbsent), and its
    /// census must not keep the refused bed's cells, or every reader of the claims (a generator, a free cell) treats them as held.
    /// </summary>
    [Fact]
    public void ARefusalAloneReleasesTheCellsAcrossTheNextReview()
    {
        using var rig = Rig.Open();
        var bed = Rig.Build("bed-0", Rig.Bed, 30, 30);
        var table = Rig.Build("bench-0", Rig.Bench, 29, 31);
        Expect(ClaimOutcome.Admitted, rig.Commit(ShelterFamily.Sleeping, "bedroom-bed", bed));
        var plan = rig.PlanOf(ShelterFamily.Sleeping, "bedroom-bed");
        rig.Stage(rig.Tick);
        Assert.NotEmpty(rig.Claims.Targets.Intersect(rig.Footprint(bed)));
        Assert.Null(rig.Refuse(plan, bed.Id).Error);
        var held = rig.Journal.LoadPlan(plan, Ct);
        var refused = Assert.Single(held.Progress).View;
        Assert.Equal((Stage.Unsuccessful, true, true, false), (refused.Stage, refused.Received(Receipt.Refused), refused.Observes(Effect.Absent), held.Retired));
        Assert.Equal(rig.Tick, refused.Tick);

        // Review N+1 at the refusal's tick: the stage and tick clause cannot release (the evidence is not newer than the order).
        rig.Stage(rig.Tick);
        Assert.Empty(rig.Claims.Targets.Intersect(rig.Footprint(bed)));
        Expect(ClaimOutcome.Admitted, rig.Commit(EconomyFamily.Resource, "workshop-table", table));
        rig.Stage(new Tick(110));
        Expect(ClaimOutcome.Continued, rig.Commit(EconomyFamily.Resource, "workshop-table", table));
    }

    // ---- (b) competing inputs and workers --------------------------------------------------------------------------------

    /// <summary>
    /// Ten rice, a six-rice workshop wall and a six-rice production batch (RG-HABITAT-003): a blueprint spends nothing until
    /// delivery, so a second wall is still placed, but the placed wall holds its cost against other work and the batch is
    /// refused on the shared materials ledger. Once the walls' orders are refused their rice is returned and the next review
    /// admits the batch. The bug it guards: the placed cost not counted against other work, so one stock was promised twice.
    /// </summary>
    [Fact]
    public void OneStockOfMaterialsIsSpentOnceAndARefusedOrderReturnsIt()
    {
        using var rig = Rig.Open(Rig.Colony(rice: 10));
        var wall = Rig.Build("wall-0", Rig.Wall, 40, 40);
        var batch = DomainAction.NewProductionBillAction(new("batch-0"), new("bench", "TestRecipe", BillMode.GearBatch, 1, "Rice"));
        Expect(ClaimOutcome.Admitted, rig.Commit(LayoutFamily.Defense, "defense-wall", wall));
        Expect(ClaimOutcome.Admitted, rig.Commit(ShelterFamily.SafeArea, "second-wall", Rig.Build("wall-1", Rig.Wall, 44, 40)));
        rig.Stage(new Tick(105));
        var refused = rig.Commit(AdminFamily.WorkLedger, "craft-batch", batch);
        Expect(ClaimOutcome.Refused, refused);
        Assert.Contains(refused.Refusal!.Kind, new[] { RefusalKind.ProductionMaterial, RefusalKind.SharedAdmission });
        Assert.Empty(rig.Journal.LoadNeed(AdminFamily.WorkLedger, rig.World, Ct).Methods);

        Assert.Null(rig.Refuse(rig.PlanOf(LayoutFamily.Defense, "defense-wall"), wall.Id).Error);
        Assert.Null(rig.Refuse(rig.PlanOf(ShelterFamily.SafeArea, "second-wall"), new("wall-1")).Error);
        rig.Stage(new Tick(110));
        Expect(ClaimOutcome.Admitted, rig.Commit(AdminFamily.WorkLedger, "craft-batch", batch));
    }

    /// <summary>
    /// One worker interval is spent once (RG-HABITAT-003, design decision 3). The power family's real runner orders the doctor
    /// to refuel the generator; the doctor carrying a downed patient to the ward is then the same pawn's second exact order and
    /// is refused naming the refuel; the other way round the refuel is refused. A refused refuel frees the doctor at the next
    /// review. The bug: a rescue claimed the rescuer only as a care subject (<c>care/doc</c>), never as a pawn, so a rescue and
    /// a refuel, repair or fix by one pawn were both admitted and the second native job order replaced the first.
    /// </summary>
    [Fact]
    public void OneWorkerIsClaimedOnceAcrossCareAndUtilityWork()
    {
        using (var rig = Rig.Open())
        {
            rig.Power = Empty("doc");
            var refuel = Assert.Single(rig.RunPower().Methods);
            Assert.Equal(("refuel-gen-doc", ClaimOutcome.Admitted), (refuel.Method.Value, refuel.Outcome));
            var carried = rig.Commit(MedicalRules.CriticalNeed(P), "rescue-p/ward", Rescue("rescue-0", "doc"));
            Expect(ClaimOutcome.Conflict, carried);
            Assert.Equal(("pawn/doc", "refuel-gen-doc"), (carried.Conflict!.Claim, carried.Conflict.Method.Value));
            var tended = rig.Commit(MedicalRules.CriticalNeed(P), "tend-p", DomainAction.NewTendAction(new("tend-0"), new Tend(new("doc"), P)));
            Assert.Equal(("pawn/doc", ClaimOutcome.Conflict), (tended.Conflict?.Claim, tended.Outcome));
            // Another rescuer is free.
            Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(P), "rescue-p/ward", Rescue("rescue-1", "a")));

            var action = Assert.Single(rig.Journal.LoadPlan(refuel.Plan, Ct).Progress).Action.Id;
            Assert.Null(rig.Refuse(refuel.Plan, action).Error);
            rig.Stage(new Tick(110));
            Assert.Equal(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(new("q")), "tend-q",
                DomainAction.NewTendAction(new("tend-1"), new Tend(new("doc"), new("q")))).Outcome);
        }

        using (var rig = Rig.Open())
        {
            Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(P), "rescue-p/ward", Rescue("rescue-0", "doc")));
            rig.Power = Empty("doc");
            var run = rig.RunPower();
            Assert.DoesNotContain(run.Methods, m => m.Outcome == ClaimOutcome.Admitted);
            Assert.Empty(rig.Journal.LoadNeed(PowerFamily.Basic, rig.World, Ct).Methods);
        }
    }

    // ---- (c) functional deficits stay visible ----------------------------------------------------------------------------

    /// <summary>
    /// An accepted refuel restores nothing by itself: the fake native records the one order, the next review's power Need is
    /// still unmet with its deficit while the native reports the running job, orders nothing twice, and only fresh powered
    /// facts close it.
    /// </summary>
    [Fact]
    public async Task AnAcceptedUtilityOrderLeavesItsNeedOpenUntilServiceIsObserved()
    {
        using var rig = Rig.Open();
        rig.Power = Empty("a");
        var method = Assert.Single(rig.RunPower().Methods);
        var progress = Assert.Single(rig.Journal.LoadPlan(method.Plan, Ct).Progress);
        await rig.HandsAsync(method.Plan, progress.Action.Id);
        Assert.Equal(ActionKind.RecoveryService, Assert.Single(rig.Native.Written).Kind);

        // Review N+1: the native reports the pawn's running refuel job, and the generator is still unpowered.
        rig.Stage(new Tick(110));
        rig.Power = rig.Power with { Restoration = rig.Power.Restoration with { Underway = [new("gen", ServicingKind.Refuel, "a", false)] } };
        var next = rig.RunPower();
        var need = Assert.Single(next.Standings, s => s.Need.Id == PowerFamily.Basic).Need;
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        Assert.True(need.Deficit > 0);
        Assert.DoesNotContain(next.Methods, m => m.Outcome == ClaimOutcome.Admitted);
        Assert.Single(rig.Native.Written);

        rig.Power = rig.Power with { Standing = true };
        Assert.Equal(Satisfaction.Met, Assert.Single(rig.RunPower().Standings, s => s.Need.Id == PowerFamily.Basic).Need.Satisfaction);
    }

    private static ReviewFacts StandIn(ReviewFacts f)
    {
        var a = SleepingStandInTests.A;
        var facts = f with
        {
            Census = f.Census with { ColonistCount = 1, BedCapacity = 1, IndoorSleepingCapacity = 1 },
            Upkeep = f.Upkeep with { Rooms = RoomFixtures.Census(SleepingStandInTests.Standing("bedroom", SleepingStandInTests.Bedroom, "x")) },
            LayoutPlan = SleepingStandInTests.OneBedroom,
            Sleeping = new SleepingObservation
            {
                Colonists = 1,
                // The colonist owns and sleeps on the spot at a comfortable temperature, yet a spot is never a rung of the bed
                // ladder (SleepingRules.SleepingRung): it answers tonight, it is not the colonist's bed.
                People = [GuestAccommodationTests.Person(a, "x")],
                Beds = [new SleepingBed
                {
                    Native = SleepingNativeFixtures.Bed("x", temperature: 21, owners: [a], users: [a], accessibleTo: [a], room: "bedroom"),
                    Building = SleepingNativeFixtures.Building("x", definition: new Resource(SleepingRules.SleepingSpotDefinition), roofed: true),
                    Humanlike = true,
                    RestEffectiveness = 0.8,
                }],
            },
        };
        return facts with { BedroomsOwed = SleepingPlanningRules.BedroomsOwedOf(facts, [], null) };
    }

    private static ReviewFacts Housed(ReviewFacts f)
    {
        var a = SleepingStandInTests.A;
        var facts = f with
        {
            Census = f.Census with { ColonistCount = 1, BedCapacity = 1, IndoorSleepingCapacity = 1 },
            Upkeep = f.Upkeep with { Rooms = RoomFixtures.Census((RoomFixtures.State("r1", temperatureC: 21), new[] { new Cell(1, 1), new Cell(2, 1) })) },
            LayoutPlan = null,
            Sleeping = new SleepingObservation
            {
                Colonists = 1,
                People = [GuestAccommodationTests.Person(a, "w1")],
                Beds = [GuestAccommodationTests.Bed("w1", owners: [a], users: [a])],
                BedBuildable = true,
            },
        };
        return facts with { BedroomsOwed = SleepingPlanningRules.BedroomsOwedOf(facts, [], null) };
    }

    private static Need Sleeping(Rig rig) => ShelterRules.SleepingNeedOf(ShelterFacts.From(rig.Reviewed, Rules.DefaultPlannerSettings()));

    /// <summary>
    /// The colonist sleeps on a stand-in spot inside the planned bedroom, owned, used and comfortable. The immediate need is answered (no stand-in is owed),
    /// yet the review keeps shelter/sleeping unmet with its whole deficit because the bedroom's bed is owed; accepting the
    /// stand-in's placement changes nothing. Only a review observing the colonist in its own suitable bed meets it.
    /// </summary>
    [Fact]
    public void ATemporaryProvisionAnswersTheImmediateNeedButKeepsTheFunctionalDeficitOpen()
    {
        using var rig = Rig.Open();
        rig.Stage(new Tick(120), StandIn);
        Assert.Equal(0, ShelterRules.StandInsOwed(rig.Reviewed).Missing);
        Assert.True(rig.Reviewed.BedroomsOwed);
        Assert.False(rig.Reviewed.SleepingRecovered);
        var open = Sleeping(rig);
        Assert.Equal((Satisfaction.Unmet, true), (open.Satisfaction, open.Deficit > 0));

        var spot = Rig.Build("spot-0", SleepingRules.SleepingSpotDefinition, 12, 1);
        Expect(ClaimOutcome.Admitted, rig.Commit(ShelterFamily.Sleeping, "stand-in", spot));
        Assert.Equal(Stage.Completed, rig.Accept(rig.PlanOf(ShelterFamily.Sleeping, "stand-in"), spot.Id).View.Stage);
        rig.Stage(new Tick(130));
        Assert.Equal(Satisfaction.Unmet, Sleeping(rig).Satisfaction);

        rig.Stage(new Tick(140), Housed);
        Assert.False(rig.Reviewed.BedroomsOwed);
        Assert.True(rig.Reviewed.SleepingRecovered);
        Assert.Equal(Satisfaction.Met, Sleeping(rig).Satisfaction);
    }

    // The barracks stand-in: the colonist sleeps, owning nothing, on a spot in an enclosed barracks room while the plan's bedroom is owed.
    private static readonly PlannedRoom Barracks = SleepingStandInTests.Planned(3, PlannedRole.Barracks, 20);

    private static SleepingBed Slot(string id, string definition, string room, PawnId[] owners, PawnId[] users) => new()
    {
        Native = SleepingNativeFixtures.Bed(id, temperature: 21, owners: [.. owners], users: [.. users], accessibleTo: [SleepingStandInTests.A], room: room),
        Building = SleepingNativeFixtures.Building(id, definition: new Resource(definition), roofed: true),
        Humanlike = true,
        RestEffectiveness = definition == SleepingRules.SleepingSpotDefinition ? 0.8 : 1,
    };

    private static ReviewFacts Sheltered(ReviewFacts f, bool bedroom, bool spot = true)
    {
        var a = SleepingStandInTests.A;
        (RoomState, IEnumerable<Cell>) barracks = (RoomFixtures.State("barracks", RoomRole.Barracks, beds: spot ? ["x"] : []), Barracks.Region.Cells());
        var facts = f with
        {
            Census = f.Census with { ColonistCount = 1, BedCapacity = 1, IndoorSleepingCapacity = 1 },
            // The flooring census read empty: the bedroom upgrades it prices find nothing to floor.
            Upkeep = f.Upkeep with { Flooring = new(), Rooms = bedroom ? RoomFixtures.Census(barracks, SleepingStandInTests.Standing("bedroom", SleepingStandInTests.Bedroom, "w1")) : RoomFixtures.Census(barracks) },
            LayoutPlan = SleepingStandInTests.OneBedroom,
            Sleeping = new SleepingObservation
            {
                Colonists = 1,
                People = [GuestAccommodationTests.Person(a, bedroom ? "w1" : "")],
                Beds = bedroom
                    ? [.. spot ? [Slot("x", SleepingRules.SleepingSpotDefinition, "barracks", [], [])] : Array.Empty<SleepingBed>(), Slot("w1", "Bed", "bedroom", [a], [a])]
                    : [Slot("x", SleepingRules.SleepingSpotDefinition, "barracks", [], [a])],
                BedBuildable = true,
            },
        };
        return facts with { BedroomsOwed = SleepingPlanningRules.BedroomsOwedOf(facts, [], []) };
    }

    private static BedroomStep NextBedroomStep(ReviewFacts f) => SleepingRules.NextBedroomStep(f.LayoutPlan!, f.Upkeep.Rooms!, f.Sleeping!,
        new Dictionary<string, RoomTarget>(), new Dictionary<PawnId, TraitEffects>(), new Dictionary<PawnId, double>());

    /// <summary>
    /// A barracks stand-in is temporary capacity: the colonist sleeps under a roof on a spot in an enclosed barracks room, so no
    /// stand-in is owed, but the plan's bedroom still owes its bed and shelter/sleeping stays unmet with its deficit. While the
    /// barracks spot is the colonist's only roofed place it is not cleared. An accepted bedroom bed order changes neither; the
    /// review observing the colonist in its own bed in the standing bedroom makes the vacant barracks spot the next bedroom step's
    /// clear (so the bedroom work is still owed), and the review that observes the spot gone meets the Need.
    /// </summary>
    [Fact]
    public void ABarracksStandInMeetsNoBedroomNeedAndStandsWhileItIsTheOnlyRoof()
    {
        using var rig = Rig.Open();
        rig.Stage(new Tick(120), f => Sheltered(f, bedroom: false));
        Assert.Equal(0, ShelterRules.StandInsOwed(rig.Reviewed).Missing);
        Assert.True(rig.Reviewed.BedroomsOwed);
        Assert.False(rig.Reviewed.SleepingRecovered);
        var open = Sleeping(rig);
        Assert.Equal((Satisfaction.Unmet, true), (open.Satisfaction, open.Deficit > 0));
        Assert.NotEqual(BedroomStepKind.Clear, NextBedroomStep(rig.Reviewed).Kind);

        var bed = Rig.Build("w1", Rig.Bed, 11, 1);
        Expect(ClaimOutcome.Admitted, rig.Commit(ShelterFamily.Sleeping, "bedroom-bed", bed));
        Assert.Equal(Stage.Completed, rig.Accept(rig.PlanOf(ShelterFamily.Sleeping, "bedroom-bed"), bed.Id).View.Stage);
        rig.Stage(new Tick(130));
        var accepted = Sleeping(rig);
        Assert.Equal((Satisfaction.Unmet, open.Deficit), (accepted.Satisfaction, accepted.Deficit));
        Assert.NotEqual(BedroomStepKind.Clear, NextBedroomStep(rig.Reviewed).Kind);

        rig.Stage(new Tick(140), f => Sheltered(f, bedroom: true));
        var clear = NextBedroomStep(rig.Reviewed);
        Assert.Equal((BedroomStepKind.Clear, "x"), (clear.Kind, clear.Bed));
        Assert.True(rig.Reviewed.BedroomsOwed);
        Assert.Equal(Satisfaction.Unmet, Sleeping(rig).Satisfaction);

        rig.Stage(new Tick(150), f => Sheltered(f, bedroom: true, spot: false));
        Assert.Equal(BedroomStepKind.None, NextBedroomStep(rig.Reviewed).Kind);
        Assert.False(rig.Reviewed.BedroomsOwed);
        Assert.True(rig.Reviewed.SleepingRecovered);
        Assert.Equal(Satisfaction.Met, Sleeping(rig).Satisfaction);
    }

    // The field medical spot: P bleeds out before any carry to the ward ends, Q is carried to the ward.
    private static readonly PawnId Q = new("q");
    private static readonly Cell SpotCell = new(3, 3);
    private const string SpotSite = "field_spot:3,3";

    private static SupportCareBed CareBed(string id, CareMovement movement, Cell cell, double heal = 0) => new(id, cell, 1)
    {
        Medical = true, Prisoners = false, Slaves = false, Humanlike = true, Occupants = [], UsableNow = true, Movement = movement,
        InfectionChanceFactor = id == "spot" ? 1 : .5, Temperature = CoupledFixtures.Comfortable, HealPerDay = heal, SurgerySuccessFactor = new CareSurgeryFactor.Read(1),
    };

    private static SupportWorker CareTend(string patient, string bed, double quality) =>
        new("care-" + patient, "doc", patient, "TendPatient", "DoctorTend", new(2, 2), 10, 20) { CareBed = bed, Medicine = null, Treatment = CoupledFixtures.Tend(quality, new InfectionCheck.Pending(50000)) };

    private static SleepingBed WardBed(string id, Cell cell, string room) => new()
    {
        Native = SleepingNativeFixtures.Bed(id, temperature: 21, accessibleTo: [P, Q], room: room, medical: true),
        Building = SleepingNativeFixtures.Building(id, definition: new Resource(id == "spot" ? SleepingRules.SleepingSpotDefinition : "Bed"), cell: cell, roofed: true),
        Humanlike = true,
        RestEffectiveness = 1,
    };

    /// <summary>
    /// The care comparison's request at one review. P is downed and bleeding where it lies (before the placement) or lies in the
    /// medical spot once it stands; <paramref name="tended"/> is the census reading P's tend (no longer tendable or bleeding).
    /// Q, downed but stable, is carried to the ward. The ward room hosts ward and ward2; the spot stands in no room.
    /// </summary>
    private static CareCapacityRequest Field(bool standing, bool tended, IReadOnlyList<CareFieldChain> chains)
    {
        SupportCareBed[] pBeds = standing
            ? [CareBed("spot", new CareMovement.AlreadyHere(), SpotCell), CareBed("ward2", tended ? new CareMovement.Walk(300) : new CareMovement.Carried("r", 2000), new(9, 8), heal: 10)]
            : [CareBed("ward2", new CareMovement.Carried("r", 2000), new(9, 8), heal: 10)];
        var p = CoupledFixtures.CarePatient("care-p", "p", BedOccupant.Colonist, standing ? new CarePosture.InBed("spot") : new CarePosture.Lying(), CareConduct.Calm, pBeds)
            with
            {
                Bleeding = tended ? new CareBleeding.Stable() : new CareBleeding.Bleeding(1, 1000),
                Tendable = !tended,
                FieldSpot = new CareFieldSpot.Site(SpotSite, SpotCell, new CareMovement.Carried("r", 40), 1, CoupledFixtures.Comfortable, 0),
            };
        var q = CoupledFixtures.CarePatient("care-q", "q", BedOccupant.Colonist, new CarePosture.Lying(), CareConduct.Calm,
            CareBed("ward", new CareMovement.Carried("r", 200), new(9, 9))) with { Bleeding = new CareBleeding.Stable() };
        SleepingBed[] beds = standing
            ? [WardBed("ward", new(9, 9), "r1"), WardBed("ward2", new(9, 8), "r1"), WardBed("spot", SpotCell, "")]
            : [WardBed("ward", new(9, 9), "r1"), WardBed("ward2", new(9, 8), "r1")];
        SupportDemand[] demands = tended
            ? [CoupledFixtures.Demand("care-q", "care", "q", new(1, 1), 0, 0)]
            : [CoupledFixtures.Demand("care-p", "care", "p", new(1, 1), 0, 0), CoupledFixtures.Demand("care-q", "care", "q", new(1, 1), 0, 0)];
        SupportWorker[] workers = tended
            ? [CareTend("q", "ward", .9)]
            : [CareTend("p", "ward2", .9), CareTend("p", standing ? "" : SpotSite, .5), CareTend("q", "ward", .9)];
        return new CareCapacityRequest
        {
            Support = CoupledFixtures.NoSupport with { Demands = [.. demands], Workers = [.. workers], Patients = [p, q] },
            Sleeping = new() { Colonists = 2, Beds = [.. beds] },
            Rooms = RoomFixtures.Census(RoomFixtures.State("r1", RoomRole.Hospital, beds: ["ward", "ward2"])),
            FieldSpots = [.. chains],
        };
    }

    private static string Show(CareCapacityDecision d) =>
        $"chosen {d.Chosen.Kind}:{d.Chosen.Bed}; " + string.Join("; ", d.Options.Select(o => $"{o.Kind}:{o.Bed}={o.Assessment}"));

    private static EmergencyPawn Patient(string id, bool downed, bool bleeding)
    {
        var row = EmergencyFixtures.Colonist(id);
        return row with { Observed = row.Observed with { Downed = downed, Health = PawnFixtures.Healthy with { Bleeding = bleeding, NeedsTend = bleeding } } };
    }

    /// <summary>The critical Needs of the emergency census at the staged review, as the medical family derives them.</summary>
    private static Dictionary<string, Need> Critical(Rig rig, params EmergencyPawn[] census) =>
        MedicalRules.CriticalNeedsOf(new MedicalFacts
        {
            Critical = EmergencyNeedsRules.CriticalPatientIds(EmergencySnapshot.New(rig.World, rig.Tick, new EmergencyFacts { Colonists = census }), rig.World, rig.Tick, []),
        }).ToDictionary(n => n.Id.Value, StringComparer.Ordinal);

    private static ReviewFacts Resting(ReviewFacts f) => f with
    {
        MedicalPawns = [CareFixtures.Patient("p") with { Observed = CareFixtures.NativePatient("p") with { Health = PawnFixtures.Healthy with { ShouldSeekMedicalRest = true } } }],
    };

    /// <summary>
    /// A medical spot placed where P lies (RG-HABITAT-004/005): P bleeds out before the carry to the ward ends, so the comparison
    /// places the spot while Q is carried to the ward, each chain under its own Need on the shared journal. The accepted placement and
    /// the accepted tend credit no treatment: P's critical Need stays unmet with its deficit until the census reads the tend, and the
    /// care Need the hospital beds answer stays unmet (P still needs medical rest; the spot is no hosted ward bed) after it. Once the
    /// census reads P tended, the comparison re-selects only P's chain (a walk to the free ward bed): Q's decision and its carry are
    /// unchanged and its rescue continues under its own plan, while P's walk takes over the care Need and P's placed spot stays read.
    /// </summary>
    [Fact]
    public void AFieldSpotCreditsTreatmentOnlyWhenTheTendIsObservedAndATransferReselectsOnlyItsPatient()
    {
        using var rig = Rig.Open();
        NeedOwner Care() => rig.Journal.LoadNeed(MedicalRules.MedicalCareNeed, rig.World, Ct);
        rig.Stage(new Tick(110), Resting);
        var first = CareCapacityRules.Plan(Field(standing: false, tended: false, []));
        var pFirst = Assert.Single(first.Decisions, d => d.Patient == "p");
        var qFirst = Assert.Single(first.Decisions, d => d.Patient == "q");
        Assert.Equal((CareCapacityKind.FieldSpot, SpotSite), (pFirst.Chosen.Kind, pFirst.Chosen.Bed));
        Assert.Equal((CareCapacityKind.Reuse, "ward"), (qFirst.Chosen.Kind, qFirst.Chosen.Bed));
        Assert.Equal([new CareFieldPlacement("p", SpotCell)], first.Placements);
        var carryQ = Assert.Single(CareTransportRules.Of(first, Field(false, false, []).Support));
        Assert.Equal(new CareTransport.Carried("q", "ward"), carryQ);

        // Each chain binds under its own Need on the one journal: the placement under care, Q's rescue under Q's critical Need.
        var spot = DomainAction.NewBuildingAction(new("spot-0"), new(SleepingRules.SleepingSpotDefinition, SpotCell, Rotation.South, ""), ConstructionTier.Expand);
        var field = HospitalPlanner.FieldMethodOf("p").Value;
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.MedicalCareNeed, field, spot));
        var rescueQ = DomainAction.NewRescueAction(new("rescue-q"), new Rescue(new("b"), Q, "ward"));
        var rescue = HospitalPlanner.TransportMethodOf(carryQ).Value;
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(Q), rescue, rescueQ));
        var rescuePlan = rig.PlanOf(MedicalRules.CriticalNeed(Q), rescue);
        Assert.Equal([new CareFieldChain("p", SpotCell, false)], HospitalPlanner.FieldSpots(rig.Journal, Care(), rig.World, Ct).Select(s => s.Chain));

        // The native accepts the placement and the tend: the chain reads placed, yet nothing observed says P was treated.
        var fieldPlan = rig.PlanOf(MedicalRules.MedicalCareNeed, field);
        Assert.Equal(Stage.Completed, rig.Accept(fieldPlan, spot.Id).View.Stage);
        var tend = DomainAction.NewTendAction(new("tend-p"), new Tend(new("doc"), P));
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(P), "tend-p", tend));
        rig.Accept(rig.PlanOf(MedicalRules.CriticalNeed(P), "tend-p"), tend.Id);
        rig.Stage(new Tick(120));
        var chains = HospitalPlanner.FieldSpots(rig.Journal, Care(), rig.World, Ct).Select(s => s.Chain!).ToArray();
        Assert.Equal([new CareFieldChain("p", SpotCell, true)], chains);
        var untended = Critical(rig, Patient("p", downed: true, bleeding: true), Patient("q", downed: true, bleeding: false));
        var pNeed = untended[MedicalRules.CriticalNeed(P).Value];
        Assert.Equal((Satisfaction.Unmet, 1.0), (pNeed.Satisfaction, pNeed.Deficit));
        var placed = Field(standing: true, tended: false, chains);
        var held = CareCapacityRules.Plan(placed);
        var pHeld = Assert.Single(held.Decisions, d => d.Patient == "p");
        Assert.True(pHeld.Chosen.Bed == "spot", Show(pHeld));
        Assert.DoesNotContain(CareTransportRules.Of(held, placed.Support), t => t.Patient == "p");
        // The medical spot stands in no room: the ward counts its two hosted beds only.
        Assert.Equal(2, HospitalRules.HostedMedicalBeds(placed.Rooms, placed.Sleeping, false));
        Assert.Equal(Satisfaction.Unmet, MedicalRules.MedicalCareNeedOf(new MedicalFacts { CareRecovered = rig.Reviewed.MedicalCareRecovered }).Satisfaction);

        // Review N+2 reads P tended: its critical Need is gone, the care Need stays open, and only P's chain is re-selected.
        rig.Stage(new Tick(130));
        var tendedNeeds = Critical(rig, Patient("p", downed: false, bleeding: false), Patient("q", downed: true, bleeding: false));
        Assert.Equal([MedicalRules.CriticalNeed(Q).Value], tendedNeeds.Keys);
        Assert.Equal(Satisfaction.Unmet, MedicalRules.MedicalCareNeedOf(new MedicalFacts { CareRecovered = rig.Reviewed.MedicalCareRecovered }).Satisfaction);
        var recovering = Field(standing: true, tended: true, chains);
        var later = CareCapacityRules.Plan(recovering);
        var pLater = Assert.Single(later.Decisions, d => d.Patient == "p");
        Assert.True((pLater.Chosen.Kind, pLater.Chosen.Bed) == (CareCapacityKind.Reuse, "ward2"), Show(pLater));
        Assert.Equal(qFirst, Assert.Single(later.Decisions, d => d.Patient == "q"));
        var transports = CareTransportRules.Of(later, recovering.Support);
        Assert.Equal(new CareTransport[] { new CareTransport.Walk("p", "ward2"), carryQ }, transports.OrderBy(t => t.Patient, StringComparer.Ordinal));

        var walk = HospitalPlanner.TransportMethodOf(new CareTransport.Walk("p", "ward2")).Value;
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.MedicalCareNeed, walk, DomainAction.NewBedRestAction(new("walk-p"), new BedRest(P, "ward2"))));
        Expect(ClaimOutcome.Continued, rig.Commit(MedicalRules.CriticalNeed(Q), rescue, rescueQ));
        Assert.Equal(rescuePlan, rig.PlanOf(MedicalRules.CriticalNeed(Q), rescue));
        // P's walk is now the care Need's Method; its placed spot is still read from the journal as P's, under its own plan.
        Assert.Equal(walk, Assert.Single(rig.Journal.LoadNeed(MedicalRules.MedicalCareNeed, rig.World, Ct).Methods).Method.Value);
        Assert.Equal(fieldPlan, rig.Journal.OwnerMethodPlan(Care(), new MethodId(field), Ct));
        Assert.Equal(chains, HospitalPlanner.FieldSpots(rig.Journal, Care(), rig.World, Ct).Select(s => s.Chain!));
    }

    // ---- (d) a known zero is not a missing fact --------------------------------------------------------------------------

    /// <summary>
    /// The defense comparison's free builders come from the review's own claims census. With every worker claimed (two refuels
    /// and the doctor's rescue) the review measures zero free builders; with the claims unread the term is unassessed naming
    /// the read. The cover line is priced differently in each, and neither is ever read as the other.
    /// </summary>
    [Fact]
    public void ZeroFreeBuildersIsAssessedAndUnreadClaimsAreUnassessedAndTheyDecideDifferently()
    {
        using var rig = Rig.Open();
        var plan = DefenseBindingTests.Workshop();
        var values = DefenseBindingTests.Inputs().Values;
        var harm = new DefenseHarm.Read(1);
        var free = Assert.IsType<DefenseTerm.Assessed>(DefenseChoiceRules.InputsOf(rig.Reviewed, plan, harm, values).Builders);
        Assert.Equal(Rig.Pawns.Length, free.Value);

        Expect(ClaimOutcome.Admitted, rig.Commit(PowerFamily.Basic, "refuel-a", Refuel("r-a", "a", "gen-a")));
        Expect(ClaimOutcome.Admitted, rig.Commit(PowerFamily.Basic, "refuel-b", Refuel("r-b", "b", "gen-b")));
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(P), "rescue-p/ward", Rescue("rescue-0", "doc")));
        rig.Stage(new Tick(110));
        Assert.Equal(["a", "b", "doc"], rig.Claims.Pawns);
        var zero = DefenseChoiceRules.InputsOf(rig.Reviewed, plan, harm, values).Builders;
        Assert.Equal(new DefenseTerm.Assessed(0), zero);
        var unread = DefenseChoiceRules.InputsOf(rig.Reviewed with { WorkView = null }, plan, harm, values).Builders;
        Assert.Equal(new DefenseTerm.Unassessed(DefenseChoiceRules.WorkClaimsUnread), unread);

        var cover = DefenseBindingTests.Cover;
        DefenseAlternative[] alternatives =
        [
            new(new DefenseCandidate { Kind = DefenseCandidateKind.Retain, Key = "retain" }, DefenseBindingTests.Guard("retain", 0), null),
            new(cover, DefenseBindingTests.Guard(cover.Key, 0.8), DefenseBindingTests.Guard(cover.Key + DefenseChoiceRules.StandingSuffix, 0)),
        ];
        var measured = DefenseChoiceRules.Choose(alternatives, DefenseBindingTests.Inputs() with { Builders = zero }).Rows.Single(r => r.Key == cover.Key);
        var missing = DefenseChoiceRules.Choose(alternatives, DefenseBindingTests.Inputs() with { Builders = unread }).Rows.Single(r => r.Key == cover.Key);
        Assert.IsNotType<DefensePrice.Unassessed>(measured.Price);
        Assert.Equal(new DefensePrice.Unassessed(DefenseChoiceRules.WorkClaimsUnread), missing.Price);
    }

    /// <summary>
    /// A fix's component stock counted at zero excludes the fix (no stock) and names the acquisition; the stock unread leaves the
    /// fix unassessed naming the read. A field's ground read as fully taken defers naming the land; ground never read raises the
    /// required fact instead of being taken as none.
    /// </summary>
    [Fact]
    public void ACountedZeroStockAndUnreadGroundAreNotTheMissingFact()
    {
        var topology = PowerRestorationTests.Topology(PowerRestorationTests.Generator("gen", empty: false, broken: true));
        var counted = PowerRestorationTests.Census(0, 0, PowerRestorationTests.Fixing("gen", "a"));
        var none = Assert.Single(PowerRestorationRules.Decide(PowerRestorationTests.Request(topology, counted)).Decisions);
        var unknown = Assert.Single(PowerRestorationRules.Decide(PowerRestorationTests.Request(topology,
            counted with { Components = new ServicingComponents.Unread("servicing_stock:ComponentIndustrial") })).Decisions);
        ServicingStanding Fix(ServicingDecision d) => d.Alternatives.Single(a => a.Kind == ServicingChoiceKind.FixBreakdown).Standing;
        Assert.Equal(new ServicingStanding.Excluded(ServicingRules.NoStock), Fix(none));
        Assert.Equal(new ServicingStanding.Unassessed("servicing_stock:ComponentIndustrial"), Fix(unknown));
        Assert.NotEqual(ServicingChoiceRules.Describe(none), ServicingChoiceRules.Describe(unknown));

        var cells = FieldPlacementTests.Row(0, 3);
        var demand = new[] { FieldPlacementTests.Demand(FieldPlacementTests.Food, FieldPlacementTests.Crop("rice"), 4) };
        var taken = FieldPlacementRules.Choose(FieldPlacementTests.Request([new(0, 0, 4)], cells, demand, [.. cells.Select(c => c.Cell)])).Decisions[0];
        Assert.Null(taken.Chosen);
        Assert.Equal("land:rice", taken.Reason);
        Assert.Throws<RequiredFactException>(() => FieldPlacementRules.Choose(FieldPlacementTests.Request([new(0, 0, 4)], cells, demand) with { Cells = null }));
    }

    // ---- (e) chains survive review N+1, save and load; (f) rewind -------------------------------------------------------

    private static readonly InteriorPiece Bridge = new()
    {
        Slot = "bench", Def = "TestSpot", Size = new Cell(1, 1), Rot = Rotation.North, Rect = new Rectangle { X = 5, Z = 5, Width = 1, Height = 1 },
    };

    /// <summary>
    /// One held plan carrying every kind of bound chain: room 1 hosts the shirt through a spot-then-table chain, room 2 the knife
    /// at the smithy with the spare's retirement depending on it and a bed relocation, and a bound sandbag line.
    /// </summary>
    private static LayoutPlan Habitat()
    {
        var host = PlannedRoomSelectionTests.HostRoom with
        {
            Production = [new(PlannedRoomSelectionTests.HostBench, PlannedRoomSelectionTests.HostRecipe, 2), new("TestSpot", PlannedRoomSelectionTests.HostRecipe, 2) { Step = HostStep.Bridge }],
        };
        var smithy = Assert.Single(TransitionBindingTests.Workshop().Rooms) with { Id = 2 };
        var plan = PlannedRoomSelectionTests.HostPlan with
        {
            Rooms = [host, smithy],
            NextRoomId = 3,
            Entrances = [new Cell(22, 26)],
            Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [1] = [PlannedRoomSelectionTests.HostPiece, Bridge] },
            Transitions = [TransitionBindingTests.Move, TransitionBindingTests.Retire],
        };
        return DefenseBindingRules.Bind(plan, DefenseBindingTests.Cover, 0.25);
    }

    private static readonly HashSet<(string, Cell)> Nothing = [];

    private static WantedPiece HostNext(LayoutPlan plan) =>
        Assert.IsType<HostBinding.Build>(PlannedRules.HostBindingOf(plan, PlannedRoomSelectionTests.HostRecipe, new(), _ => true, Nothing, Nothing)).Piece;

    /// <summary>Each chain's identity: the host step it binds next, the bound defense node and its pieces, the planned transitions.</summary>
    private static string Chains(LayoutPlan plan)
    {
        var host = HostNext(plan);
        var node = DefenseBindingRules.Bound(plan);
        return string.Join(" | ", host.DefName + "@" + host.Anchor(),
            node is null ? "-" : node.Node + ":" + string.Join(",", node.Tier!.Places.Select(p => p.Definition + "@" + p.Cell)),
            string.Join(",", plan.Transitions.Select(t => t.Target + ":" + t.Kind + ":" + t.Cause)));
    }

    private static TransitionFacts Mechanics(LayoutPlan plan) =>
        TransitionFixtures.Facts(TransitionFixtures.Mechanics("spot", TransitionBindingTests.Spot, new Cell(1, 1), minifiable: false)) with { Planned = plan.Transitions };

    /// <summary>
    /// The host chain, the defense line, the dependent retirement and the relocation are plan state: review N+1 and a save and
    /// load keep each with the same ids, the bound retirement is read back without comparing again and Retain keeps the line.
    /// The care transport is journal work: within the world it continues, and the load retires it with every open plan, so it
    /// holds no bed, its stale receipt is rejected and the new world's review issues it afresh. A replacement that stops
    /// serving then re-selects only its retirement; every other chain stays as bound.
    /// </summary>
    [Fact]
    public void BoundChainsSurviveReviewAndLoadWithTheirIdsAndAChangedInputReselectsOnlyItsChain()
    {
        using var rig = Rig.Open();
        var plan = Habitat();
        Assert.True(plan.Valid());
        rig.Hold(plan);
        var bound = Chains(plan);
        Assert.Equal("TestSpot@" + Bridge.Wanted().Anchor(), bound.Split(" | ")[0]);
        Assert.StartsWith(DefenseBindingRules.NodePrefix + DefenseBindingTests.Cover.Key, bound.Split(" | ")[1], StringComparison.Ordinal);

        var support = CareTransportTests.Request(CareTransportTests.Walking("care", "p")).Support;
        var rescue = Rescue("rescue-0", "doc");
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(P), "rescue-p/ward", rescue));
        var carried = rig.PlanOf(MedicalRules.CriticalNeed(P), "rescue-p/ward");
        var attempt = rig.Dispatch(carried, rescue.Id);
        IReadOnlyList<IssuedCareTransport> Issued() =>
            [.. HospitalPlanner.Transports(rig.Journal, rig.Journal.LoadNeed(MedicalRules.MedicalCareNeed, rig.World, Ct), support, rig.World, Ct).Select(t => t.Issued)];
        Assert.Equal([new IssuedCareTransport("p", "ward", true)], Issued());
        Assert.Equal([new CareBedHold("p", "ward")], CareTransportRules.Holds(Issued(), support));

        // Review N+1 in the same world: every chain as bound, the transport continued, not re-selected.
        rig.Stage(new Tick(110));
        Assert.Equal(bound, Chains(rig.Held()));
        Expect(ClaimOutcome.Continued, rig.Commit(MedicalRules.CriticalNeed(P), "rescue-p/ward", rescue));
        Assert.Equal("ward", rig.Commit(MedicalRules.CriticalNeed(new("q")), "hospital-transport-q/ward", BedRest("rest-q")).Conflict?.Claim);

        // Save, load, run again.
        rig.Load(rig.Save(), "load-2", new Tick(200));
        var restored = rig.Held();
        Assert.Equal(bound, Chains(restored));
        Assert.Equal(plan.Transitions, restored.Transitions);
        var (retirements, decisions) = PlannedRules.HostTransitionsOf(restored, TransitionBindingTests.View(TransitionBindingTests.SpotBench, TransitionBindingTests.SmithyBench),
            [TransitionBindingTests.Knife], _ => false, new TransitionFacts { Planned = restored.Transitions });
        Assert.Equal([new HostRetirement.Retire("spot", TransitionBindingTests.Spot, false)], retirements);
        var retire = Assert.Single(decisions);
        Assert.IsType<ServicingStanding.Bound>(retire.Chosen!.Standing);
        Assert.Empty(retire.Alternatives);
        var retain = DefenseBindingTests.Retained(restored);
        var defense = DefenseChoiceRules.Choose(
            [new(retain, DefenseBindingTests.Guard("retain", 0.8), DefenseBindingTests.Guard("retain" + DefenseChoiceRules.StandingSuffix, 0)),
             new(DefenseBindingTests.Cover, DefenseBindingTests.Guard(DefenseBindingTests.Cover.Key, 0.8), DefenseBindingTests.Guard(DefenseBindingTests.Cover.Key + DefenseChoiceRules.StandingSuffix, 0))],
            DefenseBindingTests.Inputs());
        Assert.Equal(DefenseCandidateKind.Retain, defense.Kind);
        Assert.Same(restored, DefenseBindingRules.Bind(restored, defense.Chosen.Alternative.Candidate, 0.25));

        Assert.Empty(Issued());
        Assert.True(rig.Journal.LoadPlan(carried, Ct).Retired);
        Assert.IsType<DomainException>(rig.Receive(carried, rescue.Id, attempt, Receipt.Accepted).Error);
        // The new world issues its own transport (a new plan, as MedicalFamily names each action after its plan).
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(P), "rescue-p/ward", Rescue("rescue-1", "doc")));

        // The smithy stops serving: its retirement re-selects (the spare is kept and the binding drops); nothing else moves.
        var (none, reopened) = PlannedRules.HostTransitionsOf(restored,
            TransitionBindingTests.View(TransitionBindingTests.SpotBench, TransitionBindingTests.SmithyBench with { Serves = false }),
            [TransitionBindingTests.Knife], _ => false, Mechanics(restored));
        Assert.Empty(none);
        Assert.False(Assert.Single(reopened).Admitted);
        var reselected = restored.WithTransitions(reopened);
        Assert.Equal([TransitionBindingTests.Move], reselected.Transitions);
        Assert.Equal(bound.Split(" | ")[..2], Chains(reselected).Split(" | ")[..2]);
        Assert.Equal([("host-retire-spot", "spot")], TransitionBindingRules.Stale(reselected, [("host-retire-spot", "spot"), ("room-move-bed", "bed")], o => o.Item2));
    }

    /// <summary>
    /// A rewind to a save from before the chains were bound: the plan carries no transition and no bound defense, the host
    /// binds its table directly (no bridge was planned), every open removal is stale, the journal holds no open order or claim
    /// of the rewound world and a receipt for its dispatched transport is rejected.
    /// </summary>
    [Fact]
    public void ARewindToBeforeBindingLeavesNoOrdersClaimsOrReceipts()
    {
        using var rig = Rig.Open();
        rig.Hold(PlannedRoomSelectionTests.HostPlan);
        var before = rig.Save();
        rig.Stage(new Tick(150));
        rig.Hold(Habitat());
        var rescue = Rescue("rescue-0", "doc");
        Expect(ClaimOutcome.Admitted, rig.Commit(MedicalRules.CriticalNeed(P), "rescue-p/ward", rescue));
        var carried = rig.PlanOf(MedicalRules.CriticalNeed(P), "rescue-p/ward");
        var attempt = rig.Dispatch(carried, rescue.Id);
        var pad = Rig.Build("gen-0", Rig.Generator, 31, 30);
        Expect(ClaimOutcome.Admitted, rig.Commit(PowerFamily.Basic, "generator-pad", pad));
        rig.Stage(new Tick(160));
        Assert.NotEmpty(rig.Claims.Pawns);

        rig.Load(before, "load-rewind", new Tick(100));
        var plan = rig.Held();
        Assert.Empty(plan.Transitions);
        Assert.Null(DefenseBindingRules.Bound(plan));
        Assert.DoesNotContain(plan.Reservations, r => r.Kind == ReservationKind.DefChoice);
        Assert.Equal(PlannedRoomSelectionTests.HostBench, HostNext(plan).DefName);
        (string, string)[] open = [("host-retire-spot", "spot"), ("room-move-bed", "bed")];
        Assert.Equal(open, TransitionBindingRules.Stale(plan, open, o => o.Item2));

        Assert.Empty(rig.Claims.Pawns);
        Assert.Empty(rig.Claims.Targets);
        Assert.Empty(rig.Journal.LoadNeed(MedicalRules.CriticalNeed(P), rig.World, Ct).Methods);
        Assert.Empty(rig.Journal.LoadNeed(PowerFamily.Basic, rig.World, Ct).Methods);
        Assert.IsType<DomainException>(rig.Receive(carried, rescue.Id, attempt, Receipt.Accepted).Error);
        Assert.NotEqual(Stage.Completed, Assert.Single(rig.Journal.LoadPlan(carried, Ct).Progress).View.Stage);
        // The rewound world admits the same ground again: nothing of the rewound claims survives.
        Expect(ClaimOutcome.Admitted, rig.Commit(LayoutFamily.Defense, "defense-tier", Rig.Build("bags-0", Rig.Sandbags, 31, 30)));
    }
}
