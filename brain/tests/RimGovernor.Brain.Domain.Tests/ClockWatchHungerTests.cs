using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Policy.ClockWatchRules;

namespace RimGovernor.Brain.Domain.Tests;

// #3435: native holds a colony run for an unacknowledged colonist at the hunger hold category, so the review that
// answers them runs under a held clock. Only a resume acknowledges; adjusting a running clock never does.
public sealed class ClockWatchHungerTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private static readonly RimGovernor.Brain.GameFacts.Common.Identity Identity = new() { ColonyId = "colony", MapId = 1, LoadToken = "load" };

    private static RimGovernor.Brain.GameFacts.Observations.PawnState Colonist(string id, RimGovernor.Brain.GameFacts.Defs.HungerCategory? hunger) => EmergencyFixtures.Placed(id) with
    {
        Needs = hunger is { } h ? PawnNeedsTests.NoNeeds with { Food = new RimGovernor.Brain.GameFacts.Observations.FoodNeed.Has(0.1, h) } : PawnNeedsTests.NoNeeds,
    };

    private static EmergencySnapshot Census(params RimGovernor.Brain.GameFacts.Observations.PawnState[] rows)
    {
        var status = new RimGovernor.Brain.GameFacts.Observations.StatusSnapshot
        {
            Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = Identity, Tick = 100 }, Threats = new() { Pawns = [], HostileBuildings = [] },
            Colonists = [.. rows.Select(r => new RimGovernor.Brain.GameFacts.Common.Ref { Id = r.Pawn.Id })],
            DependentPatients = new() { Patients = EquatableArray.From<RimGovernor.Brain.GameFacts.Observations.DependentPatientObservation>([]) },
        };
        var facts = GameClient.EmergencyDecode.DecodeEmergencyStatus(status, Pawns.Of(rows)).Facts;
        return EmergencySnapshot.New(World, new(100), facts);
    }

    private static ClockWatchDecision Decide(ClockWatchState state, EmergencySnapshot census, params string[] acknowledged)
    {
        var decision = EvaluateClockWatch(
            new ClockWatchFacts
            {
                Current = World,
                Tick = new(100),
                SinceStarted = TimeSpan.FromSeconds(1),
                SinceObserved = TimeSpan.FromMilliseconds(500),
                Emergency = census,
                EventsOpen = true,
                Status = new ClockWatchStatus { Snapshot = World, Tick = new(100), State = state, NativeTickBoundary = true },
                CombatPlan = false,
                AcknowledgedHungry = EquatableArray.From(acknowledged.Select(a => new PawnId(a))),
            },
            new ClockWatchLimits { MaxAge = TimeSpan.FromSeconds(10) });
        Assert.True(decision.Run, string.Join(",", decision.Refused.Items));
        return decision;
    }

    private static string[] Ids(ClockWatchDecision d) => [.. d.Hungry.Items.Select(p => p.Value)];

    [Fact]
    public void ResumeAcknowledgesColonistsAtTheCategoryButNotThoseWithoutAFoodNeed()
    {
        var census = Census(
            Colonist("fed", RimGovernor.Brain.GameFacts.Defs.HungerCategory.Fed),
            Colonist("hungry", RimGovernor.Brain.GameFacts.Defs.HungerCategory.Hungry),
            Colonist("urgent", RimGovernor.Brain.GameFacts.Defs.HungerCategory.UrgentlyHungry),
            Colonist("starving", RimGovernor.Brain.GameFacts.Defs.HungerCategory.Starving),
            Colonist("foodless", null));

        Assert.Equal(["starving", "urgent"], Ids(Decide(ClockWatchState.Held, census)));
    }

    [Fact]
    public void RunningAdjustKeepsOnlyEarlierAcknowledgementsSoANewlyHungryColonistStillHolds()
    {
        var census = Census(
            Colonist("earlier", RimGovernor.Brain.GameFacts.Defs.HungerCategory.UrgentlyHungry),
            Colonist("since", RimGovernor.Brain.GameFacts.Defs.HungerCategory.UrgentlyHungry),
            Colonist("ate", RimGovernor.Brain.GameFacts.Defs.HungerCategory.Fed));

        Assert.Equal(["earlier"], Ids(Decide(ClockWatchState.Running, census, "earlier", "ate")));
        Assert.Empty(Ids(Decide(ClockWatchState.Running, census)));
    }

    [Fact]
    public void PolicyRequiresARealHungerHoldCategoryAndDistinctAcknowledgements()
    {
        var policy = new RimGovernor.Brain.GameOrders.Clock.WatchPolicy
        {
            SeriousSingleHitDamage = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSingleHitDamage,
            SeriousSummaryHealthFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSummaryHealthFloor,
            SeriousBleedRateFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousBleedRateFloor,
            SeriousVitalPartFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousVitalPartFloor,
            ExplosiveNearMarginCells = RimGovernor.Brain.Policy.HazardThresholdsRules.ExplosiveNearMarginCells,
            MeleeReachCells = RimGovernor.Brain.Policy.HazardThresholdsRules.MeleeReachCells,
            InjurySeverityFloorTicks = RimGovernor.Brain.Policy.HazardThresholdsRules.InjurySeverityFloorTicks,
            HungerHoldCategory = RimGovernor.Brain.Policy.HazardThresholdsRules.HungerHoldCategory,
            Mode = RimGovernor.Brain.GameOrders.Clock.WatchMode.Colony, HealthDropFraction = 0.5f, MinHealthFraction = 0.2f, HostileWithin = 20, InjuryStopCooldownMs = 0,
        };
        Assert.Equal(RimGovernor.Brain.GameFacts.Defs.HungerCategory.UrgentlyHungry, policy.HungerHoldCategory);
        ClockValidation.ValidateClockPolicy(policy);
        ClockValidation.ValidateClockPolicy(policy with { AcknowledgedHungryColonistIds = ["a", "b"] });

        Assert.ThrowsAny<Exception>(() => ClockValidation.ValidateClockPolicy(policy with { HungerHoldCategory = (RimGovernor.Brain.GameFacts.Defs.HungerCategory)int.MaxValue }));
        Assert.ThrowsAny<Exception>(() => ClockValidation.ValidateClockPolicy(policy with { HungerHoldCategory = RimGovernor.Brain.GameFacts.Defs.HungerCategory.Fed }));
        Assert.ThrowsAny<Exception>(() => ClockValidation.ValidateClockPolicy(policy with { AcknowledgedHungryColonistIds = ["a", "a"] }));
    }
}
