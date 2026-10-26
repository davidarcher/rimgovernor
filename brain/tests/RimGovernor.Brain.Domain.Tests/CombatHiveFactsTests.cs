using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatHiveFactsTests
{
    [Theory]
    [InlineData("InstalledHive", true)]
    [InlineData("Hive", false)]
    public void MeleeAndRangedTargetRulesUseTheNativeHiveFact(string definition, bool hive)
    {
        var row = EmergencyFixtures.ThreatBuilding("structure") with { Hive = hive };
        row = row with { Building = row.Building with { DefName = definition } };
        var structure = new HostileStructure { Observed = row };
        Assert.Same(row, structure.Observed);
        Assert.Equal(new PawnId(row.Building.Id), structure.Id);
        Assert.Equal(row.Occupied.Minimum, structure.Cell);
        var view = new CombatView { Structures = [structure], SiteExit = null, Layout = null, Pods = null, DoorStates = [], Population = 2 };
        var memory = new CombatMemory { Roles = [new() { Pawn = new("melee"), Target = structure.Id }, new() { Pawn = new("gunner"), Target = structure.Id, Ranged = true }] };
        var updated = CombatMortarRules.FromRange(view, memory);
        Assert.Equal(hive ? structure.Id : default, updated.Roles[0].Target);
        Assert.Equal(structure.Id, updated.Roles[1].Target);
        var aim = CombatMortarRules.MortarAim(view, new CombatMortar { Cell = new(0, 0), MaxRange = 100 });
        Assert.Equal(!hive, aim.Ok);
        if (!hive) Assert.Equal(structure.Cell, aim.Aim);
    }
}
