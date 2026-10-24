using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Defs = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.ClockWatchRules;

namespace RimGovernor.Brain.Domain.Tests;

// #3435: native holds a colony run for an unacknowledged colonist at the hunger hold category, so the review that
// answers them runs under a held clock. Only a resume acknowledges; adjusting a running clock never does.
public sealed class ClockWatchHungerTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private static readonly C.Identity Identity = new() { ColonyId = "colony", MapId = 1, LoadToken = "load" };

    private static O.PawnState Colonist(string id, Defs.HungerCategory? hunger) => EmergencyFixtures.Placed(id) with
    {
        Needs = hunger is { } h ? PawnNeedsTests.NoNeeds with { Food = new O.FoodNeed.Has(0.1, h) } : PawnNeedsTests.NoNeeds,
    };

    private static EmergencySnapshot Census(params O.PawnState[] rows)
    {
        var status = new O.StatusSnapshot
        {
            Context = new() { Identity = Identity, Tick = 100 }, Threats = new() { Pawns = [], HostileBuildings = [] },
            Colonists = [.. rows.Select(r => new C.Ref { Id = r.Pawn.Id })],
            DependentPatients = new() { Patients = EquatableArray.From<O.DependentPatientObservation>([]) },
        };
        var facts = FacadeNativeClient.EmergencyDecode.DecodeEmergencyStatus(status, Pawns.Of(rows)).Facts;
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
                Status = new ClockWatchStatus { Snapshot = World, Tick = new(100), State = state, NativeTickBoundary = Known(true) },
                CombatPlan = Known(false),
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
            Colonist("fed", Defs.HungerCategory.Fed),
            Colonist("hungry", Defs.HungerCategory.Hungry),
            Colonist("urgent", Defs.HungerCategory.UrgentlyHungry),
            Colonist("starving", Defs.HungerCategory.Starving),
            Colonist("foodless", null));

        Assert.Equal(["starving", "urgent"], Ids(Decide(ClockWatchState.Held, census)));
    }

    [Fact]
    public void RunningAdjustKeepsOnlyEarlierAcknowledgementsSoANewlyHungryColonistStillHolds()
    {
        var census = Census(
            Colonist("earlier", Defs.HungerCategory.UrgentlyHungry),
            Colonist("since", Defs.HungerCategory.UrgentlyHungry),
            Colonist("ate", Defs.HungerCategory.Fed));

        Assert.Equal(["earlier"], Ids(Decide(ClockWatchState.Running, census, "earlier", "ate")));
        Assert.Empty(Ids(Decide(ClockWatchState.Running, census)));
    }

    [Fact]
    public void PolicyRequiresARealHungerHoldCategoryAndDistinctAcknowledgements()
    {
        var policy = ClockExpectation.WithHazardThresholds(new Clock.WatchPolicy
        {
            Mode = Clock.WatchMode.Colony, HealthDropFraction = 0.5f, MinHealthFraction = 0.2f, HostileWithin = 20, InjuryStopCooldownMs = 0,
        });
        Assert.Equal(Defs.HungerCategory.UrgentlyHungry, policy.HungerHoldCategory);
        ClockValidation.ValidateClockPolicy(policy);
        ClockValidation.ValidateClockPolicy(policy with { AcknowledgedHungryColonistIds = ["a", "b"] });

        Assert.ThrowsAny<Exception>(() => ClockValidation.ValidateClockPolicy(policy with { HungerHoldCategory = null }));
        Assert.ThrowsAny<Exception>(() => ClockValidation.ValidateClockPolicy(policy with { HungerHoldCategory = Defs.HungerCategory.Fed }));
        Assert.ThrowsAny<Exception>(() => ClockValidation.ValidateClockPolicy(policy with { AcknowledgedHungryColonistIds = ["a", "a"] }));
    }
}
