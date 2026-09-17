using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Picks an existing colonist, builds
    // and claims one bed as their known "previous bed" (the deterministic
    // precondition AssignBed guards against), and builds a second, separate,
    // unclaimed compliant bed nearby, so bedassignaccept can exercise
    // NativeBedAssignOperations' CompAssignableToPawn.TryAssignPawn dispatch
    // without depending on native random colony bed layout or ownership
    // (a fresh/tribal start colonist need not already own a bed), mirroring
    // HusbandryFixture's own claim-a-bed-directly pattern and
    // RecoveryAreaFixture's disposable-enclosure pattern.
    public sealed class BedAssignFixture
    {
        [Tool("test/bed_assign_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: pick an existing colonist, build and claim one bed as their known previous bed, clear their area restriction, and build one small roofed enclosure nearby containing a single unclaimed compliant target bed.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted
                    && !p.InMentalState && p.ownership != null
                    && p.CurJob?.playerForced != true && !p.health.HasHediffsNeedingTend())
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("No existing colonist eligible for a bed assignment.");
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;

                var bedDef = ThingDefOf.Bed;
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                if (wallDef == null || !wallDef.MadeFromStuff || !GenStuff.AllowedStuffsFor(wallDef).Contains(ThingDefOf.WoodLog))
                    return Refuse("Wall def unavailable or WoodLog is not an allowed stuff in this ruleset.");
                if (doorDef == null) return Refuse("Door def unavailable in this ruleset.");
                var origin = GenRadial.RadialCellsAround(pawn.Position, 40, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, 6, 4).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.Standable(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                    && pawn.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable area for the fixture bedroom.");

                // One open 6x4 roofed room (no interior dividing wall, so the
                // target bed at x=3 is reachable from the claimed previous
                // bed at x=1) with a single exterior door so the fixture
                // colonist can path in from outside.
                for (var x = 0; x < 6; x++) for (var z = 0; z < 4; z++)
                {
                    var c = new IntVec3(origin.x + x, 0, origin.z + z);
                    if (x == 0 && z == 1)
                    {
                        var door = (Building)ThingMaker.MakeThing(doorDef, ThingDefOf.WoodLog);
                        door.SetFaction(player);
                        GenSpawn.Spawn(door, c, map);
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    }
                    else if (x == 0 || x == 5 || z == 0 || z == 3)
                    {
                        var wall = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                        wall.SetFaction(player);
                        GenSpawn.Spawn(wall, c, map);
                    }
                    else map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }

                var previousBed = (Building_Bed)ThingMaker.MakeThing(bedDef, ThingDefOf.WoodLog);
                previousBed.SetFaction(player);
                GenSpawn.Spawn(previousBed, new IntVec3(origin.x + 1, 0, origin.z + 1), map);
                previousBed.SetForbidden(false, false);
                if (!pawn.ownership.ClaimBedIfNonMedical(previousBed) || pawn.ownership.OwnedBed != previousBed)
                    return Refuse("Fixture colonist could not claim the fixture previous bed.");

                var newBed = (Building_Bed)ThingMaker.MakeThing(bedDef, ThingDefOf.WoodLog);
                newBed.SetFaction(player);
                GenSpawn.Spawn(newBed, new IntVec3(origin.x + 3, 0, origin.z + 1), map);
                newBed.SetForbidden(false, false);
                if (newBed.Medical || newBed.ForPrisoners) return Refuse("Fixture bed unexpectedly medical or prisoner-only.");
                if (newBed.OwnersForReading.Any()) return Refuse("Fixture bed unexpectedly already owned.");

                // The walls above were spawned directly, so the enclosure's
                // room does not exist until regions are rebuilt; do that now
                // (the game is paused) and pin the room's temperature to the
                // middle of the colonist's comfy band. AssignBed refuses beds
                // outside that band (and CanReach at Danger.None refuses
                // extreme cells), and a fresh debug world's outdoor
                // temperature is not guaranteed to be comfortable (#96).
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var room = newBed.GetRoom();
                if (room == null || room.TouchesMapEdge || room.OpenRoofCount > 0)
                    return Refuse("Fixture bedroom is not an enclosed roofed room.");
                var comfyMin = pawn.GetStatValue(StatDefOf.ComfyTemperatureMin);
                var comfyMax = pawn.GetStatValue(StatDefOf.ComfyTemperatureMax);
                room.Temperature = (comfyMin + comfyMax) / 2f;
                var ambient = newBed.AmbientTemperature;
                if (ambient < comfyMin || ambient > comfyMax)
                    return Refuse($"Fixture bed ambient temperature {ambient:F1} is outside the colonist's comfy band [{comfyMin:F1}, {comfyMax:F1}].");
                if (!pawn.CanReach(newBed, Verse.AI.PathEndMode.OnCell, Danger.None))
                    return Refuse("Fixture colonist cannot reach the target bed at Danger.None.");

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, pawn = pawn.GetUniqueLoadID(), bedTemperature = ambient, comfyMin, comfyMax,
                    previousBed = previousBed.GetUniqueLoadID(), bed = newBed.GetUniqueLoadID(),
                    setup = "Test-only roofed two-room enclosure: one bed claimed as the fixture colonist's previous bed, one unclaimed compliant target bed, and cleared area restriction; native CompAssignableToPawn.TryAssignPawn dispatch remains native.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
