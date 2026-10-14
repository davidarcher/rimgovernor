using RimWorld;
using Verse;

namespace RimGovernor.RealCombat.Native;

/// <summary>
/// An infestation's hive under rock, with a corridor of three stone doors in series running west from it (the door probe's
/// ground, and a place to fight the insects at a choke). The hive spawns no insects of its own: the scenario stages them.
/// </summary>
public sealed class HiveStage(int hiveX, int hiveZ)
{
    /// <summary>The corridor's door cells, innermost first.</summary>
    public IReadOnlyList<(int X, int Z)> Doors => [(hiveX - 4, hiveZ), (hiveX - 5, hiveZ), (hiveX - 6, hiveZ)];

    /// <summary>Builds the hive, the doors and the rock roof over them.</summary>
    public void Raise(Map map)
    {
        var made = ThingMaker.MakeThing(ThingDefOf.Hive);
        made.SetFaction(Faction.OfInsects);
        GenSpawn.Spawn(made, new IntVec3(hiveX, 0, hiveZ), map);
        var spawner = ((ThingWithComps)made).GetComp<CompSpawnerPawn>();
        typeof(CompSpawnerPawn).GetField("canSpawnPawns", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.SetValue(spawner, false);

        var stone = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
        foreach (var (x, z) in Doors)
        {
            var at = new IntVec3(x, 0, z);
            var door = ThingMaker.MakeThing(ThingDefOf.Door, stone);
            door.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(door, at, map);
        }

        for (var x = hiveX - 8; x <= hiveX + 4; x++)
        {
            for (var z = hiveZ - 4; z <= hiveZ + 4; z++)
            {
                var at = new IntVec3(x, 0, z);
                if (at.InBounds(map))
                {
                    map.roofGrid.SetRoof(at, RoofDefOf.RoofRockThick);
                }
            }
        }
    }
}
