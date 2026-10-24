using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using NearestColonist = RimGovernor.Contracts.Wire.Observations.NearestColonist;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Hostile threat facts and the combat role read whole (#3911): a positional threat is every member of an on-map
/// hostile's row (an off-map hostile is not one), a combat role's post, standby, fire and repair are duty-shaped
/// states, and the memory's sapper breach is guarded or not. Each state round-trips the store.
/// </summary>
public sealed class CombatRoleDutyTests
{
    private static readonly PawnId Gunner = new("Colonist_gunner");
    private static readonly PawnId Raider = new("Raider_1");
    private static readonly Cell Here = new(10, 10);

    private static CombatPawnState At(Cell cell) => CombatPawnFixtures.Pawn(Gunner, cell);

    [Fact]
    public void AFreeRoleAttacksItsTargetAndAsksNothingWithout()
    {
        var role = new CombatRole { Pawn = Gunner, Target = Raider };

        var (order, ok) = role.Want(At(Here));
        Assert.True(ok);
        Assert.Equal((CombatOrderKind.Attack, Raider), (order.Kind, order.Target));

        Assert.False((role with { Target = default }).Want(At(Here)).Ok);
    }

    [Fact]
    public void APostedRoleMovesToItsCellThenAttacks()
    {
        var role = new CombatRole { Pawn = Gunner, Target = Raider, Post = new CombatPost.At(new Cell(12, 10)) };

        var (move, _) = role.Want(At(Here));
        Assert.Equal((CombatOrderKind.Move, new Cell(12, 10), CombatOrderReason.Formation), (move.Kind, move.Cell, move.Reason));
        Assert.Equal(CombatOrderReason.Retreat, (role with { Retreat = true }).Want(At(Here)).Order.Reason);
        Assert.Equal(CombatOrderKind.Attack, role.Want(At(new Cell(12, 10))).Order.Kind);
    }

    [Fact]
    public void AMortarCrewOrAGroundShotWinsOverThePostAndTheTarget()
    {
        var posted = new CombatRole { Pawn = Gunner, Target = Raider, Post = new CombatPost.At(new Cell(12, 10)) };

        var (crew, _) = (posted with { Fire = new RoleFire.CrewMortar(new Cell(5, 5), new Cell(80, 80), "Shell_HighExplosive") }).Want(At(Here));
        Assert.Equal(
            (CombatOrderKind.ManMortar, new Cell(5, 5), new Cell(80, 80), "Shell_HighExplosive", CombatOrderReason.CounterBattery),
            (crew.Kind, crew.Cell, crew.Aim, crew.Shell, crew.Reason));

        var (shot, _) = (posted with { Fire = new RoleFire.AtGround(new Cell(30, 30)) }).Want(At(Here));
        Assert.Equal((CombatOrderKind.AttackGround, new Cell(30, 30), CombatOrderReason.RocketClump), (shot.Kind, shot.Cell, shot.Reason));
    }

    [Fact]
    public void RetreatCellReadsAFreePostAsTheFirstOpenRetreatCell()
    {
        var layout = new CombatLayout
        {
            Firing = EquatableArray.From(new[] { new Cell(10, 10), new Cell(11, 11) }),
            Retreat = EquatableArray.From(new[] { new Cell(1, 1), new Cell(9, 9) }),
            Toward = Rotation.North,
            Choke = new CombatChoke.NoCorridor(),
        };

        Assert.Equal(new Cell(1, 1), CombatFallbackRules.RetreatCell(layout, new CombatPost.Free(), []));
        Assert.Equal(new Cell(9, 9), CombatFallbackRules.RetreatCell(layout, new CombatPost.At(new Cell(11, 11)), []));
    }

    [Fact]
    public void EveryRoleStateAndTheSapperGuardRoundTripTheStore()
    {
        var roles = new[]
        {
            new CombatRole { Pawn = Gunner },
            new CombatRole { Pawn = Gunner, Target = Raider, Ranged = true, Post = new CombatPost.At(new Cell(3, 4)), Standby = new CombatPost.At(new Cell(5, 6)) },
            new CombatRole { Pawn = Gunner, Fire = new RoleFire.CrewMortar(new Cell(7, 8), new Cell(70, 80), "Shell_EMP") },
            new CombatRole { Pawn = Gunner, Fire = new RoleFire.CrewMortar(new Cell(7, 8), new Cell(70, 80), "") },
            new CombatRole { Pawn = Gunner, Post = new CombatPost.At(new Cell(1, 2)), Fire = new RoleFire.AtGround(new Cell(40, 41)) },
            new CombatRole { Pawn = Gunner, Duty = CombatDuty.Blocker, Retreat = true, RepairDoor = new RoleRepair.Door(new Cell(9, 9)) },
        };
        foreach (var role in roles)
        {
            Assert.Equal(role, StoreJson.Deserialize<CombatRole>(StoreJson.Serialize(role)));
        }
        Assert.DoesNotContain("Shell", StoreJson.Serialize(roles[1]));

        foreach (var guard in new BreachGuard[] { new BreachGuard.Unguarded(), new BreachGuard.Guarding(new Cell(20, 21)) })
        {
            var memory = new CombatMemory { SapperGuard = guard, Roles = EquatableArray.From(roles) };
            var back = StoreJson.Deserialize<CombatMemory>(StoreJson.Serialize(memory));
            Assert.Equal(guard, back.SapperGuard);
            Assert.Equal(memory.Roles, back.Roles);
        }
    }

    private static DefensiveThreatFacts Raid(string? toil) => new()
    {
        Id = Raider,
        Dead = false,
        Downed = false,
        Humanlike = true,
        LordJobClass = "LordJob_AssaultColony",
        LordToilClass = toil,
        NearestColonist = new NearestColonist.None(),
        Position = new Cell(10, 5),
        Mech = false,
    };

    private static readonly Cell[] Firing = [new Cell(10, 20), new Cell(12, 20)];

    private static readonly SquadDefenderFacts Shooter = new() { Id = Gunner, Dead = false, Downed = false, Armor = new DefenderArmor.Unmirrored(), RangedEquipped = true, ViolenceCapable = true, HealthFraction = 1 };

    [Fact]
    public void AHostileBetweenLordToilsRefusesTheHoldButNeverBreaksOne()
    {
        var (_, refusal) = DefensePositions.ExplainDefensivePositions(Firing, Rotation.North, [Raid(null)], [Shooter]);
        Assert.Equal("hostile Raider_1: no lord toil (job \"LordJob_AssaultColony\", toil \"\")", refusal);
        Assert.False(DefensePositions.HoldCompromised(Firing, Rotation.North, [Raid(null)]));

        var (positions, held) = DefensePositions.ExplainDefensivePositions(Firing, Rotation.North, [Raid("LordToil_AssaultColony")], [Shooter]);
        Assert.Equal("", held);
        Assert.Equal([new DefensivePosition(Gunner, new Cell(10, 20), Raider)], positions);
        Assert.True(DefensePositions.HoldCompromised(Firing, Rotation.North, [Raid("LordToil_AssaultColonySappers")]));
    }

    [Fact]
    public void AnAnimalThreatIsNotHumanlikeUnlessItIsAMech()
    {
        var animal = Raid("LordToil_AssaultColony") with { Humanlike = false };

        Assert.Equal("hostile Raider_1: not humanlike", DefensePositions.ExplainDefensivePositions(Firing, Rotation.North, [animal], [Shooter]).Refusal);
        Assert.Equal("", DefensePositions.ExplainDefensivePositions(Firing, Rotation.North, [animal with { Mech = true }], [Shooter]).Refusal);
    }

    [Fact]
    public void ThePositionalThreatReadsTheWholeRowAndLeavesAnOffMapHostileOut()
    {
        var catalog = new DefinitionCatalog([new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }], DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));
        var row = EmergencyFixtures.Placed("Raider_1", 7, 9) with
        {
            LordJobClass = "LordJob_AssaultColony",
            LordToilClass = "LordToil_AssaultColony",
            NearestColonist = new NearestColonist.At(12.5),
        };

        var facts = RoundsDefensePlanner.DefensiveThreatFacts(row, catalog);
        Assert.NotNull(facts);
        Assert.Equal(
            (Raider, new Cell(7, 9), "LordToil_AssaultColony", (NearestColonist)new NearestColonist.At(12.5), false),
            (facts.Id, facts.Position, facts.LordToilClass, facts.NearestColonist, facts.Mech));

        Assert.Null(RoundsDefensePlanner.DefensiveThreatFacts(row with { Pawn = row.Pawn with { Place = new O.EntityPlace.Unplaced() } }, catalog));
    }
}
