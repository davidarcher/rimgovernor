using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Selects crewCount existing free
    // colonists to depart and leaves the rest home (at least one of them
    // Doctor-eligible), tops up home food and WoodLog cargo stock so the
    // native FormCaravan admission checks (home staffing, home food, cargo
    // availability) are genuinely satisfied rather than incidentally true of
    // whatever the debug save happens to start with, and picks an existing
    // non-hostile visitable settlement as a real reachable destination tile.
    // A freshly generated debug map starts with an empty Home Area, and
    // native's CaravanFormingUtility.AllReachableColonyItems only considers
    // items inside Home Area (matching the vanilla Dialog_FormCaravan UI);
    // an established colony would already have Home Area covering its
    // stockpile, so the fixture marks the dropped supplies' cells Home
    // itself rather than relying on incidental map generation. No quest/
    // trade state is touched; this exercises CaravanDeparture in isolation.
    public sealed class CaravanDepartureFixture
    {
        [Tool("test/caravan_departure_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: select crewCount existing free colonists to depart, leave the rest home (with at least one Doctor-eligible), top up home food and WoodLog stock within Home Area, and pick an existing non-hostile visitable settlement as destination.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int crewCount = 1)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (crewCount < 1 || crewCount > 4) return Refuse("Use 1..4 departing colonists.");

                var colonists = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count < crewCount + 2)
                    return Refuse("At least crewCount+2 eligible colonists (crew, home, home doctor) are required.");

                var crew = colonists.Take(crewCount).ToList();
                var remaining = colonists.Skip(crewCount).ToList();
                var doctor = remaining.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                if (doctor == null) return Refuse("No eligible home doctor among remaining colonists.");
                if (doctor.workSettings != null && doctor.workSettings.GetPriority(WorkTypeDefOf.Doctor) == 0)
                    doctor.workSettings.SetPriority(WorkTypeDefOf.Doctor, 3);

                var center = colonists[0].Position;
                var mealCell = center + new IntVec3(3, 0, 0);
                var woodCell = center + new IntVec3(-3, 0, 0);
                // Ensure the drop cells (and a small margin, since GenPlace.Near may
                // shift the exact spawn cell) are Home Area, or native's item
                // reachability scan will silently see zero cargo.
                foreach (var cell in GenRadial.RadialCellsAround(center, 5, true))
                    if (cell.InBounds(map)) map.areaManager.Home[cell] = true;

                var meal = ThingMaker.MakeThing(ThingDef.Named("MealSimple"));
                meal.stackCount = meal.def.stackLimit;
                GenPlace.TryPlaceThing(meal, mealCell, map, ThingPlaceMode.Near);
                meal.SetForbidden(false, false);
                var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = 75;
                GenPlace.TryPlaceThing(wood, woodCell, map, ThingPlaceMode.Near);
                wood.SetForbidden(false, false);
                // The colony's food reserve (#428): forbidden pemmican the
                // departure adapter packs first (#464), beside unforbidden
                // survival meals it packs next and the simple meals above it
                // must leave home. Two survival stacks keep the home runway
                // over the routine floor after the pack leaves.
                var pemmican = ThingMaker.MakeThing(ThingDef.Named("Pemmican"));
                pemmican.stackCount = 20;
                GenPlace.TryPlaceThing(pemmican, center + new IntVec3(0, 0, 3), map, ThingPlaceMode.Near);
                pemmican.SetForbidden(true, false);
                for (var i = 0; i < 2; i++)
                {
                    var survival = ThingMaker.MakeThing(ThingDef.Named("MealSurvivalPack"));
                    survival.stackCount = 30;
                    GenPlace.TryPlaceThing(survival, center + new IntVec3(i * 2 - 1, 0, -3), map, ThingPlaceMode.Near);
                    survival.SetForbidden(false, false);
                }
                // A crew member carrying a packed meal would show in the
                // caravan's inventory beside the composed pack.
                foreach (var pawn in crew) pawn.inventory?.innerContainer.ClearAndDestroyContents();

                // A candidate must have an actual native path from the home
                // tile, not merely exist and be non-hostile: on a random world
                // seed the nearest/first-by-id candidate can be unreachable
                // (across water, blocked terrain), which the real
                // CaravanCatalog observation would then correctly refuse at
                // catalog time -- mirrors NativeCaravanCatalog.RouteFacts'
                // reachability check so this fixture cannot hand out a
                // destination the vertical itself would reject as unreachable.
                Settlement settlement = null;
                foreach (var candidate in Find.WorldObjects.SettlementBases
                    .Where(s => s.Faction != null && s.Faction != player && !s.Faction.HostileTo(player) && s.Visitable && s.Tile != map.Tile)
                    .OrderBy(s => Find.WorldGrid.ApproxDistanceInTiles(map.Tile, s.Tile)).ThenBy(s => s.GetUniqueLoadID()))
                {
                    using var path = map.Tile.Layer.Pather.FindPath(map.Tile, candidate.Tile, null);
                    if (path.Found) { settlement = candidate; break; }
                }
                if (settlement == null) return Refuse("No existing non-hostile visitable and reachable settlement destination.");

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    crewPawnIds = crew.Select(p => p.GetUniqueLoadID()).ToArray(),
                    remainingPawnIds = remaining.Select(p => p.GetUniqueLoadID()).ToArray(),
                    doctorPawnId = doctor.GetUniqueLoadID(),
                    destinationTile = settlement.Tile.tileId,
                    homeTile = map.Tile.tileId,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
