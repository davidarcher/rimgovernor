using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The roster hunt's formation (#3515, <see cref="CombatHuntRules.HuntFormation"/>): only the view's HuntHunters are
/// sent, a rostered melee or outranged gunner is left out for the reason <see cref="CombatHuntRules.HuntFormationRefusals"/>
/// names, an armed defender outside the roster is never sent, an empty roster or no prey gives no roles with the
/// right refusal. A gunner stages within the estimate's engage distance (#3702): where he stands when already inside
/// it, else straight in to it; an unreachable stage cell leaves him out, the prey walking off a role re-forms the hunt,
/// and a roster pawn with no role is let go.
/// </summary>
public sealed class CombatHuntTests
{
    private static readonly PawnId Rifleman = new("Rifleman");
    private static readonly PawnId Marksman = new("Marksman");
    private static readonly PawnId Swordsman = new("Swordsman");
    private static readonly PawnId Pistoleer = new("Pistoleer");
    private static readonly PawnId Guard = new("Guard");
    private static readonly PawnId Deer = new("Deer_1");
    private static readonly PawnId Elk = new("Elk_1");

    private const string RifleDef = "Gun_BoltActionRifle";
    private const string SwordDef = "MeleeWeapon_LongSword";
    private const string PistolDef = "Gun_Autopistol";

    private static readonly WeaponDef Rifle = new() { Ranged = true, Range = 37, Reach = 37 };
    private static readonly WeaponDef ShortRifle = new() { Ranged = true, Range = 25, Reach = 25 };
    private static readonly WeaponDef Pistol = new() { Ranged = true, Range = 12, Reach = 12 };
    private static readonly WeaponDef Sword = new() { Melee = true, Reach = 1 };

    private static CombatPawnState Gunner(PawnId id, Cell cell, string weapon, WeaponDef facts) =>
        CombatPawnFixtures.Pawn(id, cell) with { Weapon = weapon, WeaponFacts = facts };

    private static CombatPawnState Animal(PawnId id, Cell cell, double weaponRange = 0) =>
        CombatPawnFixtures.Pawn(id, cell) with { Kind = id.Value.Split('_')[0], WeaponFacts = new WeaponDef { Reach = weaponRange } };

    /// <summary>A defender every shared squad gate admits: alive, up, undrafted, violence-capable, untended, whole.</summary>
    private static SquadDefenderFacts Defender(PawnId id) => new()
    {
        Id = id,
        Dead = false,
        Downed = false,
        Drafted = false,
        ViolenceCapable = true,
        NeedsTend = false,
        HealthFraction = 1.0,
        Armor = new DefenderArmor.Unmirrored(),
    };

    private static SquadThreatFacts Prey(PawnId id) => new() { Id = id, Dead = false, Downed = false, Animal = true, Anomaly = new RimGovernor.Contracts.Wire.Observations.PawnAnomalyState.Inactive() };

    private static CombatView View(IEnumerable<CombatPawnState> pawns, IEnumerable<PawnId> defenders, IEnumerable<PawnId> prey, IEnumerable<PawnId> roster)
    {
        var ids = defenders.ToArray();
        return CombatPawnFixtures.View() with
        {
            Pawns = EquatableArray.From(pawns),
            Defenders = EquatableArray.From(ids.Select(Defender)),
            Threats = EquatableArray.From(prey.Select(Prey)),
            Orderable = EquatableArray.From(ids),
            Hunt = true,
            HuntHunters = EquatableArray.From(roster),
        };
    }

    // Two rostered riflemen and an armed guard outside the roster, a deer twenty cells north of them.
    private static CombatView TwoGunners(params PawnId[] roster) => View(
        [
            Gunner(Rifleman, new Cell(48, 50), RifleDef, Rifle),
            Gunner(Marksman, new Cell(52, 50), RifleDef, Rifle),
            Gunner(Guard, new Cell(50, 46), RifleDef, Rifle),
            Animal(Deer, new Cell(50, 70)),
        ],
        [Rifleman, Marksman, Guard],
        [Deer],
        roster);

