using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;
using BuildingHitPoints = RimGovernor.Contracts.Wire.Observations.BuildingHitPoints;
using ConditionDuration = RimGovernor.Contracts.Wire.Observations.ConditionDuration;
using EnvironmentCondition = RimGovernor.Contracts.Wire.Observations.EnvironmentCondition;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Disaster and recovery facts read whole (#3888): a condition's duration is permanent or timed, a recovery building's
/// flags are plain values with its hit points tracked or not and its fuel refuelable or not, a short circuit is seen
/// or none, and a disaster whose conditions went unread is in the named Unread phase.
/// </summary>
public sealed class DisasterRecoveryReadTests
{
    private static O.BuildingState Building(string id) =>
        PowerBuildingFixtures.Trader(id, "Generator", new(4, 7), 1000, 1000, true) with
        {
            HitPoints = new BuildingHitPoints.Tracked { Current = 100, Maximum = 100 },
        };

    private static O.BuildingFuel.Refuelable Fuel(double amount, double target) => new()
    {
        Fuel = amount, TargetFuel = target, OutOfFuel = amount == 0,
        Burn = BuildingServiceWire.IdleBurn with { PerDay = 2, UnitsPerItem = 3, UseDriven = true, InRain = true, WhenPowered = true, Flickable = true },
    };

    private static DisasterCondition Flare(ConditionDuration duration) =>
        new() { Observed = new EnvironmentCondition { Id = "condition-1", DefName = "SolarFlare", Duration = duration }, DisablesPower = true };

    [Fact]
    public void RecoveryRetainsWholeInputsAndReplaysTheSameSelectionAfterStorage()
    {
        var fuel = Fuel(0, 30);
        var building = Building("generator") with
        {
            Roofed = true, RainVulnerable = true, FuelDefinitions = ["WoodLog"], Stuff = "Steel",
            Service = Building("generator").Service with { Fuel = fuel },
            Door = new O.BuildingDoor { Open = true, HoldOpen = false },
        };
        var native = PawnFixtures.Row("a");
        native = native with
        {
            Needs = native.Needs with { Mood = new O.NeedLevel.Has(.35), BreakThresholdMinor = .2,
                Food = new O.FoodNeed.Has(.7, RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed),
                Rest = new O.NeedLevel.Has(.8), Joy = new O.NeedLevel.Has(.6) },
            Social = native.Social with { HighExpectations = true },
        };
        MoodPawn Worker(O.PawnState row) => new()
        {
            Pawn = row, Forecast = new O.PatientForecast { PawnId = row.Pawn.Id, MoodTarget = .65 },
            Thoughts = [new("TestThought", -5)],
        };
        O.PawnState Named(string id) => native with { Pawn = native.Pawn with { Id = id } };
        var pawn = Worker(native);
        var planning = new RecoveryPlanning
        {
            Buildings = [building],
            Workers = [pawn, Worker(Named("forced") with { Job = new O.JobEvidence { PlayerForced = true } }),
                Worker(Named("dead") with { Dead = true }), Worker(Named("downed") with { Downed = true }),
                Worker(Named("drafted") with { Drafted = true }),
                Worker(Named("mental") with { MentalState = new O.PawnMentalState { DefName = "Wander", IsAggro = false, Ticks = 120 } })],
            Safety = new RecoverySafety { Restrictions = [RecoveryObservationFixtures.Restriction("a", "Safe"), RecoveryObservationFixtures.Restriction("forced"),
                RecoveryObservationFixtures.Restriction("dead"), RecoveryObservationFixtures.Restriction("downed"), RecoveryObservationFixtures.Restriction("drafted"), RecoveryObservationFixtures.Restriction("mental")] },
        };
        var condition = Flare(new ConditionDuration.Timed { Ticks = 321 });
        var services = Enum.GetValues<DisasterService>().Where(s => s != DisasterService.Infrastructure)
            .ToDictionary(s => s, _ => DisasterServiceStatus.Restored);
        var history = DisasterRules.ReviewDisaster([condition], planning.Buildings, services, null, new(100), new DisasterShortCircuit.NoneSeen());
        var selection = RecoveryRules.SelectRecoveryMethods(planning, history, [], new(100));
        Assert.Equal([new PawnId("a"), new PawnId("forced")], selection.Candidates.Select(c => c.Pawn));
        Assert.All(selection.Candidates, candidate => Assert.Same(fuel, candidate.Fuel));

        var record = new RimGovernor.Brain.Store.RoundsRecovery
        {
            Need = DisasterFamily.Services, Selection = selection,
            World = new RimGovernor.Brain.Store.World(new("colony"), new("load"), new(0)),
            Buildings = planning.Buildings, Workers = planning.Workers,
            Safety = new() { Restrictions = EquatableArray.From(planning.Safety.Restrictions.Select(r => new RimGovernor.Brain.Store.RoundsRecoveryRestriction(new PawnId(r.Pawn.Id), r.AreaId ?? ""))) },
        };
        Assert.Same(building, Assert.Single(record.Buildings));
        Assert.Same(pawn, record.Workers[0]);

        var back = StoreJson.Deserialize<RimGovernor.Brain.Store.RoundsRecovery>(StoreJson.Serialize(record));
        Assert.Equal(record, back);
        Assert.Equal("{\"Pawn\":\"forced\",\"Area\":\"\"}", StoreJson.Serialize(back.Safety.Restrictions[1]));
        Assert.Equal(building, Assert.Single(back.Buildings));
        Assert.Equal(fuel.Burn, Assert.IsType<O.BuildingFuel.Refuelable>(back.Buildings[0].Service.Fuel).Burn);
        Assert.True(Assert.IsType<O.BuildingFuel.Refuelable>(back.Selection.Candidates[0].Fuel).OutOfFuel);
        var replay = new RecoveryPlanning
        {
            Buildings = back.Buildings, Workers = back.Workers,
            Safety = new() { Restrictions = EquatableArray.From(back.Safety.Restrictions.Select(r => RecoveryObservationFixtures.Restriction(r.Pawn.Value, r.Area.Length == 0 ? null : r.Area))) },
        };
        Assert.Null(replay.Safety.Restrictions[1].AreaId);
        Assert.Equal(selection, RecoveryRules.SelectRecoveryMethods(replay, history, [], new(100)));
        Assert.Same(condition.Observed, Assert.Single(history!.Conditions).Observed);
    }

    [Fact]
    public void AConditionsDurationIsPermanentOrTimedOnTheWire()
    {
        foreach (var (duration, json) in new (ConditionDuration, string)[]
        {
            (new ConditionDuration.Permanent(), """{"type":"permanent"}"""),
            (new ConditionDuration.Timed { Ticks = 2500 }, """{"type":"timed","ticks":2500}"""),
        })
        {
            var row = WireJson.Serialize(new EnvironmentCondition { Id = "1", DefName = "ToxicFallout", Duration = duration });

            Assert.Contains("\"duration\":" + json, row);
            Assert.Equal(row, WireJson.Serialize(WireJson.Deserialize<EnvironmentCondition>(row)));
        }
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<EnvironmentCondition>("""{"id":"1","defName":"ToxicFallout"}"""));
    }

    [Fact]
    public void PendingWorkReadsTheBuildingsHitPointsAndFuelStates()
    {
        var work = DisasterRules.RecoveryPending(
        [
            Building("broken") with { Service = BuildingServiceWire.NoService with { BrokenDown = true } },
            Building("damaged") with { HitPoints = new BuildingHitPoints.Tracked { Current = 40, Maximum = 100 } },
            Building("untracked") with { HitPoints = new BuildingHitPoints.Untracked() },
            Building("low") with { Service = BuildingServiceWire.NoService with { Fuel = Fuel(10, 100) } },
            Building("fueled") with { Service = BuildingServiceWire.NoService with { Fuel = Fuel(80, 100) } },
            Building("forbidden") with { Service = BuildingServiceWire.NoService with { BrokenDown = true }, Settings = new O.BuildingSettings { Forbidden = true, Control = new O.BuildingControl.None() } },
            Building("burning") with { Service = BuildingServiceWire.NoService with { BrokenDown = true }, Burning = true },
        ]);

        Assert.Equal(
            [new RecoveryWork("low", RecoveryMethod.Refuel), new RecoveryWork("broken", RecoveryMethod.Breakdown), new RecoveryWork("damaged", RecoveryMethod.Repair)],
            work);
        Assert.Throws<DomainException>(() => DisasterRules.RecoveryPending([Building("over") with { HitPoints = new BuildingHitPoints.Tracked { Current = 120, Maximum = 100 } }]));
        Assert.Throws<DomainException>(() => DisasterRules.RecoveryPending([Building("nan") with { Service = BuildingServiceWire.NoService with { Fuel = Fuel(double.NaN, 100) } }]));
    }

    [Fact]
    public void UnreadServiceFindingsLeaveAnOpenDisasterInTheUnreadPhase()
    {
        var conditions = EquatableArray.From([Flare(new ConditionDuration.Timed { Ticks = 2500 })]);
        var buildings = EquatableArray.From([Building("battery")]);
        var services = Enum.GetValues<DisasterService>().Where(s => s != DisasterService.Infrastructure).ToDictionary(s => s, _ => DisasterServiceStatus.Restored);
        services[DisasterService.Food] = DisasterServiceStatus.AwaitingFoodPlan;

        var open = DisasterRules.ReviewDisaster(conditions, buildings, services, null, new(100), new DisasterShortCircuit.Seen(new Tick(90)));
        Assert.NotNull(open);
        Assert.Equal(new DisasterShortCircuit.Seen(new(90)), open.ShortCircuit);

        var unread = DisasterRules.ReviewDisaster(default, buildings, services, open, new(110), new DisasterShortCircuit.NoneSeen());
        Assert.Equal(DisasterPhase.Unread, unread?.Phase);
        Assert.Equal(open.ShortCircuit, unread?.ShortCircuit);
    }

    [Fact]
    public void TheDisasterHistoryAndARecoveryCandidateRoundTripTheirNamedStates()
    {
        foreach (var history in new[]
        {
            new DisasterHistory { Started = new(1), Observed = new(2), Phase = DisasterPhase.Unread, Conditions = [Flare(new ConditionDuration.Permanent())] },
            new DisasterHistory
            {
                ShortCircuit = new DisasterShortCircuit.Seen(new(5)), Started = new(1), Observed = new(6), Phase = DisasterPhase.Disrupted,
                Conditions = [Flare(new ConditionDuration.Timed { Ticks = 300 })],
            },
        })
        {
            var back = StoreJson.Deserialize<DisasterHistory>(StoreJson.Serialize(history));
            Assert.Equal((history.ShortCircuit, history.Phase), (back.ShortCircuit, back.Phase));
            Assert.Equal(history.Conditions, back.Conditions);
        }

        foreach (var candidate in new[]
        {
            new RecoveryCandidate
            {
                ID = new("recovery-a"), Building = "generator", Method = RecoveryMethod.Refuel,
                HitPoints = new BuildingHitPoints.Tracked { Current = 50, Maximum = 100 }, Fuel = Fuel(5, 30),
            },
            new RecoveryCandidate { ID = new("recovery-b"), Building = "wall", Method = RecoveryMethod.Breakdown, HitPoints = new BuildingHitPoints.Untracked(), Fuel = new O.BuildingFuel.NotRefuelable() },
        })
        {
            Assert.Equal(candidate, StoreJson.Deserialize<RecoveryCandidate>(StoreJson.Serialize(candidate)));
        }
    }
}
