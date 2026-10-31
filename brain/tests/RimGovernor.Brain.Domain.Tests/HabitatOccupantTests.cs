using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;
using QuestStatus = RimGovernor.Brain.GameFacts.Receipts.QuestStatus;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-001 occupants (task 2.2): each occupant kind needs a compatible, reachable slot of its own. A colonist never
/// sleeps in a slave, prison, medical bed or crib; a slave only in a bed set for slaves (a vacant regular bed is marked for
/// it, else one is built); a baby is crib demand, never a sleeping target; an accepted hosting quest's lodgers not on the
/// map yet are known arrivals while an unaccepted offer brings nobody; the hospitality gate counts compatible reachable
/// slots.
/// </summary>
public sealed class HabitatOccupantTests
{
    private static readonly PawnId A = new("a");
    private static readonly PawnId S = new("s");
    private static readonly PawnId Baby = new("baby");

    private static SleepingPerson Person(PawnId id, string bed = "", bool baby = false)
    {
        var pawn = SleepingNativeFixtures.Pawn(id, ownedBed: bed, comfortableMin: 10, comfortableMax: 30);
        if (baby)
        {
            pawn = pawn with { Biotech = new PawnBiotechState.Active(new RimGovernor.Brain.GameFacts.Observations.PawnBiotech
            {
                DevelopmentalStage = RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Baby, Genes = [], Mechanitor = null, Mech = null, Deathrest = null,
            }) };
        }
        return new SleepingPerson { Native = SleepingNativeFixtures.Person(id), Pawn = pawn };
    }

    private static SleepingBed Bed(string id, EquatableArray<PawnId> owners = default, EquatableArray<PawnId> users = default, bool slaves = false,
        bool prisoners = false, bool medical = false, bool crib = false, EquatableArray<PawnId>? reach = null, int slots = 1) => new()
    {
        Native = SleepingNativeFixtures.Bed(id, temperature: 21, owners: owners, users: users, accessibleTo: reach ?? [A, S, Baby], room: "r1",
            slaves: slaves, prisoners: prisoners, medical: medical, slots: slots),
        Building = SleepingNativeFixtures.Building(id, roofed: true),
        Humanlike = true,
        Crib = crib,
        RestEffectiveness = 1,
    };

