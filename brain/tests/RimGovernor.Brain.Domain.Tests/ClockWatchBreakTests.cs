using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Policy.ClockWatchRules;

namespace RimGovernor.Brain.Domain.Tests;

// #3572: a colonist's aggressive break that no squad can subdue ends only with ticks. With the defense planner's
// no-squad answer known the break is watched like a fight's hostile; without it, or for a hostile pawn, the clock
// still holds.
public sealed class ClockWatchBreakTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static EmergencyPawn Colonist(string id, bool breaking) => EmergencyFixtures.Colonist(id) with
    {
        Observed = EmergencyFixtures.Placed(id, 10, 10) with { MentalState = breaking ? new RimGovernor.Contracts.Wire.Observations.PawnMentalState { DefName = "Slaughterer", IsAggro = true, Ticks = 10 } : null },
    };

    private static EmergencyThreat Raider(string id) => EmergencyFixtures.Threat(id);

    private static EmergencySnapshot Census(IEnumerable<EmergencyPawn> colonists, params EmergencyThreat[] threats) =>
        EmergencySnapshot.New(World, new(100), new EmergencyFacts
        {
            Colonists = [.. colonists],
            Threats = threats,
        });

    private static ClockWatchDecision Decide(EmergencySnapshot census, SquadAnswer squad) => EvaluateClockWatch(
        new ClockWatchFacts
        {
            Current = World,
            Tick = new(100),
            SinceStarted = TimeSpan.FromSeconds(1),
            SinceObserved = TimeSpan.FromMilliseconds(500),
            Emergency = census,
            EventsOpen = true,
            Status = new ClockWatchStatus { Snapshot = World, Tick = new(100), State = ClockWatchState.Held, NativeTickBoundary = true },
            CombatPlan = false,
            Squad = squad,
        },
        new ClockWatchLimits { MaxAge = TimeSpan.FromSeconds(10) });

    private static EmergencySnapshot BreakingColony() => Census([Colonist("brookie", breaking: true), Colonist("ada", breaking: false), Colonist("bo", breaking: false)]);

    [Fact]
    public void AColonistBreakNoSquadCanSubdueIsWatchedSoTheBreakEndsWithTicks()
    {
        var decision = Decide(BreakingColony(), SquadAnswer.NoSquad);

        Assert.True(decision.Run, string.Join(",", decision.Refused.Items));
        Assert.Equal(ClockWatchMode.Combat, decision.Mode);
        string[] watched = [.. decision.Hostiles.Items.Select(p => p.Value)];
        Assert.Equal(["brookie"], watched);
    }

    [Fact]
    public void AColonistBreakHoldsWhileThePlannerHasNotReportedOrFoundASquad()
    {
        SquadAnswer[] answers = [SquadAnswer.NotReported, SquadAnswer.Squad];
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
        var decision = Decide(Census([Colonist("ada", breaking: false)], Raider("raider")), SquadAnswer.NoSquad);

        Assert.False(decision.Run);
        Assert.Contains(ClockWatchReason.Unsafe, decision.Refused.Items);
    }
}
