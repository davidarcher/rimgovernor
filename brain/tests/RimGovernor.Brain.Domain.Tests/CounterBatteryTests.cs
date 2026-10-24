using RimGovernor.Brain.Policy;
using Xunit;
using NearestColonist = RimGovernor.Contracts.Wire.Observations.NearestColonist;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The counter-battery (#3407, replacing the legacy <c>defense/siege-mortar</c> case): with a siege camped in mortar
/// range and a free colonist, the colonist nearest the mortar crews it with HE on the densest camp clump; a besieger
/// within the friendly danger radius of a colonist is never aimed at, and a siege still travelling in is left alone.
/// </summary>
public sealed class CounterBatteryTests
{
    private static readonly PawnId Near = new("Colonist_near");
    private static readonly PawnId Far = new("Colonist_far");
    private static readonly Cell MortarCell = new(50, 50);

    private static CombatView View(string toil, params (string Id, Cell Cell)[] besiegers) => CombatPawnFixtures.View() with
    {
        Pawns = EquatableArray.From(
            new[] { CombatPawnFixtures.Pawn(Near, new Cell(52, 50)), CombatPawnFixtures.Pawn(Far, new Cell(40, 40)) }
                .Concat(besiegers.Select(b => CombatPawnFixtures.Pawn(new(b.Id), b.Cell) with { Kind = "Mercenary_Gunner" }))),
        Defenders = EquatableArray.From(new[] { Near, Far }.Select(id => new SquadDefenderFacts { Id = id, Dead = false, Downed = false, Armor = new DefenderArmor.Unmirrored() })),
        Positional = EquatableArray.From(besiegers.Select(b => new DefensiveThreatFacts
        {
            Id = new(b.Id),
            Dead = false,
            Downed = false,
            Humanlike = true,
            LordJobClass = "LordJob_Siege",
            LordToilClass = toil,
            NearestColonist = new NearestColonist.None(),
            Position = b.Cell,
            Mech = false,
        })),
        Orderable = EquatableArray.From(new[] { Near, Far }),
        Mortars = EquatableArray.From(new[] { new CombatMortar { Id = "Mortar_1", Cell = MortarCell, MinRange = 3, MaxRange = 60 } }),
        Shells = new MortarShells { HE = "Shell_HighExplosive", Incendiary = "Shell_Incendiary", EMP = "Shell_EMP" },
    };

    [Fact]
    public void CampedSiegeIsShelledWithHeOnItsDensestClump()
    {
        // Three besiegers clumped at (80, 80) and one straggler at (95, 50), all over ten cells from the colonists.
        var view = View(CombatSiegeRules.SiegeCampToil, ("Besieger_1", new(80, 80)), ("Besieger_2", new(82, 80)), ("Besieger_3", new(80, 82)), ("Besieger_4", new(95, 50)));

        var (memory, stood) = CombatMortarRules.CounterBattery(view, [], new CombatMemory());

        Assert.Empty(stood);
        var role = Assert.Single(memory.Roles.Items, r => r.Fire is RoleFire.CrewMortar);
        Assert.Equal(Near, role.Pawn);
        Assert.Equal(new RoleFire.CrewMortar(MortarCell, new Cell(80, 80), "Shell_HighExplosive"), role.Fire);
    }

    [Fact]
    public void BesiegerNearAColonistIsNeverAimedAt()
    {
        // The only camped besieger stands four cells from the far colonist.
        var view = View(CombatSiegeRules.SiegeCampToil, ("Besieger_1", new(44, 40)));

        var (memory, _) = CombatMortarRules.CounterBattery(view, [], new CombatMemory());

        Assert.DoesNotContain(memory.Roles.Items, r => r.Fire is RoleFire.CrewMortar);
    }

    [Fact]
    public void TravellingSiegeIsLeftAloneAndTheLastCrewStoodDown()
    {
        var view = View("LordToil_Travel", ("Besieger_1", new(80, 80)));
        var prev = new[] { new CombatRole { Pawn = Near, Fire = new RoleFire.CrewMortar(MortarCell, new Cell(80, 80), "Shell_HighExplosive") } };

        var (memory, stood) = CombatMortarRules.CounterBattery(view, prev, new CombatMemory());

        Assert.DoesNotContain(memory.Roles.Items, r => r.Fire is RoleFire.CrewMortar);
        Assert.Equal([new MortarStand(MortarCell, Near)], stood);
    }
}
