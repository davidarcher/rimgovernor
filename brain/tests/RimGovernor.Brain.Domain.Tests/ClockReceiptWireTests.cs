using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using K = RimGovernor.Contracts.Wire.Clock;
using R = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ClockReceiptWireTests
{
    private static readonly C.ObservationContext Context = new()
    {
        Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 },
        Tick = 1200, NativeGeneration = new C.ObservedGeneration.AtCase(2),
    };

    private static void RoundTrips<T>(T value)
    {
        var json = WireJson.Serialize(value);
        Assert.Equal(value, WireJson.Deserialize<T>(json));
        Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<T>(json)));
    }

    private static K.Status Status(K.HoldState hold, K.RunHistory run, K.Pacing pacing, K.PlayerSpeedRequest request) => new()
    {
        Context = Context, State = hold is K.HoldState.RunningCase ? K.ClockState.Running : K.ClockState.Held,
        Hold = hold, LastRun = run, Pacing = pacing, PlayerRequest = request, HoldSerial = 3,
        NativeTickBoundary = true, ActualPaused = hold is not K.HoldState.RunningCase, EffectiveTicksPerSecond = 12.5,
    };

    [Fact]
    public void ClockStatesRoundTripWithoutInventingAReasonSpeedOrTimestamp()
    {
        RoundTrips(Status(new K.HoldState.NeverRunCase(), new K.RunHistory.NeverRunCase(), new K.Pacing.StoppedCase(), new K.PlayerSpeedRequest.NoneCase()));
        RoundTrips(Status(new K.HoldState.RunningCase(), new K.RunHistory.RanCase(K.Speed.Hyperfast), new K.Pacing.RunningCase(1800, K.PacingReason.Fixed, 0), new K.PlayerSpeedRequest.RequestedCase(K.Speed.Normal, 7)));
        RoundTrips(Status(new K.HoldState.PendingCase(K.HoldReason.Review), new K.RunHistory.RanCase(K.Speed.Fast), new K.Pacing.StoppedCase(), new K.PlayerSpeedRequest.NoneCase()));
        RoundTrips(Status(new K.HoldState.HeldCase(K.HoldReason.Review, 1234, new K.HoldTrigger.OrdinaryCase()), new K.RunHistory.RanCase(K.Speed.Fast), new K.Pacing.StoppedCase(), new K.PlayerSpeedRequest.NoneCase()));
        RoundTrips(Status(new K.HoldState.HeldCase(K.HoldReason.CombatEvent, 1235, new K.HoldTrigger.CombatCase(K.CombatEvent.Downed)), new K.RunHistory.RanCase(K.Speed.Normal), new K.Pacing.StoppedCase(), new K.PlayerSpeedRequest.NoneCase()));
    }

    [Fact]
    public void EveryAnimalEvidenceBranchRoundTrips()
    {
        R.AnimalEffect[] effects =
        [
            new R.AnimalEffect.TrainingCase("Obedience", true), new R.AnimalEffect.SlaughterCase(false),
            new R.AnimalEffect.TameCase(true), new R.AnimalEffect.ReleaseCase(false),
            new R.AnimalEffect.SterilizationQueuedCase(), new R.AnimalEffect.SterilizedCase(),
            new R.AnimalEffect.AreaCase(""), new R.AnimalEffect.MasterCase("Human1"),
            new R.AnimalEffect.FollowDraftedCase(true), new R.AnimalEffect.FollowFieldworkCase(false),
        ];
        foreach (var effect in effects) RoundTrips(effect);
    }

    [Fact]
    public void TradeEvidenceDistinguishesApproachOpenStagingExecutionAndEnd()
    {
        R.TradeEffect[] effects =
        [
            new R.TradeEffect.ApproachingCase(), new R.TradeEffect.OpenCase("session", "Faction1"),
            new R.TradeEffect.StagedCase([new R.TradeLineEffect { LineId = "#0" }]),
            new R.TradeEffect.ExecutedCase("deal", 1000, 800, true), new R.TradeEffect.EndedCase(),
        ];
        foreach (var effect in effects) RoundTrips(effect);
    }

    [Fact]
    public void ZoneEvidenceDistinguishesTheCreatedComponentsFromAnEditedOrDeletedZone()
    {
        RoundTrips<R.ZoneEffect>(new R.ZoneEffect.CreatedCase([new R.CreatedZone { ZoneId = "Zone1", Cells = [new RimGovernor.Brain.Domain.Cell { X = 1, Z = 2 }] }]));
        RoundTrips<R.ZoneEffect>(new R.ZoneEffect.ZoneCase("Zone1", true, 3));
        RoundTrips<R.ZoneEffect>(new R.ZoneEffect.ZoneCase("Zone1", false, 0));
    }

    [Fact]
    public void AWholeBodySurgeryHasNoInventedPartIndex()
    {
        RoundTrips<R.SurgeryTarget>(new R.SurgeryTarget.WholeBodyCase());
        RoundTrips<R.SurgeryTarget>(new R.SurgeryTarget.PartCase(0));
        RoundTrips(new R.SurgeryEffect { PawnId = "Human1", Bill = new C.Ref { Id = "Bill1" }, State = R.SurgeryState.Queued, Target = new R.SurgeryTarget.WholeBodyCase() });
    }

    [Fact]
    public void MissingStatusOrUnionPayloadMembersAreRefused()
    {
        var status = JsonNode.Parse(WireJson.Serialize(Status(new K.HoldState.NeverRunCase(), new K.RunHistory.NeverRunCase(), new K.Pacing.StoppedCase(), new K.PlayerSpeedRequest.NoneCase())))!.AsObject();
        status.Remove("actualPaused");
        Assert.Throws<JsonException>(() => WireJson.Deserialize<K.Status>(status.ToJsonString()));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<K.PlayerSpeedRequest>("{\"type\":\"requested\",\"speed\":\"SPEED_FAST\"}"));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<R.AnimalEffect>("{\"type\":\"slaughter\"}"));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<R.SurgeryTarget>("{\"type\":\"part\"}"));
    }

    [Fact]
    public void AnInitialZeroHoldSerialIsAValueButAnOmittedSerialIsNot()
    {
        var request = new K.RunRequest
        {
            Identity = Context.Identity, ExpectedGeneration = 2, Speed = K.Speed.Normal,
            PlannedHoldSerial = 0, HoldAtTick = 2500,
            Policy = new K.WatchPolicy
            {
                Mode = K.WatchMode.Colony, HealthDropFraction = .1f, MinHealthFraction = .2f,
                HostileWithin = 40, InjuryStopCooldownMs = 0,
                SeriousSingleHitDamage = 10, SeriousSummaryHealthFloor = .3f,
                SeriousBleedRateFloor = .1f, SeriousVitalPartFloor = .2f,
                ExplosiveNearMarginCells = 2, MeleeReachCells = 1,
                InjurySeverityFloorTicks = 1000, HungerHoldCategory = RimGovernor.Contracts.Wire.Defs.HungerCategory.Hungry,
            },
        };
        RoundTrips(request);
        foreach (var field in new[] { "plannedHoldSerial", "holdAtTick", "expectedGeneration", "speed", "policy" })
        {
            var json = JsonNode.Parse(WireJson.Serialize(request))!.AsObject();
            json.Remove(field);
            Assert.Throws<JsonException>(() => WireJson.Deserialize<K.RunRequest>(json.ToJsonString()));
        }
    }

    [Fact]
    public void CombatReceiptRetainsNativeResultsAndOriginalIndices()
    {
        var receipt = new R.Receipt
        {
            Outcome = new R.Applied
            {
                Observed = new R.EffectEvidence
                {
                    Effect = new R.CombatOrdersEffect
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
        var decoded = WireJson.Deserialize<R.Receipt>(WireJson.Serialize(receipt));
        var results = RimGovernor.Brain.Bridge.Contracts.CombatCalls.CombatOrderResults(decoded);
        var native = decoded.Outcome.Applied!.Observed!.Effect!.CombatOrders!.Results;
        Assert.Same(native[0], results[0]);
        Assert.Same(native[1], results[1]);
        Assert.Equal("Goto", results[0].JobDef);
        Assert.Equal([0, 1], results.Select(r => r.Index));
        Assert.True(results[0].Applied);
        Assert.False(results[1].Applied);
        Assert.Equal("not_drafted", results[1].Refusal);

        var filtered = new R.Receipt
        {
            Outcome = new R.Applied
            {
                Observed = new R.EffectEvidence
                {
                    Effect = new R.CombatOrdersEffect { Results = [native[1]] },
                },
            },
        };
        var remaining = Assert.Single(RimGovernor.Brain.Bridge.Contracts.CombatCalls.CombatOrderResults(filtered));
        Assert.Same(native[1], remaining);
        Assert.Equal(1, remaining.Index);
    }
}
