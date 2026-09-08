using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable inspection fixtures only; excluded from production builds.
    public sealed class InspectorFixture
    {
        [Tool("test/inspector_fixture", Description = "Populate a disposable inspection test; never part of gameplay.")]
        public async Task<object> Create(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var used = new HashSet<IntVec3>();
                var created = new List<object>();
                foreach (var name in new[] { "Cooler", "Vent", "Battery", "TableButcher" })
                {
                    var definition = DefDatabase<ThingDef>.GetNamed(name);
                    int rotations = name == "Cooler" || name == "Vent" ? 4 : 1;
                    for (int rotation = 0; rotation < rotations; rotation++)
                    {
                        var facing = new Rot4(rotation);
                        var cell = GenRadial.RadialCellsAround(pawn.Position, 35, true).First(c =>
                            GenAdj.OccupiedRect(c, facing, definition.Size).ExpandedBy(2).All(p =>
                                p.InBounds(map) && !p.Fogged(map) && p.Standable(map)
                                && p.GetEdifice(map) == null && !used.Contains(p)));
                        foreach (var occupied in GenAdj.OccupiedRect(cell, facing, definition.Size).ExpandedBy(2))
                            used.Add(occupied);
                        var thing = ThingMaker.MakeThing(definition, definition.MadeFromStuff ? ThingDefOf.WoodLog : null);
                        thing.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(thing, cell, map, facing);
                        if (thing is IBillGiver giver)
                            giver.BillStack.AddBill(definition.AllRecipes.First().MakeNewBill());
                        created.Add(new { thingId = thing.ThingID, defName = name, rotation,
                            position = BridgeCommon.Pos(cell) });
                    }
                }
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                var origin = GenRadial.RadialCellsAround(pawn.Position, 35, true).First(c =>
                    new CellRect(c.x, c.z, 3, 3).All(p => p.InBounds(map) && !p.Fogged(map)
                        && p.Standable(map) && p.GetEdifice(map) == null && !used.Contains(p)
                        && map.zoneManager.ZoneAt(p) == null));
                foreach (var cell in new CellRect(origin.x, origin.z, 3, 3)) zone.AddCell(cell);
                // Deliberately inconsistent test data: the new list claims a cell
                // whose grid owner remains the old zone. Production never does this.
                var conflicting = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(conflicting);
                // Change only the private fixture list to avoid the native
                // AddCell overwrite warning pausing the protocol before inspection.
                var cellsField = typeof(Zone).GetField("cells",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                ((List<IntVec3>)cellsField.GetValue(conflicting)).Add(origin);
                var probeDef = DefDatabase<ThingDef>.GetNamed("Cooler");
                var probe = ThingMaker.MakeThing(probeDef);
                probe.Position = IntVec3.Zero;
                probe.Rotation = Rot4.North;
                return (object)new { success = true, buildings = created, zoneId = zone.ID,
                    conflictingZoneId = conflicting.ID, conflictCell = BridgeCommon.Pos(origin),
                    boundaryProbe = ThermalSides.Read(map, probe, probeDef) };
            }, cancellationToken);
        }
    }
}
