using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ClockReceiptWireTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new()
    {
        Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 },
        Tick = 1200, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(2),
    };

    private static void RoundTrips<T>(T value)
    {
        var json = OrdersJson.Serialize(value);
        Assert.Equal(value, OrdersJson.Deserialize<T>(json));
        Assert.Equal(json, OrdersJson.Serialize(OrdersJson.Deserialize<T>(json)));
    }

    private static RimGovernor.Brain.GameFacts.Clock.Status Status(RimGovernor.Brain.GameFacts.Clock.HoldState hold, RimGovernor.Brain.GameFacts.Clock.RunHistory run, RimGovernor.Brain.GameFacts.Clock.Pacing pacing, RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest request) => new()
    {
        Context = Context, State = hold is RimGovernor.Brain.GameFacts.Clock.HoldState.RunningCase ? RimGovernor.Brain.GameFacts.Clock.ClockState.Running : RimGovernor.Brain.GameFacts.Clock.ClockState.Held,
        Hold = hold, LastRun = run, Pacing = pacing, PlayerRequest = request, HoldSerial = 3,
        NativeTickBoundary = true, ActualPaused = hold is not RimGovernor.Brain.GameFacts.Clock.HoldState.RunningCase, EffectiveTicksPerSecond = 12.5,
    };

    [Fact]
    public void ClockStatesRoundTripWithoutInventingAReasonSpeedOrTimestamp()
    {
        RoundTrips(Status(new RimGovernor.Brain.GameFacts.Clock.HoldState.NeverRunCase(), new RimGovernor.Brain.GameFacts.Clock.RunHistory.NeverRunCase(), new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase(), new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase()));
        RoundTrips(Status(new RimGovernor.Brain.GameFacts.Clock.HoldState.RunningCase(), new RimGovernor.Brain.GameFacts.Clock.RunHistory.RanCase(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast), new RimGovernor.Brain.GameFacts.Clock.Pacing.RunningCase(1800, RimGovernor.Brain.GameFacts.Clock.PacingReason.Fixed, 0), new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.RequestedCase(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, 7)));
        RoundTrips(Status(new RimGovernor.Brain.GameFacts.Clock.HoldState.PendingCase(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review), new RimGovernor.Brain.GameFacts.Clock.RunHistory.RanCase(RimGovernor.Brain.GameFacts.Clock.Speed.Fast), new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase(), new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase()));
        RoundTrips(Status(new RimGovernor.Brain.GameFacts.Clock.HoldState.HeldCase(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, 1234, new RimGovernor.Brain.GameFacts.Clock.HoldTrigger.OrdinaryCase()), new RimGovernor.Brain.GameFacts.Clock.RunHistory.RanCase(RimGovernor.Brain.GameFacts.Clock.Speed.Fast), new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase(), new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase()));
        RoundTrips(Status(new RimGovernor.Brain.GameFacts.Clock.HoldState.HeldCase(RimGovernor.Brain.GameFacts.Clock.HoldReason.CombatEvent, 1235, new RimGovernor.Brain.GameFacts.Clock.HoldTrigger.CombatCase(RimGovernor.Brain.GameFacts.Clock.CombatEvent.Downed)), new RimGovernor.Brain.GameFacts.Clock.RunHistory.RanCase(RimGovernor.Brain.GameFacts.Clock.Speed.Normal), new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase(), new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase()));
    }

    [Fact]
    public void EveryAnimalEvidenceBranchRoundTrips()
    {
        RimGovernor.Brain.GameFacts.Receipts.AnimalEffect[] effects =
        [
            new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.TrainingCase("Obedience", true), new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.SlaughterCase(false),
            new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.TameCase(true), new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.ReleaseCase(false),
            new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.SterilizationQueuedCase(), new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.SterilizedCase(),
            new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.AreaCase(""), new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.MasterCase("Human1"),
            new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.FollowDraftedCase(true), new RimGovernor.Brain.GameFacts.Receipts.AnimalEffect.FollowFieldworkCase(false),
        ];
        foreach (var effect in effects) RoundTrips(effect);
    }

    [Fact]
    public void TradeEvidenceDistinguishesApproachOpenStagingExecutionAndEnd()
    {
        RimGovernor.Brain.GameFacts.Receipts.TradeEffect[] effects =
        [
            new RimGovernor.Brain.GameFacts.Receipts.TradeEffect.ApproachingCase(), new RimGovernor.Brain.GameFacts.Receipts.TradeEffect.OpenCase("session", "Faction1"),
            new RimGovernor.Brain.GameFacts.Receipts.TradeEffect.StagedCase([new RimGovernor.Brain.GameFacts.Receipts.TradeLineEffect { LineId = "#0" }]),
            new RimGovernor.Brain.GameFacts.Receipts.TradeEffect.ExecutedCase("deal", 1000, 800, true), new RimGovernor.Brain.GameFacts.Receipts.TradeEffect.EndedCase(),
        ];
        foreach (var effect in effects) RoundTrips(effect);
    }

    [Fact]
    public void ZoneEvidenceDistinguishesTheCreatedComponentsFromAnEditedOrDeletedZone()
    {
        RoundTrips<RimGovernor.Brain.GameFacts.Receipts.ZoneEffect>(new RimGovernor.Brain.GameFacts.Receipts.ZoneEffect.CreatedCase([new RimGovernor.Brain.GameFacts.Receipts.CreatedZone { ZoneId = "Zone1", Cells = [new RimGovernor.Brain.Domain.Cell { X = 1, Z = 2 }] }]));
        RoundTrips<RimGovernor.Brain.GameFacts.Receipts.ZoneEffect>(new RimGovernor.Brain.GameFacts.Receipts.ZoneEffect.ZoneCase("Zone1", true, 3));
        RoundTrips<RimGovernor.Brain.GameFacts.Receipts.ZoneEffect>(new RimGovernor.Brain.GameFacts.Receipts.ZoneEffect.ZoneCase("Zone1", false, 0));
    }

    [Fact]
    public void AWholeBodySurgeryHasNoInventedPartIndex()
    {
        RoundTrips<RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget>(new RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget.WholeBodyCase());
        RoundTrips<RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget>(new RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget.PartCase(0));
        RoundTrips(new RimGovernor.Brain.GameFacts.Receipts.SurgeryEffect { PawnId = "Human1", Bill = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "Bill1" }, State = RimGovernor.Brain.GameFacts.Receipts.SurgeryState.Queued, Target = new RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget.WholeBodyCase() });
    }

    [Fact]
    public void MissingStatusOrUnionPayloadMembersAreRefused()
    {
        var status = JsonNode.Parse(OrdersJson.Serialize(Status(new RimGovernor.Brain.GameFacts.Clock.HoldState.NeverRunCase(), new RimGovernor.Brain.GameFacts.Clock.RunHistory.NeverRunCase(), new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase(), new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase())))!.AsObject();
        status.Remove("actualPaused");
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Clock.Status>(status.ToJsonString()));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest>("{\"type\":\"requested\",\"speed\":\"SPEED_FAST\"}"));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Receipts.AnimalEffect>("{\"type\":\"slaughter\"}"));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget>("{\"type\":\"part\"}"));
    }

    [Fact]
    public void AnInitialZeroHoldSerialIsAValueButAnOmittedSerialIsNot()
    {
        var request = new RimGovernor.Brain.GameOrders.Clock.RunRequest
        {
            Identity = Context.Identity, ExpectedGeneration = 2, Speed = RimGovernor.Brain.GameFacts.Clock.Speed.Normal,
            PlannedHoldSerial = 0, HoldAtTick = 2500,
            Policy = new RimGovernor.Brain.GameOrders.Clock.WatchPolicy
            {
                Mode = RimGovernor.Brain.GameOrders.Clock.WatchMode.Colony, HealthDropFraction = .1f, MinHealthFraction = .2f,
                HostileWithin = 40, InjuryStopCooldownMs = 0,
                SeriousSingleHitDamage = 10, SeriousSummaryHealthFloor = .3f,
                SeriousBleedRateFloor = .1f, SeriousVitalPartFloor = .2f,
                ExplosiveNearMarginCells = 2, MeleeReachCells = 1,
                InjurySeverityFloorTicks = 1000, HungerHoldCategory = RimGovernor.Brain.GameFacts.Defs.HungerCategory.Hungry,
            },
        };
        RoundTrips(request);
        foreach (var field in new[] { "plannedHoldSerial", "holdAtTick", "expectedGeneration", "speed", "policy" })
        {
            var json = JsonNode.Parse(OrdersJson.Serialize(request))!.AsObject();
            json.Remove(field);
            Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Clock.RunRequest>(json.ToJsonString()));
        }
    }

    [Fact]
    public void CombatReceiptRetainsNativeResultsAndOriginalIndices()
    {
        var receipt = new RimGovernor.Brain.GameFacts.Receipts.Receipt
        {
            Outcome = new RimGovernor.Brain.GameFacts.Receipts.Applied
            {
                Observed = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence
                {
                    Effect = new RimGovernor.Brain.GameFacts.Receipts.CombatOrdersEffect
                    {
                        Results =
                        [
                            new CombatResult { Index = 0, PawnId = "Human1", Applied = true, JobDef = "Goto" },
                            new CombatResult { Index = 1, PawnId = "Human2", Applied = false, Refusal = "not_drafted" },
                        ],
                    },
                },
            },
        };
        RoundTrips(receipt);
        var decoded = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Receipts.Receipt>(OrdersJson.Serialize(receipt));
        var results = RimGovernor.Brain.GameAccess.CombatCalls.CombatOrderResults(decoded);
        var native = decoded.Outcome.Applied!.Observed!.Effect!.CombatOrders!.Results;
        Assert.Same(native[0], results[0]);
        Assert.Same(native[1], results[1]);
        Assert.Equal("Goto", results[0].JobDef);
        Assert.Equal([0, 1], results.Select(r => r.Index));
        Assert.True(results[0].Applied);
        Assert.False(results[1].Applied);
        Assert.Equal("not_drafted", results[1].Refusal);

        var filtered = new RimGovernor.Brain.GameFacts.Receipts.Receipt
        {
            Outcome = new RimGovernor.Brain.GameFacts.Receipts.Applied
            {
                Observed = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence
                {
                    Effect = new RimGovernor.Brain.GameFacts.Receipts.CombatOrdersEffect { Results = [native[1]] },
                },
            },
        };
        var remaining = Assert.Single(RimGovernor.Brain.GameAccess.CombatCalls.CombatOrderResults(filtered));
        Assert.Same(native[1], remaining);
        Assert.Equal(1, remaining.Index);
    }
}