    private static double Distance(Cell a, Cell b)
    {
        double dx = a.X - b.X;
        double dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    [Fact]
    public void OnlyTheRosteredHuntersAreSent()
    {
        var roles = CombatHuntRules.HuntFormation(TwoGunners(Rifleman, Marksman), []);

        Assert.Equal(new[] { Marksman, Rifleman }, roles.Select(r => r.Pawn).Order().ToArray());
        Assert.All(roles, r =>
        {
            Assert.Equal(Deer, r.Target);
            Assert.True(r.Ranged);
            Assert.NotNull(r.Cell);
        });
    }

    [Fact]
    public void ArmedDefenderOutsideTheRosterIsNeverSent()
    {
        var view = TwoGunners(Rifleman);

        var roles = CombatHuntRules.HuntFormation(view, []);

        var role = Assert.Single(roles);
        Assert.Equal(Rifleman, role.Pawn);
        Assert.DoesNotContain(roles, r => r.Pawn == Guard);
        Assert.DoesNotContain(CombatHuntRules.HuntFormationRefusals(view), line => line.StartsWith(Guard.Value + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void RosteredMeleeAndOutrangedGunnersAreLeftOutForTheNamedReason()
    {
        // The elk shoots back at 20 cells (an armed prey): the pistol is outranged, the sword is no hunting weapon, the rifle hunts.
        var view = View(
            [
                Gunner(Rifleman, new Cell(48, 50), RifleDef, Rifle),
                Gunner(Swordsman, new Cell(50, 50), SwordDef, Sword),
                Gunner(Pistoleer, new Cell(52, 50), PistolDef, Pistol),
                Animal(Elk, new Cell(50, 70), weaponRange: 20),
            ],
            [Rifleman, Swordsman, Pistoleer],
            [Elk],
            [Rifleman, Swordsman, Pistoleer]);

        var roles = CombatHuntRules.HuntFormation(view, []);
        var refusals = CombatHuntRules.HuntFormationRefusals(view);

        var role = Assert.Single(roles);
        Assert.Equal(Rifleman, role.Pawn);
        Assert.Contains(Rifleman.Value + ":eligible", refusals);
        Assert.Contains(Swordsman.Value + ":outranged|weapon_does_not_hunt(" + SwordDef + ",not_ranged)", refusals);
        Assert.Contains(Pistoleer.Value + ":outranged", refusals);
    }

    [Fact]
    public void EmptyRosterGivesNoRolesAndNoRoster()
    {
        var view = TwoGunners();

        Assert.Empty(CombatHuntRules.HuntFormation(view, []));
        var refusals = CombatHuntRules.HuntFormationRefusals(view);
        Assert.Contains("no_roster", refusals);
        Assert.DoesNotContain("no_prey", refusals);
    }

    [Fact]
    public void NoPreyGivesNoRolesAndNoPrey()
    {
        var view = View(
            [Gunner(Rifleman, new Cell(48, 50), RifleDef, Rifle), Gunner(Marksman, new Cell(52, 50), RifleDef, Rifle)],
            [Rifleman, Marksman],
            [],
            [Rifleman, Marksman]);

        Assert.Empty(CombatHuntRules.HuntFormation(view, []));
        var refusals = CombatHuntRules.HuntFormationRefusals(view);
        Assert.Contains("no_prey", refusals);
        Assert.Contains(Rifleman.Value + ":eligible", refusals);
        Assert.DoesNotContain("no_roster", refusals);
    }

    [Fact]
    public void DownedOrDeadPreyIsNotHunted()
    {
        var view = TwoGunners(Rifleman, Marksman) with
        {
            Threats = EquatableArray.From(new[] { Prey(Deer) with { Downed = true } }),
        };

        Assert.Empty(CombatHuntRules.HuntFormation(view, []));
        Assert.Contains("no_prey", CombatHuntRules.HuntFormationRefusals(view));
    }

    [Fact]
    public void GunnersAlreadyWithinTheirStageDistanceShootFromWhereTheyStand()
    {
        // The riflemen stand twenty cells from the deer, inside the 37-cell rifle's stage distance: nobody walks out to range (#3702).
        var view = TwoGunners(Rifleman, Marksman);

        var roles = CombatHuntRules.HuntFormation(view, []);

        Assert.Equal(new Cell(48, 50), roles.Single(r => r.Pawn == Rifleman).Cell);
        Assert.Equal(new Cell(52, 50), roles.Single(r => r.Pawn == Marksman).Cell);
    }

    [Fact]
    public void AGunnerBeyondHisStageDistanceWalksStraightInToItWithEveryPreyInReach()
    {
        // A 25-cell rifle sixty cells south of a deer and an elk six cells apart (centre 53,70, spread 3): he stops on the
        // line to the centre at the estimate's engage distance less the spread and half a diagonal, floor(23.75-3-0.71)=20.
        var preyCells = new[] { new Cell(50, 70), new Cell(56, 70) };
        var view = View(
            [Gunner(Marksman, new Cell(53, 10), RifleDef, ShortRifle), Animal(Deer, preyCells[0]), Animal(Elk, preyCells[1])],
            [Marksman],
            [Deer, Elk],
            [Marksman]);

        var role = Assert.Single(CombatHuntRules.HuntFormation(view, []));

        Assert.Equal(20, CombatHuntRules.StageDistance(ShortRifle.Range, 3));
        Assert.Equal(new Cell(53, 50), role.Cell);
        Assert.All(preyCells, p => Assert.True(Distance(role.Cell!.Value, p) <= HuntEstimator.EngageRangeFactor * ShortRifle.Range, $"prey at {p} is beyond the engage distance"));
    }

    [Fact]
    public void AGunnerWhoseStageCellIsUnreachableIsLeftOut()
    {
        var view = View(
            [Gunner(Rifleman, new Cell(50, 0), RifleDef, Rifle), Gunner(Marksman, new Cell(52, 50), RifleDef, Rifle), Animal(Deer, new Cell(50, 70))],
            [Rifleman, Marksman],
            [Deer],
            [Rifleman, Marksman]);
        var stage = CombatHuntRules.StageCell(new Cell(50, 0), new Cell(50, 70), CombatHuntRules.StageDistance(Rifle.Range, 0));

        var role = Assert.Single(CombatHuntRules.HuntFormation(view, [stage]));

        Assert.Equal(new Cell(50, 36), stage);
        Assert.Equal(Marksman, role.Pawn);
    }

    [Fact]
    public void ThePreyWalkingBeyondARolesRangeOrOutOfTheViewReformsTheHunt()
    {
        var memory = new CombatMemory
        {
            Tactic = CombatTactic.Hunt,
            Roles = EquatableArray.From(new[] { new CombatRole { Pawn = Rifleman, Cell = new Cell(50, 50), Target = Deer, Ranged = true } }),
        };
        CombatView At(Cell deer) => View([Gunner(Rifleman, new Cell(50, 50), RifleDef, Rifle), Animal(Deer, deer)], [Rifleman], [Deer], [Rifleman]);

        // Within the rifle's 37 cells the formation stands; at 40 the deer has walked off it.
        Assert.False(CombatHuntRules.PreyOutOfReach(At(new Cell(50, 85)), memory));
        Assert.False(CombatHuntRules.ReformHunt(At(new Cell(50, 85)), memory));
        Assert.True(CombatHuntRules.PreyOutOfReach(At(new Cell(50, 90)), memory));
        Assert.True(CombatHuntRules.ReformHunt(At(new Cell(50, 90)), memory));

        // A target gone from the prey (another animal now hunted) re-forms too.
        var other = View([Gunner(Rifleman, new Cell(50, 50), RifleDef, Rifle), Animal(Elk, new Cell(50, 60))], [Rifleman], [Elk], [Rifleman]);
        Assert.True(CombatHuntRules.PreyOutOfReach(other, memory));
    }

    [Fact]
    public void RosterPawnsTheFormationGivesNoRoleAreLetGo()
    {
        var roles = new[] { new CombatRole { Pawn = Rifleman, Target = Deer, Ranged = true } };

        Assert.Equal(new[] { Guard, Marksman }, CombatHuntRules.IdleHunters([Rifleman, Marksman, Guard], roles));
        Assert.Empty(CombatHuntRules.IdleHunters([Rifleman], roles));
    }
}
