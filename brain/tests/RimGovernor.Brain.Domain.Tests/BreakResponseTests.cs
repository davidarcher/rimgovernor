using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3573: SUBDUE admits melee-armed and unarmed responders (fists, as the game's own subdue) and never a
// ranged-equipped one; a fists-only squad is refused against a breaker wielding a melee weapon.
public sealed class BreakResponseTests
{
    private enum Arms
    {
        Unarmed,
        Melee,
        Ranged,
    }

    private static readonly EntityPlace At = new EntityPlace.OnMap(0, new RimGovernor.Brain.Domain.Cell { X = 10, Z = 10 });

    private static EmergencyPawn Breaker() => EmergencyFixtures.Colonist("brookie") with
    {
        Observed = EmergencyFixtures.Placed("brookie", 10, 10) with { MentalState = new PawnMentalState { DefName = "Berserk", IsAggro = true, Ticks = 10 } },
    };

    private static BreakResponder Pawn(string id, Arms arms, int distance, bool breaking = false) => new()
    {
        Place = new EntityPlace.OnMap(0, new RimGovernor.Brain.Domain.Cell { X = 10 + distance, Z = 10 }),
        SquadDefenderFacts = new SquadDefenderFacts
        {
            Id = new PawnId(id),
            Dead = false,
            Downed = false,
            Drafted = false,
            MentalState = breaking,
            ViolenceCapable = true,
            NeedsTend = false,
            HealthFraction = 1.0,
            Armor = new DefenderArmor.Unmirrored(),
            Armed = arms != Arms.Unarmed,
            MeleeEquipped = arms == Arms.Melee,
            RangedEquipped = arms == Arms.Ranged,
        },
    };

    private static string[] Squad(params BreakResponder[] candidates) =>
        [.. BreakResponseRules.SelectBreakSquad(Breaker(), At, candidates).Select(p => p.Value)];

    [Fact]
    public void UnarmedRespondersSubdueAnUnarmedBreaker()
    {
        var squad = Squad(Pawn("brookie", Arms.Unarmed, 0, breaking: true), Pawn("ada", Arms.Unarmed, 2), Pawn("bo", Arms.Unarmed, 3));

        Assert.Equal(["ada", "bo"], squad);
    }

    [Fact]
    public void AFistsOnlySquadIsRefusedAgainstAMeleeArmedBreaker()
    {
        var squad = Squad(Pawn("brookie", Arms.Melee, 0, breaking: true), Pawn("ada", Arms.Unarmed, 2), Pawn("bo", Arms.Unarmed, 3));

        Assert.Empty(squad);
    }

    [Fact]
    public void AMeleeArmedBreakerIsMetByTheNearestMeleeArmedResponderFirst()
    {
        var squad = Squad(Pawn("brookie", Arms.Melee, 0, breaking: true), Pawn("ada", Arms.Unarmed, 2), Pawn("bo", Arms.Unarmed, 3), Pawn("cy", Arms.Melee, 6));

        Assert.Equal(["cy", "ada"], squad);
    }

    [Fact]
    public void RangedEquippedRespondersAreNeverChosen()
    {
        var squad = Squad(Pawn("brookie", Arms.Unarmed, 0, breaking: true), Pawn("ada", Arms.Ranged, 1), Pawn("bo", Arms.Ranged, 2), Pawn("cy", Arms.Unarmed, 6));

        Assert.Equal(["cy"], squad);
    }

    [Fact]
    public void ARespondersOnNoMapIsNeverChosen()
    {
        var away = Pawn("ada", Arms.Unarmed, 1) with { Place = new EntityPlace.Unplaced() };

        Assert.Equal(["bo", "cy"], Squad(Pawn("brookie", Arms.Unarmed, 0, breaking: true), away, Pawn("bo", Arms.Unarmed, 3), Pawn("cy", Arms.Unarmed, 4)));
    }

    [Fact]
    public void ABreakerOnNoMapHasNoSquad()
    {
        var squad = BreakResponseRules.SelectBreakSquad(Breaker(), new EntityPlace.Unplaced(), [Pawn("ada", Arms.Unarmed, 1)]);

        Assert.Empty(squad);
    }
}
