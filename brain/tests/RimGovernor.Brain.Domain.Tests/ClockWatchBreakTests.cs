using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.ClockWatchRules;

namespace RimGovernor.Brain.Domain.Tests;

// #3572: a colonist's aggressive break that no squad can subdue ends only with ticks. With the defense planner's
// no-squad answer known the break is watched like a fight's hostile; without it, or for a hostile pawn, the clock
// still holds.
public sealed class ClockWatchBreakTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static EmergencyPawn Colonist(string id, bool breaking) => new()
    {
        Id = new PawnId(id),
        Dead = Known(false),
        Downed = Known(false),
        Bleeding = Known(false),
        NeedsTend = Known(false),
        InBed = Known(false),
        Hunger = Known(RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed),
        MentalState = breaking ? new MentalState { DefName = "Slaughterer", IsAggro = true, TicksInState = 10 } : null,
    };

    private static EmergencyThreat Raider(string id) => new()
    {
        Id = new PawnId(id),
        Kind = ThreatKind.Hostile,
        Dead = Known(false),
        Downed = Known(false),
        Animal = Known(false),
        Distance = Known(12.0),
    };

    private static EmergencySnapshot Census(IEnumerable<EmergencyPawn> colonists, params EmergencyThreat[] threats) =>
        EmergencySnapshot.New(World, new(100), new EmergencyFacts
        {
            ColonistsComplete = Known(true),
            DependentPatients = Known(EquatableArray.From<EmergencyDependentPatient>([])),
            Colonists = [.. colonists],
            Threats = threats,
        });

    private static ClockWatchDecision Decide(EmergencySnapshot census, Fact<bool> squadUnanswered) => EvaluateClockWatch(
        new ClockWatchFacts
        {
            Current = World,
            Tick = new(100),
            SinceStarted = TimeSpan.FromSeconds(1),
            SinceObserved = TimeSpan.FromMilliseconds(500),
            Emergency = census,
            EventsOpen = true,
            Status = new ClockWatchStatus { Snapshot = World, Tick = new(100), State = ClockWatchState.Held, NativeTickBoundary = Known(true) },
            CombatPlan = Known(false),
            SquadUnanswered = squadUnanswered,
        },
        new ClockWatchLimits { MaxAge = TimeSpan.FromSeconds(10) });

    private static EmergencySnapshot BreakingColony() => Census([Colonist("brookie", breaking: true), Colonist("ada", breaking: false), Colonist("bo", breaking: false)]);

    [Fact]
    public void AColonistBreakNoSquadCanSubdueIsWatchedSoTheBreakEndsWithTicks()
    {
        var decision = Decide(BreakingColony(), Known(true));

        Assert.True(decision.Run, string.Join(",", decision.Refused.Items));
        Assert.Equal(ClockWatchMode.Combat, decision.Mode);
        string[] watched = [.. decision.Hostiles.Items.Select(p => p.Value)];
        Assert.Equal(["brookie"], watched);
    }

    [Fact]
    public void AColonistBreakHoldsWhileThePlannerHasNotReportedOrFoundASquad()
    {
        Fact<bool>[] answers = [Unknown<bool>(), Known(false)];
        foreach (var answer in answers)
        {
            var decision = Decide(BreakingColony(), answer);

            Assert.False(decision.Run);
            Assert.Contains(ClockWatchReason.Unsafe, decision.Refused.Items);
        }
    }

    [Fact]
    public void AHostilePawnNoSquadCanAnswerStillHolds()
    {
        var decision = Decide(Census([Colonist("ada", breaking: false)], Raider("raider")), Known(true));

        Assert.False(decision.Run);
        Assert.Contains(ClockWatchReason.Unsafe, decision.Refused.Items);
    }
}
