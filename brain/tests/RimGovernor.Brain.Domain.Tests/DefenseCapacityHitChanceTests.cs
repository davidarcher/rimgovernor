using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3845: defense capacity counts each colonist's ranged DPS at the game's hit chance over the reference gunner's
// (PawnEquipment.RangedHitFactor, read natively from ShotReport and the weapon's accuracy at the engagement distance).
// The tests run the production read: the pawn rows as native gives them, decoded by RoundsDefenders, summed by
// DefenseCapacityPolicy.
public sealed class DefenseCapacityHitChanceTests
{
    // The rows of a colonist with these combat facts, as the pawn read gives them.
    private static O.PawnState Row(string id, double rangedDps, double hitFactor, double meleeDps) => PawnFixtures.Row(id) with
    {
        Equipment = GearFixtures.NoEquipment with { RangedDps = rangedDps, RangedHitFactor = hitFactor, MeleeDps = meleeDps },
    };

    private static double Capacity(params O.PawnState[] rows)
    {
        var emergency = new EmergencyFacts
        {
            Colonists = [.. rows.Select(r => new EmergencyPawn { Id = new PawnId(r.Pawn.Id), Dead = Known(false), Downed = Known(false) })],
        };
        var colony = ColonyFactsCoreTests.Snapshot() with { ColonistCount = (uint)rows.Length };
        var pawns = new O.PawnSnapshot { Context = colony.Context, Pawns = EquatableArray.From(rows) };
        var defenders = RoundsMoodReads.RoundsDefenders(colony, emergency, pawns);
        Assert.True(DefenseCapacityPolicy.DefenseCapacity(defenders, Known(EquatableArray.From(new List<DefenseTurretFacts>()))).TryGetValue(out var capacity));
        return capacity;
    }

    [Fact]
    public void TwoColonistsDifferingOnlyInShootingHaveRangedCapacityInTheRatioOfTheirHitChances()
    {
        // The same rifle (11 DPS), no melee: a level-4 shooter's hit factor 0.594 and a level-16 shooter's 1.81
        // (ShootingAccuracyPawn 0.94 and 0.98333 at 24.72 cells, the rifle's 0.651, over the reference 0.2374).
        var poor = Capacity(Row("poor", 11, 0.594, 0));
        var good = Capacity(Row("good", 11, 1.81, 0));
        Assert.Equal(1.81 / 0.594, good / poor, 9);
        Assert.Equal(11 * 1.81 * DefenseCapacityPolicy.DefensePointsPerDps, good, 9);
    }

    [Fact]
    public void MeleeIsUnchangedByTheHitFactor()
    {
        // A brawler's melee DPS (already hit-weighted by the game's MeleeDPS stat) counts as before, whatever the factor.
        Assert.Equal(3 * DefenseCapacityPolicy.DefensePointsPerDps, Capacity(Row("brawler", 0, 0, 3)), 9);
        Assert.Equal(3 * DefenseCapacityPolicy.DefensePointsPerDps, Capacity(Row("a", 11, 0.5, 3)) - Capacity(Row("b", 11, 0.5, 0)), 9);
    }

    [Fact]
    public void AReferenceGunnerCountsItsRawRangedDpsSoTheExposureScaleHolds()
    {
        // Every gunner the exposure calibration staged is the reference gunner (a level-8 lab colonist with a normal
        // assault rifle): its factor is 1 and its capacity, hence every committed ratio, is unchanged.
        Assert.Equal((11 + 2) * DefenseCapacityPolicy.DefensePointsPerDps, Capacity(Row("lab", 11, 1, 2)), 9);
    }

    [Fact]
    public void AFreshColonysCapacityIsReadWithAnUnarmedColonistAsTheNoRangedWeaponState()
    {
        // A crashlanded start: two armed colonists and one with no weapon. The unarmed colonist reads RangedDps 0 and
        // hit factor 0 (no ranged primary lands no shots) and adds only melee; the capacity is known.
        var capacity = Capacity(Row("rifle", 11, 1.2, 2), Row("revolver", 6, 0.9, 2), Row("unarmed", 0, 0, 1.5));
        Assert.Equal((11 * 1.2 + 2 + 6 * 0.9 + 2 + 1.5) * DefenseCapacityPolicy.DefensePointsPerDps, capacity, 9);
    }
}
