using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Contracts.Wire;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
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
    private static RecoveryBuilding Building(string id) => new()
    {
        Id = id,
        HitPoints = new BuildingHitPoints.Tracked { Current = 100, Maximum = 100 },
        Broken = false,
        Forbidden = false,
        Burning = false,
        Fuel = new RecoveryFuel.NotRefuelable(),
    };

    private static DisasterCondition Flare(ConditionDuration duration) =>
        new() { Id = "condition-1", Definition = "SolarFlare", Duration = duration, DisablesPower = true };

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
            Building("broken") with { Broken = true },
            Building("damaged") with { HitPoints = new BuildingHitPoints.Tracked { Current = 40, Maximum = 100 } },
            Building("untracked") with { HitPoints = new BuildingHitPoints.Untracked() },
            Building("low") with { Fuel = new RecoveryFuel.Refuelable(10, 100) },
            Building("fueled") with { Fuel = new RecoveryFuel.Refuelable(80, 100) },
            Building("forbidden") with { Broken = true, Forbidden = true },
            Building("burning") with { Broken = true, Burning = true },
        ]);

        Assert.Equal(
            [new RecoveryWork("low", RecoveryMethod.Refuel), new RecoveryWork("broken", RecoveryMethod.Breakdown), new RecoveryWork("damaged", RecoveryMethod.Repair)],
            work);
        Assert.Throws<DomainException>(() => DisasterRules.RecoveryPending([Building("over") with { HitPoints = new BuildingHitPoints.Tracked { Current = 120, Maximum = 100 } }]));
        Assert.Throws<DomainException>(() => DisasterRules.RecoveryPending([Building("nan") with { Fuel = new RecoveryFuel.Refuelable(double.NaN, 100) }]));
    }

    [Fact]
    public void UnreadConditionsLeaveAnOpenDisasterInTheUnreadPhase()
    {
        var conditions = Known<EquatableArray<DisasterCondition>>([Flare(new ConditionDuration.Timed { Ticks = 2500 })]);
        var buildings = Known<EquatableArray<RecoveryBuilding>>([Building("battery")]);
        var services = new Dictionary<DisasterService, Fact<bool>>();

        var open = DisasterRules.ReviewDisaster(conditions, buildings, services, null, new(100), Known(new Tick(90)));
        Assert.NotNull(open);
        Assert.Equal(new DisasterShortCircuit.Seen(new(90)), open.ShortCircuit);

        var unread = DisasterRules.ReviewDisaster(default, buildings, services, open, new(110));
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
                HitPoints = new BuildingHitPoints.Tracked { Current = 50, Maximum = 100 }, Fuel = new RecoveryFuel.Refuelable(5, 30),
            },
            new RecoveryCandidate { ID = new("recovery-b"), Building = "wall", Method = RecoveryMethod.Breakdown, HitPoints = new BuildingHitPoints.Untracked(), Fuel = new RecoveryFuel.NotRefuelable() },
        })
        {
            Assert.Equal(candidate, StoreJson.Deserialize<RecoveryCandidate>(StoreJson.Serialize(candidate)));
        }
    }
}
