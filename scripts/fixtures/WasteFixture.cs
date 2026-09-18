using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable setup only; never exposed through the controller gameplay surface.
    public sealed class WasteFixture
    {
        [Tool("test/waste_fixture", Description = "Prepare disposable waste hauling acceptance inputs.")]
        public async Task<object> Create(IRimBridgeContext ctx, CancellationToken cancellationToken, bool burial = false)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                // A generous pool of candidate cells, not one fixed cell per
                // item: ThingPlaceMode.Direct placement of a fresh item was
                // observed live to fail outright (TryPlaceThing returns
                // false, the thing left unspawned) on some individual cells
                // for reasons this fixture does not need to fully diagnose
                // (e.g. terrain/roof affordances GetEdifice/Standable do not
                // capture). Each item independently walks the same candidate
                // pool until placement actually succeeds, rather than
                // committing to a single guessed cell.
                //
                // Candidate cells hold no item already: Direct placement
                // absorbs a fixture stack into an existing stack of the same
                // def at the cell (the debug start's own starting Steel, #185),
                // leaving the fixture's Thing destroyed and its Position
                // off-map. The pool is filtered up front and the placement is
                // checked afterwards so the returned item is the one spawned.
                var candidates = GenRadial.RadialCellsAround(pawn.Position, 10, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).Distinct().ToList();
                if (candidates.Count < 8) throw new System.InvalidOperationException("Not enough empty standable cells near the fixture pawn; reroll the world.");
                Thing PlaceSomewhere(Thing thing)
                {
                    foreach (var cell in candidates)
                    {
                        if (cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)) continue;
                        if (!GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Direct, out var placed)) continue;
                        if (placed != thing || !thing.Spawned || thing.Position != cell)
                            throw new System.InvalidOperationException($"Fixture {thing.def.defName} was absorbed or displaced at {cell} instead of spawning there.");
                        return thing;
                    }
                    throw new System.InvalidOperationException($"Could not place a fixture {thing.def.defName} on any of {candidates.Count} candidate cells.");
                }
                var source = candidates[0];
                var destination = GenRadial.RadialCellsAround(source, 35, true).First(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetZone(map) == null
                    && !map.areaManager.Home[c] && c.DistanceToSquared(source) > 225
                    && c.GetRoom(map)?.UsesOutdoorTemperature == true
                    && !map.listerBuildings.allBuildingsColonist.Any(b => b.Position.DistanceToSquared(c) < 196));
                var kind = burial ? PawnKindDefOf.Colonist : DefDatabase<PawnKindDef>.AllDefsListForReading.First(k => k.race.defName == "Squirrel");
                var animal = PawnGenerator.GeneratePawn(kind, burial ? Faction.OfPlayer : null);
                GenSpawn.Spawn(animal, source, map);
                animal.Kill(null);
                var corpse = animal.Corpse;
                // Pawn.Kill's own corpse placement can silently nudge the
                // corpse to a neighboring cell instead of exactly "source"
                // (observed live); re-place it exactly through the same pool.
                if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
                PlaceSomewhere(corpse);
                corpse.TryGetComp<CompRottable>().RotProgress = 200000f;
                corpse.SetForbidden(false, false);
                var unwanted = (Thing)PlaceSomewhere(ThingMaker.MakeThing(ThingDefOf.WoodLog));
                unwanted.SetForbidden(false, false);
                var protectedItem = (Thing)PlaceSomewhere(ThingMaker.MakeThing(ThingDefOf.Steel));
                protectedItem.SetForbidden(true, false);
                var unwantedCell = unwanted.Position;
                var protectedCell = protectedItem.Position;
                if (burial)
                {
                    var grave = ThingMaker.MakeThing(ThingDefOf.Grave);
                    grave.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(grave, destination, map);
                    ((Building_Grave)grave).GetStoreSettings().filter.SetAllow(corpse.def, true);
                }
                else
                {
                    var zone = new Zone_Stockpile(StorageSettingsPreset.DumpingStockpile, map.zoneManager);
                    map.zoneManager.RegisterZone(zone);
                    zone.AddCell(destination);
                    zone.AddCell(destination + IntVec3.East);
                    zone.GetStoreSettings().filter.SetDisallowAll();
                    zone.GetStoreSettings().filter.SetAllow(corpse.def, true);
                    zone.GetStoreSettings().filter.SetAllow(unwanted.def, true);
                    foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable))
                        zone.GetStoreSettings().filter.SetAllow(special, true);
                    zone.GetStoreSettings().Priority = StoragePriority.Critical;
                    // Native stockpile creation may expand Home; this fixture
                    // deliberately models player-selected dirty storage outside it.
                    foreach (var cell in zone.Cells) map.areaManager.Home[cell] = false;
                }
                // Disable Hauling for every colonist (including the returned
                // pawn), not enable it: the native ManageWaste dispatch under
                // test issues its own forced WorkGiver_Scanner job directly
                // (TryTakeOrderedJob), bypassing work settings entirely, so
                // no colonist's own priority needs to be enabled for it. Left
                // enabled, a live run observed the normal think-tree grab
                // this exact unforbidden corpse/WoodLog into the dumping
                // stockpile within the same handful of ticks fixture setup
                // itself consumes, before the acceptance tool ever issues its
                // own dispatch -- disabling it here removes that race.
                foreach (var colonist in map.mapPawns.FreeColonistsSpawned)
                    colonist.workSettings?.SetPriority(WorkTypeDefOf.Hauling, 0);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    pawn = pawn.GetUniqueLoadID(), corpse = corpse.GetUniqueLoadID(),
                    unwanted = unwanted.GetUniqueLoadID(), protectedItem = protectedItem.GetUniqueLoadID(),
                    source = BridgeCommon.Pos(source), destination = BridgeCommon.Pos(destination),
                    // The corpse, the unwanted WoodLog and the forbidden
                    // Steel can each land on a different candidate cell (see
                    // PlaceSomewhere above); report the WoodLog's and Steel's
                    // actual resulting cells so the acceptance tool scans
                    // exactly where they really are, not a guessed "source".
                    unwantedCell = BridgeCommon.Pos(unwantedCell), protectedCell = BridgeCommon.Pos(protectedCell) };
            }, cancellationToken);
    }
}
