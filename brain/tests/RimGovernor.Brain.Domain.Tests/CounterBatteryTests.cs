using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

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

    private static CombatView View(string toil, params (string Id, Cell Cell)[] besiegers) => new()
    {
        Pawns = EquatableArray.From(
            new[] { new CombatPawnState { Id = Near, Cell = Known(new Cell(52, 50)) }, new CombatPawnState { Id = Far, Cell = Known(new Cell(40, 40)) } }
                .Concat(besiegers.Select(b => new CombatPawnState { Id = new(b.Id), Cell = Known(b.Cell), Kind = "Mercenary_Gunner" }))),
        Defenders = EquatableArray.From(new[] { Near, Far }.Select(id => new SquadDefenderFacts { Id = id, Dead = Known(false), Downed = Known(false) })),
        Positional = EquatableArray.From(besiegers.Select(b => new DefensiveThreatFacts
        {
            Id = new(b.Id),
            Dead = Known(false),
            Downed = Known(false),
            LordJobClass = Known("LordJob_Siege"),
            LordToilClass = Known(toil),
            Position = Known(b.Cell),
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
        var role = Assert.Single(memory.Roles.Items, r => r.Mortar is not null);
        Assert.Equal(Near, role.Pawn);
        Assert.Equal(MortarCell, role.Mortar);
        Assert.Equal(new Cell(80, 80), role.Aim);
        Assert.Equal("Shell_HighExplosive", role.Shell);
    }

    [Fact]
    public void BesiegerNearAColonistIsNeverAimedAt()
    {
        // The only camped besieger stands four cells from the far colonist.
        var view = View(CombatSiegeRules.SiegeCampToil, ("Besieger_1", new(44, 40)));

        var (memory, _) = CombatMortarRules.CounterBattery(view, [], new CombatMemory());

        Assert.DoesNotContain(memory.Roles.Items, r => r.Mortar is not null);
    }

    [Fact]
    public void TravellingSiegeIsLeftAloneAndTheLastCrewStoodDown()
    {
        var view = View("LordToil_Travel", ("Besieger_1", new(80, 80)));
        var prev = new[] { new CombatRole { Pawn = Near, Mortar = MortarCell, Aim = new Cell(80, 80) } };

        var (memory, stood) = CombatMortarRules.CounterBattery(view, prev, new CombatMemory());

        Assert.DoesNotContain(memory.Roles.Items, r => r.Mortar is not null);
        Assert.Equal([new MortarStand(MortarCell, Near)], stood);
    }
}
