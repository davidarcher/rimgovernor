using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Room goals (#3496 part C): a RoomRole goal adds a child room to the planned-room needs or a bed floor to the hospital
/// and station channels, and is judged by the standing rooms and the owning Need's progress.
/// </summary>
public sealed class RoomRoleGoalTests
{
    private static GoalRecord Room(int id, RoomRole role, int count = 0) => new() { Id = id, Kind = GoalKind.RoomRole, Target = Wire.Name(role), Count = count };

    private static RoomState Standing(RoomRole role, params string[] beds) => RoomFixtures.State("r" + role, role, beds: beds);

    private static GoalsFacts Facts(GoalRecord goal, params RoomState[] rooms) => new()
    {
        Goals = [goal],
        Rooms = RoomFixtures.Census(rooms),
        Rounds = new RoundsFacts { Sleeping = new SleepingObservation { Beds = [] } },
    };

    private static SleepingBed Bed(string id, bool medical) => new() { Id = id, Definition = new Resource("Bed"), Humanlike = true, Medical = medical };

    private static OwnerProgress Working => new(true, "");

    private static readonly string[] SickIds = ["p1", "p2", "p3"];

    [Theory]
    [InlineData(RoomRole.Nursery)]
    [InlineData(RoomRole.Classroom)]
    [InlineData(RoomRole.DeathrestChamber)]
    public void AChildRoomGoalYieldsItsRoomNeedForTheSleepingStepToBuild(RoomRole role)
    {
        var goal = Room(1, role);
        var demand = GoalRules.DemandOf(Facts(goal));
        Assert.Equal([role], demand.Build.Rooms.Items);

        var needs = ChildRoomsRules.GoalRoomNeeds(demand.Build.Rooms, []);
        var need = Assert.Single(needs);
        Assert.Equal(role, need.Role);
        Assert.NotEmpty(need.Furniture.Items);

        // The need rides every read of the world.
        Assert.Equal(demand.Build, GoalRules.Stamp(new RoundsFacts(), demand).GoalBuild);
    }

    [Fact]
    public void ARoomThePawnsAlreadyOweKeepsTheirLargerNeed()
    {
        var owed = new ChildRoomNeed { Role = RoomRole.Classroom, Module = PlannedRole.Classroom };
        Assert.Empty(ChildRoomsRules.GoalRoomNeeds([RoomRole.Classroom], [owed]));
        Assert.Single(ChildRoomsRules.GoalRoomNeeds([RoomRole.Classroom, RoomRole.Classroom, RoomRole.Nursery], [owed]));
    }

    [Fact]
    public void AChildRoomGoalIsPursuedBySleepingAndMetOnceTheRoomStands()
    {
        var goal = Room(1, RoomRole.Classroom);
        var open = Facts(goal) with { Owners = new Dictionary<NeedId, OwnerProgress> { [ShelterFamily.Sleeping] = Working } };
        var status = GoalRules.Evaluate(goal, open);
        Assert.Equal((GoalState.Pursuing, ShelterFamily.Sleeping, 1.0), (status.State, status.Owner, ((GoalDeficit.Measured)status.Deficit).Amount));

        var blocked = open with { Owners = new Dictionary<NeedId, OwnerProgress> { [ShelterFamily.Sleeping] = new(false, "no_site") } };
        Assert.Equal((GoalState.Blocked, "no_site"), (GoalRules.Evaluate(goal, blocked).State, GoalRules.Evaluate(goal, blocked).Reason));

        var built = Facts(goal, Standing(RoomRole.Classroom));
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, built).State);
        // A met room goal keeps asking, or the plan would end the role and retire the room.
        Assert.Equal([RoomRole.Classroom], GoalRules.DemandOf(built).Build.Rooms.Items);

        Assert.Equal("rooms", Assert.Throws<RequiredFactException>(() => GoalRules.Evaluate(goal, open with { Rooms = null! })).Fact);
    }

    [Fact]
    public void AWorshipRoomGoalNeedsTheIdeoligionToRequireABuilding()
    {
        var goal = Room(1, RoomRole.WorshipRoom);
        var none = Facts(goal) with { Ideology = new IdeologyMode.NotInstalled() };
        Assert.Equal((GoalState.NoMethod, "missing_dlc"), (GoalRules.Evaluate(goal, none).State, GoalRules.Evaluate(goal, none).Reason));

        var bare = Facts(goal) with { Ideology = new IdeologyMode.Installed(new Ideoligion(new IdeologyDefs(), new IdeoligionFacts { Development = IdeoDevelopment.Fixed })) };
        Assert.Equal((GoalState.NoMethod, "no_required_buildings"), (GoalRules.Evaluate(goal, bare).State, GoalRules.Evaluate(goal, bare).Reason));

        // The worship room itself is the ideoligion's need, so the goal asks for no child room of its own.
        Assert.Empty(GoalRules.DemandOf(bare).Build.Rooms.Items);
    }

    [Fact]
    public void ARoleNoPlannerStagesHasNoMethodUntilItStands()
    {
        // No playroom is planned (#3809): its goal is a role no planner stages, as the laboratory's is.
        var playroom = Room(2, RoomRole.Playroom);
        Assert.Equal((GoalState.NoMethod, "no_pursuit"), (GoalRules.Evaluate(playroom, Facts(playroom)).State, GoalRules.Evaluate(playroom, Facts(playroom)).Reason));
        Assert.Empty(ChildRoomsRules.GoalRoomNeeds([RoomRole.Playroom], []));
        var goal = Room(1, RoomRole.Laboratory);
        var absent = GoalRules.Evaluate(goal, Facts(goal));
        Assert.Equal((GoalState.NoMethod, "no_pursuit"), (absent.State, absent.Reason));
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, Facts(goal, Standing(RoomRole.Laboratory))).State);
        Assert.Equal((0, 0), (GoalRules.DemandOf(Facts(goal)).Build.Rooms.Count, GoalRules.DemandOf(Facts(goal)).Build.HospitalBeds));

        var unknown = new GoalRecord { Id = 2, Kind = GoalKind.RoomRole, Target = "Nonsense" };
        Assert.Equal("unknown_role", GoalRules.Evaluate(unknown, Facts(unknown)).Reason);
    }

    [Fact]
    public void AHospitalGoalYieldsAMinimumBedCountAndIsPursuedByTheCareNeed()
    {
        var goal = Room(1, RoomRole.Hospital, 2);
        Assert.Equal(2, GoalRules.DemandOf(Facts(goal)).Build.HospitalBeds);
        Assert.Equal(1, GoalRules.DemandOf(Facts(Room(1, RoomRole.Hospital))).Build.HospitalBeds);

        var facts = Facts(goal, Standing(RoomRole.Hospital, "b1")) with { Owners = new Dictionary<NeedId, OwnerProgress> { [MedicalRules.MedicalCareNeed] = Working } };
        facts = facts with { Rounds = facts.Rounds with { Sleeping = new SleepingObservation { Beds = [Bed("b1", true)] } } };
        var status = GoalRules.Evaluate(goal, facts);
        Assert.Equal((GoalState.Pursuing, MedicalRules.MedicalCareNeed, 1.0), (status.State, status.Owner, ((GoalDeficit.Measured)status.Deficit).Amount));

        var two = facts with { Rounds = facts.Rounds with { Sleeping = new SleepingObservation { Beds = [Bed("b1", true), Bed("b2", true)] } }, Rooms = RoomFixtures.Census(Standing(RoomRole.Hospital, "b1", "b2")) };
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, two).State);
    }

    private static HospitalRequest Ward(int floor, params SleepingBed[] beds) => new()
    {
        Doctors = 1,
        Patients = [],
        Sleeping = new SleepingObservation { Beds = [.. beds] },
        Rooms = RoomFixtures.Census(Standing(RoomRole.Hospital, [.. beds.Select(b => b.Id)])),
        Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }] },
        Definitions = [new BenchDefinition { Name = "Bed", Available = true}],
        MinBeds = floor,
        // Native's sleeping capacities leave the medical beds out.
        BedCapacity = beds.Count(b => !b.Medical),
        IndoorCapacity = beds.Count(b => !b.Medical),
    };

    [Fact]
    public void TheGoalBedFloorIsDemandWithNoPatientAndNeverLowersThePatientsOwn()
    {
        Assert.Equal(HospitalMethod.NoDemand, HospitalRules.SelectHospitalBed(Ward(0)).Method);

        var short1 = HospitalRules.SelectHospitalBed(Ward(2, Bed("b1", true)));
        Assert.Equal((HospitalMethod.Build, 2, "Bed"), (short1.Method, short1.Needed, short1.Definition));

        var spare = HospitalRules.SelectHospitalBed(Ward(2, Bed("b1", true), Bed("b2", false)));
        Assert.Equal((HospitalMethod.Convert, "b2"), (spare.Method, spare.Bed));

        Assert.Equal(HospitalMethod.Existing, HospitalRules.SelectHospitalBed(Ward(2, Bed("b1", true), Bed("b2", true))).Method);

        var sick = Ward(1, Bed("b1", true)) with
        {
            Patients = [.. SickIds.Select(id => CareFixtures.Patient(id) with { NeedsRest = true })],
        };
        Assert.Equal(3, HospitalRules.SelectHospitalBed(sick).Needed);
    }

    [Fact]
    public void AShortWardKeepsTheCareNeedUnmetWhileNobodyIsSick()
    {
        var rooms = RoomFixtures.Census(Standing(RoomRole.Hospital, "b1"));
        var sleeping = new SleepingObservation { Beds = [Bed("b1", true)] };
        Assert.True(HospitalRules.BedsShort(2, rooms, sleeping, false));
        Assert.False(HospitalRules.BedsShort(1, rooms, sleeping, false));
        Assert.False(HospitalRules.BedsShort(0, RoomTestFacts.NoRooms, new SleepingObservation(), false));

        var recovered = new MedicalFacts { CareRecovered = true };
        Assert.Equal(Satisfaction.Met, MedicalRules.MedicalCareNeedOf(recovered).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, MedicalRules.MedicalCareNeedOf(recovered with { HospitalShort = true }).Satisfaction);
        Assert.Equal(Satisfaction.Met, MedicalRules.MedicalCareNeedOf(recovered with { HospitalShort = false }).Satisfaction);
        // An unrecovered care census keeps the Need unmet even with enough beds.
        Assert.Equal(Satisfaction.Unmet, MedicalRules.MedicalCareNeedOf(new MedicalFacts { HospitalShort = false }).Satisfaction);
    }

    [Fact]
    public void TheStationGraphSizesTheWardAndTheNurseryFromTheGoalFloors()
    {
        StationFacts Base(GoalBuildDemand build, StationCatalog catalog) => new()
        {
            Catalog = catalog,
            Colonists = 4L,
            Patients = [],
            Prisoners = 0,
            WorkPawns = new WorkPawnsRead([]),
            GoalBuild = build,
        };

        var crib = new StationCatalog { PlayBuildings = [], Stations = new Dictionary<string, Station> { ["Crib"] = new Station { Contributions = [new() { Role = RoomRole.Nursery }] } } };
        var asked = new GoalBuildDemand { Rooms = EquatableArray.From([RoomRole.Nursery]), HospitalBeds = 3 };

        var population = LayoutStationPlanRules.StationPopulationOf(Base(asked, crib));
        Assert.Equal((3, ChildRoomsRules.MinNurseryBeds, 4), (population.Injured, population.Babies, population.Colonists));

        // Without a crib to place (no Biotech) the nursery goal asks the graph for nothing.
        Assert.Equal(0, LayoutStationPlanRules.StationPopulationOf(Base(asked, new StationCatalog { PlayBuildings = [] })).Babies);
        Assert.Equal((0, 0), (LayoutStationPlanRules.StationPopulationOf(Base(GoalBuildDemand.None, crib)).Injured, LayoutStationPlanRules.StationPopulationOf(Base(GoalBuildDemand.None, crib)).Babies));
    }
}
