using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Building = RimGovernor.Brain.Domain.Building;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.RealCombat.Native;

/// <summary>
/// An infestation's hive under rock, with the burn-out room the colony's own policy plans around it already built: the
/// stone seal and corridor doors and the wood stools come from <see cref="PolicyFunctions.BurnBuilds"/> over an empty
/// census of the site, the way the defense round would ask them of the builders, and are placed at once. The room, its
/// corridor and the fuel are then roofed in thick rock, as a hive under a mountain is. The fire, the retreat and the
/// finishing are the policy's to play.
/// </summary>
public sealed class BurnStage(Cell hive, Cell exit)
{
    /// <summary>The cell the hive stands on.</summary>
    public Cell Hive => hive;

    /// <summary>What the policy asked for and the stage placed.</summary>
    public IReadOnlyList<Building> Placed { get; private set; } = [];

    /// <summary>Builds the hive, the seal, the corridor, the fuel and the roof.</summary>
    public void Raise(Map map)
    {
        var plan = new CombatBurn { Hive = hive, Exit = exit };
        var (lo, hi) = PolicyFunctions.BurnRegion(hive);
        var census = new Dictionary<Cell, WaitDoorCell>();
        for (var x = lo.X; x <= hi.X; x++)
        {
            for (var z = lo.Z; z <= hi.Z; z++)
            {
                census[new Cell(x, z)] = new WaitDoorCell { Walkable = true, Roofed = true };
            }
        }

        Placed = PolicyFunctions.BurnBuilds(plan, census, "BlocksGranite", null!);
        foreach (var b in Placed)
        {
            DefenseStage.Place(map, b);
        }

        var hiveDef = ThingDefOf.Hive;
        var made = ThingMaker.MakeThing(hiveDef);
        made.SetFaction(Faction.OfInsects);
        GenSpawn.Spawn(made, new IntVec3(hive.X, 0, hive.Z), map);

        // Only the staged insects are in the room: the hive's own, spawned on the corridor axis, would see the carrier.
        var spawner = ((ThingWithComps)made).GetComp<CompSpawnerPawn>();
        typeof(CompSpawnerPawn).GetField("canSpawnPawns", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.SetValue(spawner, false);

        // The seal, its corridor and the flanks beside the doors lie under rock; the retreat past them is open sky.
        var roof = RoofDefOf.RoofRockThick;
        var roofed = new List<Cell>();
        for (var x = hive.X - PolicyFunctions.BurnFuelRadius - 1; x <= hive.X + PolicyFunctions.BurnFuelRadius + 1; x++)
        {
            for (var z = hive.Z - PolicyFunctions.BurnFuelRadius - 1; z <= hive.Z + PolicyFunctions.BurnFuelRadius + 1; z++)
            {
                roofed.Add(new Cell(x, z));
            }
        }

        for (var k = PolicyFunctions.BurnFuelRadius + 2; k < PolicyFunctions.BurnFuelRadius + 1 + 3; k++)
        {
            for (var side = -1; side <= 1; side++)
            {
                roofed.Add(new Cell(hive.X + (exit.X * k) + (exit.Z * side), hive.Z + (exit.Z * k) + (exit.X * side)));
            }
        }

        foreach (var c in roofed)
        {
            var at = new IntVec3(c.X, 0, c.Z);
            if (at.InBounds(map))
            {
                map.roofGrid.SetRoof(at, roof);
            }
        }
    }
}
