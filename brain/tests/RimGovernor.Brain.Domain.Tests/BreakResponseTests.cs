using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

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

    private static readonly Fact<Cell> At = Known(new Cell(10, 10));

    private static EmergencyPawn Breaker() => EmergencyFixtures.Colonist("brookie") with
    {
        MentalState = new MentalState { DefName = "Berserk", IsAggro = true, TicksInState = 10 },
    };

    private static BreakResponder Pawn(string id, Arms arms, int distance, bool breaking = false) => new()
    {
        Cell = Known(new Cell(10 + distance, 10)),
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
}
