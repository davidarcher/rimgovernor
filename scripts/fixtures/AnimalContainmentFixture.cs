using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class AnimalContainmentFixture
    {
        [Tool("test/containment_setup", Description = "Prepare a disposable loose pen animal and pet. No pen or handling settings are created.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var center = map.mapPawns.FreeColonistsSpawned.First().Position;
                var cell = GenRadial.RadialCellsAround(center, 8, true).First(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map));
                var animal = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Muffalo"), Faction.OfPlayerSilentFail);
                var pet = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Husky"), Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(animal, cell, map);
                GenSpawn.Spawn(pet, cell, map);
                animal.needs.food.CurLevelPercentage = .95f;
                pet.needs.food.CurLevelPercentage = .95f;
                return new { success = true, animal = animal.GetUniqueLoadID(), pet = pet.GetUniqueLoadID() };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Private disposable acceptance only. Mirrors StoreroomFixture's clear-and-supply pattern
        // (spawn just enough WoodLog for this exact shell rather than depend on the debug start's
        // own stock) so the pen-shell room is deterministic regardless of starting terrain.
        [Tool("test/containment_construct_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: clear one open 8x8 ground area, spawn just enough WoodLog for a full 6x6 pen shell (19 Fence + 1 FenceGate), spawn one uncontained pen-requiring herd animal and a non-pen pet inside it, set one capable colonist's normal Construct/Handling priorities and Work timetable, and return the legal 6x6 pen-shell rectangle (Fence perimeter, FenceGate at the south wall's center). No placement or game tick changes.")]
        public async Task<object> ConstructPrepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var pawn = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && p.Faction == player && !p.Dead && !p.Downed
                    && !p.Drafted && !p.InMentalState && p.workSettings != null && p.timetable != null && p.skills != null
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Handling)
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("No existing colonist capable of normal construction/handling.");
                var fenceDef = DefDatabase<ThingDef>.GetNamedSilentFail("Fence");
                var gateDef = DefDatabase<ThingDef>.GetNamedSilentFail("FenceGate");
                var markerDef = DefDatabase<ThingDef>.GetNamedSilentFail("PenMarker");
                if (fenceDef == null || gateDef == null || markerDef == null) return Refuse("Fence/FenceGate/PenMarker defs unavailable.");
                if (!fenceDef.MadeFromStuff || !gateDef.MadeFromStuff || !markerDef.MadeFromStuff
                    || !GenStuff.AllowedStuffsFor(fenceDef).Contains(ThingDefOf.WoodLog)
                    || !GenStuff.AllowedStuffsFor(gateDef).Contains(ThingDefOf.WoodLog)
                    || !GenStuff.AllowedStuffsFor(markerDef).Contains(ThingDefOf.WoodLog))
                    return Refuse("WoodLog is not an allowed stuff for Fence/FenceGate/PenMarker in this ruleset.");
                var fenceCost = fenceDef.CostListAdjusted(ThingDefOf.WoodLog, false).Where(c => c.thingDef == ThingDefOf.WoodLog).Sum(c => c.count);
                var gateCost = gateDef.CostListAdjusted(ThingDefOf.WoodLog, false).Where(c => c.thingDef == ThingDefOf.WoodLog).Sum(c => c.count);
                var markerCost = markerDef.CostListAdjusted(ThingDefOf.WoodLog, false).Where(c => c.thingDef == ThingDefOf.WoodLog).Sum(c => c.count);
                if (fenceCost <= 0) return Refuse("Fence has no WoodLog cost in this ruleset.");
                if (markerCost <= 0) return Refuse("PenMarker has no WoodLog cost in this ruleset.");

                const int size = 6;
                IntVec3 room = IntVec3.Invalid;
                foreach (var anchor in GenRadial.RadialCellsAround(pawn.Position, 20, true).Take(1024)) {
                    var origin = anchor; var ok = true;
                    for (var dx = -1; dx <= size && ok; dx++) {
                        for (var dz = -1; dz <= size && ok; dz++) {
                            var cell = new IntVec3(origin.x + dx, 0, origin.z + dz);
                            if (!cell.InBounds(map) || cell.Fogged(map) || !cell.Standable(map) || cell.Roofed(map)
                                || map.zoneManager.ZoneAt(cell) != null
                                || !cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)
                                || !pawn.CanReach(cell, PathEndMode.Touch, Danger.None)) { ok = false; break; }
                        }
                    }
                    if (ok) { room = origin; break; }
                }
                if (room == IntVec3.Invalid) return Refuse("Bounded nearby search found no legal 6x6 pen shell room.");

                // Clear any plant/item obstruction inside the room's own footprint (not its margin) so
                // every perimeter and interior cell is genuinely free, mirroring StoreroomFixture.Setup.
                foreach (var cell in CellRect.FromLimits(room, room + new IntVec3(size - 1, 0, size - 1)))
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();

                // 19 Fence + 1 FenceGate for the shell, plus the interior PenMarker
                // (30 WoodLog by its own costStuffCount) -- omitting the marker's own
                // cost here previously left the fixture's colonist stranded mid-build
                // with no material to finish the marker blueprint.
                var totalWoodNeeded = fenceCost * 19 + gateCost + markerCost;
                var stackLimit = ThingDefOf.WoodLog.stackLimit;
                var stacks = (totalWoodNeeded + stackLimit - 1) / stackLimit;
                var woodIDs = new List<string>();
                var woodTotal = 0;
                for (var i = 0; i < stacks; i++) {
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    var count = Math.Min(stackLimit, totalWoodNeeded - woodTotal);
                    wood.stackCount = count;
                    if (!GenPlace.TryPlaceThing(wood, room + new IntVec3(size, 0, i), map, ThingPlaceMode.Near))
                        return Refuse("Could not place fixture WoodLog near the pen shell room.");
                    wood.SetForbidden(false, false);
                    woodIDs.Add(wood.GetUniqueLoadID());
                    woodTotal += count;
                }
                if (woodTotal < totalWoodNeeded) return Refuse("Fixture could not place enough WoodLog for a full 6x6 pen shell.");

                var animal = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Muffalo"), player);
                var pet = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Husky"), player);
                var interior = room + new IntVec3(size / 2, 0, size / 2);
                GenSpawn.Spawn(animal, interior, map);
                GenSpawn.Spawn(pet, interior, map);
                animal.needs.food.CurLevelPercentage = .95f;
                pet.needs.food.CurLevelPercentage = .95f;

                pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                pawn.workSettings.SetPriority(WorkTypeDefOf.Handling, 1);
                for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, pawnId = pawn.GetUniqueLoadID(),
                    animal = animal.GetUniqueLoadID(), pet = pet.GetUniqueLoadID(),
                    pawnCell = new { x = pawn.Position.x, z = pawn.Position.z },
                    room = new { x = room.x, z = room.z, width = size, height = size },
                    constructPriority = pawn.workSettings.GetPriority(WorkTypeDefOf.Construction),
                    handlingPriority = pawn.workSettings.GetPriority(WorkTypeDefOf.Handling),
                    wood = woodIDs.ToArray(), woodCount = woodTotal,
                    fenceCost, gateCost, markerCost,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