    private static SleepingRequest Request(SleepingObservation sleeping, SleepingReview review) => new()
    {
        Targets = review.Targets,
        Sleeping = sleeping,
        Rooms = RoomFixtures.Census((RoomFixtures.State("r1", temperatureC: 21), new[] { new Cell(1, 1), new Cell(2, 1) })),
        Definitions = [new BenchDefinition { Name = "Bed", Available = true }],
        Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }] },
    };

    [Fact]
    public void AColonistIsNeverOfferedASlavePrisonMedicalBedOrCribAsItsSlot()
    {
        // Every vacant bed reachable, none of them the colonist's kind: it stays a target with nothing to take, short a slot.
        SleepingObservation Colony(params SleepingBed[] beds) => new()
        {
            Colonists = 1, People = [Person(A)], Beds = [.. beds], BedBuildable = true,
        };
        var incompatible = Colony(Bed("slave", slaves: true), Bed("prison", prisoners: true), Bed("medical", medical: true), Bed("crib", crib: true));
        var target = Assert.Single(SleepingRules.ReviewSleeping(incompatible, new SleepingHistory(), new Tick(600)).Targets);
        Assert.Equal((A, SleepingNeedKind.Upgrade), (target.Pawn, target.Kind));
        Assert.Empty(target.Available);
        Assert.Equal((1, 0), SleepingRules.SlotShortfall(incompatible));

        // A regular bed it reaches is its slot; one it cannot reach is no capacity.
        var regular = incompatible with { Beds = [.. incompatible.Beds, Bed("regular")] };
        Assert.Equal(["regular"], Assert.Single(SleepingRules.ReviewSleeping(regular, new SleepingHistory(), new Tick(600)).Targets).Available);
        Assert.Equal((0, 0), SleepingRules.SlotShortfall(regular));
        Assert.Equal((1, 0), SleepingRules.SlotShortfall(incompatible with { Beds = [.. incompatible.Beds, Bed("far", reach: [])] }));
    }

    [Fact]
    public void ASlaveWithNoMarkableBedIsBuiltOneThenItIsSetForSlavesThenAssigned()
    {
        var tick = new Tick(600);
        SleepingObservation Colony(params SleepingBed[] more) => new()
        {
            Colonists = 1,
            People = [Person(A, "bedA")],
            Slaves = [Person(S)],
            Beds = [Bed("bedA", owners: [A], users: [A]), .. more],
            BedBuildable = true,
        };

        // The colonist's owned bed is never the slave's: a bed is built.
        var bare = Colony();
        var review = SleepingRules.ReviewSleeping(bare, new SleepingHistory(), tick);
        Assert.Equal((S, SleepingNeedKind.Upgrade), (Assert.Single(review.Targets).Pawn, Assert.Single(review.Targets).Kind));
        Assert.Equal(SleepingMethod.Build, SleepingRules.SelectSleepingMethod(Request(bare, review)).Method);
        Assert.Equal((0, 1), SleepingRules.SlotShortfall(bare));

        // The fresh regular bed stands vacant: it is set for slaves (a crib never is).
        var built = Colony(Bed("bedS"), Bed("crib", crib: true));
        review = SleepingRules.ReviewSleeping(built, review.History, tick);
        var mark = SleepingRules.SelectSleepingMethod(Request(built, review));
        Assert.Equal((SleepingMethod.MarkSlaves, S, "bedS"), (mark.Method, mark.Pawn, mark.Bed));

        // Set for slaves, it is the slave's slot and the slave is assigned to it.
        var marked = Colony(Bed("bedS", slaves: true));
        review = SleepingRules.ReviewSleeping(marked, review.History, tick);
        var assign = SleepingRules.SelectSleepingMethod(Request(marked, review));
        Assert.Equal((SleepingMethod.Assign, S, "bedS"), (assign.Method, assign.Pawn, assign.Bed));
        Assert.Equal((0, 0), SleepingRules.SlotShortfall(marked));
    }

    [Fact]
    public void ASecureSlaveTheColonistCountHoldsIsOneFootholdSleeperAndNeedsASlaveSlot()
    {
        // Native's colonist count (IsFreeColonist) holds the secure slave the slave census lists too: it sleeps once.
        var sleeping = new SleepingObservation
        {
            Colonists = 1, CensusSlaves = 1, People = [Person(A, "bedA")], Slaves = [Person(S, "bedS")],
            Beds = [Bed("bedA", owners: [A], users: [A]), Bed("bedS", owners: [S], users: [S], slaves: true)],
        };
        var f = new ReviewFacts
        {
            Census = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 2, BedCapacity = 2, IndoorSleepingCapacity = 2 },
            Sleeping = sleeping,
        };
        Assert.Equal(2L, Rules.FootholdSleepers(f));
        Assert.True(Rules.FootholdSleeping(f));

        // Two places stand, but the slave's is a regular bed: no slave slot, so the foothold is short of sleeping; a
        // stand-in spot is no slave's slot, so none is owed for it (the sleeping Need marks or builds one).
        var regular = f with { Sleeping = sleeping with { Beds = [Bed("bedA", owners: [A], users: [A]), Bed("bedS", owners: [S])] } };
        Assert.Equal((0, 1), SleepingRules.SlotShortfall(regular.Sleeping));
        Assert.False(Rules.FootholdSleeping(regular));
        Assert.Equal(0, ShelterRules.StandInsOwed(regular).Missing);
    }

    [Fact]
    public void ABabyIsCribDemandNeverARegularSleeperAndTheNurserySizesOneCribPerBaby()
    {
        var sleeping = new SleepingObservation
        {
            Colonists = 2,
            People = [Person(A, "bedA"), Person(Baby, baby: true)],
            Beds = [Bed("bedA", owners: [A], users: [A])],
            BedBuildable = true,
        };
        // The bedless baby is no sleeping target and short no regular slot.
        Assert.True(SleepingRules.ReviewSleeping(sleeping, new SleepingHistory(), new Tick(600)).Recovered());
        Assert.Equal((0, 0), SleepingRules.SlotShortfall(sleeping));
        // A hosted baby is crib demand too, never a hosted sleeper.
        var hosted = new SleepingObservation { Guests = [Person(new PawnId("g")), Person(new PawnId("gb"), baby: true)] };
        Assert.Equal((1, 1), (hosted.HostedSleepers, hosted.HostedBabies));

        // One baby: one crib, not the goal nursery's floor of two.
        var baby = PawnFixtures.Worker("baby") with { Biotech = new PawnBiotechStatus.Active(new RimGovernor.Brain.Policy.PawnBiotech { Observed = new RimGovernor.Brain.GameFacts.Observations.PawnBiotech
        {
            DevelopmentalStage = RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Baby, Genes = [], Mechanitor = null, Mech = null, Deathrest = null,
        } }) };
        var nursery = Assert.Single(ChildRoomsRules.ChildRoomNeeds([baby], null));
        Assert.Equal(1, nursery.Furniture.Single(f => f.Role == FurnitureRole.BabyBed).Count);
    }

    private static readonly QuestProfile Refugees = new() { Family = QuestFamily.HospitalityRefugee };

    private static QuestState Lodgers(QuestStatus state, params PawnId[] lodgers) => QuestStateTests.NoQuest with
    {
        Id = new("lodgers"),
        State = state,
        ScriptDef = "Hospitality_Refugee",
        Objectives = [QuestStateTests.Objective(QuestObjectiveKind.HostLodgers) with
        {
            Goal = new QuestObjectiveGoal.Pawns(lodgers.Length), PawnIds = [.. lodgers], Active = true,
        }],
    };

    private static ReviewFacts Hosting(QuestState quest, SleepingObservation sleeping) => QuestStateTests.WithQuests(
        new ReviewFacts { Census = ColonyFactsCoreTests.Snapshot(), Sleeping = sleeping }, [quest], QuestStateTests.Root("Hospitality_Refugee", Refugees));

    [Fact]
    public void AnAcceptedLodgerQuestAddsItsLodgersNotYetHereAsKnownArrivalsAndAnOfferAddsNobody()
    {
        var here = new PawnId("l1");
        var sleeping = new SleepingObservation { Colonists = 1, People = [Person(A)], Guests = [Person(here)] };
        var accepted = Hosting(Lodgers(QuestStatus.Ongoing, here, new PawnId("l2")), sleeping);
        var arrival = Assert.Single(QuestHospitality.KnownArrivals(accepted));
        Assert.Equal(new KnownArrival(1, false), arrival);

        // Offered, not accepted: nobody is coming yet.
        Assert.Empty(QuestHospitality.KnownArrivals(Hosting(Lodgers(QuestStatus.NotYetAccepted, here, new PawnId("l2")), sleeping)));
    }

    [Fact]
    public void TheHospitalityGateCountsCompatibleReachableSlots()
    {
        var offer = Lodgers(QuestStatus.NotYetAccepted, new PawnId("l1"), new PawnId("l2"));
        ReviewFacts With(params SleepingBed[] beds) => Hosting(offer, new SleepingObservation { Colonists = 1, People = [Person(A)], Beds = [.. beds] });
        // A double bed nobody holds is two slots.
        Assert.Equal("", QuestHospitality.HospitalityAdmission(offer, With(Bed("double", slots: 2))).Value);
        // A crib, a slave bed and a bed no colonist reaches are no lodger's slot.
        Assert.Equal("guest_bed_capacity", QuestHospitality.HospitalityAdmission(offer,
            With(Bed("one"), Bed("crib", crib: true), Bed("slave", slaves: true), Bed("far", reach: []))).Value);
        // A half-owned double bed keeps one slot.
        Assert.Equal("", QuestHospitality.HospitalityAdmission(offer, With(Bed("one"), Bed("double", owners: [A], slots: 2))).Value);
    }
}
