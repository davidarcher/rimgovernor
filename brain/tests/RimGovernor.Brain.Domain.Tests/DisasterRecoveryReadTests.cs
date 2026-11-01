using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;
using BuildingHitPoints = RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints;
using ConditionDuration = RimGovernor.Brain.GameFacts.Observations.ConditionDuration;
using EnvironmentCondition = RimGovernor.Brain.GameFacts.Observations.EnvironmentCondition;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Disaster and recovery facts read whole (#3888): a condition's duration is permanent or timed, a recovery building's
/// flags are plain values with its hit points tracked or not and its fuel refuelable or not, a short circuit is seen
/// or none, and a disaster whose conditions went unread is in the named Unread phase.
/// </summary>
public sealed class DisasterRecoveryReadTests
{
    private static RimGovernor.Brain.GameFacts.Observations.BuildingState Building(string id) =>
        PowerBuildingFixtures.Trader(id, "Generator", new(4, 7), 1000, 1000, true) with
        {
            HitPoints = new BuildingHitPoints.Tracked { Current = 100, Maximum = 100 },
        };

    private static RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable Fuel(double amount, double target) => new()
    {
        AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Fuel = amount, TargetFuel = target, AutoRefuelPercent = 0.3, OutOfFuel = amount == 0,
        Burn = BuildingServiceWire.IdleBurn with { PerDay = 2, UnitsPerItem = 3, UseDriven = true, InRain = true, WhenPowered = true },
    };

    private static DisasterCondition Flare(ConditionDuration duration) =>
        new() { Observed = new EnvironmentCondition { Id = "condition-1", DefName = "SolarFlare", Duration = duration }, DisablesPower = true };

    [Fact]
    public void AConditionsDurationIsPermanentOrTimedOnTheWire()
    {
        foreach (var (duration, json) in new (ConditionDuration, string)[]
        {
            (new ConditionDuration.Permanent(), """{"type":"permanent"}"""),
            (new ConditionDuration.Timed { Ticks = 2500 }, """{"type":"timed","ticks":2500}"""),
        })
        {
            var row = OrdersJson.Serialize(new EnvironmentCondition { Id = "1", DefName = "ToxicFallout", Duration = duration });

            Assert.Contains("\"duration\":" + json, row);
            Assert.Equal(row, OrdersJson.Serialize(OrdersJson.Deserialize<EnvironmentCondition>(row)));
        }
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<EnvironmentCondition>("""{"id":"1","defName":"ToxicFallout"}"""));
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
            Building("forbidden") with { Service = BuildingServiceWire.NoService with { BrokenDown = true }, Settings = new RimGovernor.Brain.GameFacts.Observations.BuildingSettings { Forbidden = true, Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.None() } },
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
    public void TheDisasterHistoryRoundTripsItsNamedStates()
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
    }
}
