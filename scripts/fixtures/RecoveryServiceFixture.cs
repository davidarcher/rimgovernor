using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Spawns one genuinely damaged player
    // Wall near an existing colonist and enables that colonist's Construction
    // work, so recoveryserviceaccept can exercise NativeRecoveryOperations'
    // RecoverService/Repair dispatch (WorkGiver_Repair, matching the real
    // native RecoveryTools.Order path) without depending on native random
    // colony damage or starting terrain, mirroring
    // BuildingTemperatureFixture's/AnimalContainmentFixture's own minimal
    // disposable-spawn pattern.
    public sealed class RecoveryServiceFixture
    {
        [Tool("test/recovery_service_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: spawn one genuinely damaged player Wall near an existing colonist and enable that colonist's Construction work on the current paused map.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var pawn = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && p.Faction == player && !p.Dead && !p.Downed
                    && !p.Drafted && !p.InMentalState && p.workSettings != null && p.timetable != null
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("No existing colonist capable of normal construction.");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                if (wallDef == null || !wallDef.MadeFromStuff || !GenStuff.AllowedStuffsFor(wallDef).Contains(ThingDefOf.WoodLog))
                    return Refuse("Wall def unavailable or WoodLog is not an allowed stuff in this ruleset.");
                var cell = GenRadial.RadialCellsAround(pawn.Position, 15, true).FirstOrDefault(c =>
                    c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && map.zoneManager.ZoneAt(c) == null && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)
                    && pawn.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (cell == default) return Refuse("No open reachable cell for the fixture wall.");
                var wall = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                wall.SetFaction(player);
                GenSpawn.Spawn(wall, cell, map);
                map.areaManager.Home[cell] = true;
                if (!wall.def.useHitPoints || wall.MaxHitPoints <= 1) return Refuse("Fixture wall unexpectedly lacks usable hit points.");
                var maxHitPoints = wall.MaxHitPoints;
                // Apply real damage through TakeDamage (mirroring DisasterFixture's own
                // pattern) rather than setting HitPoints directly: WorkGiver_Repair's
                // ShouldSkip gate depends on ListerBuildingsRepairable, which is only
                // marked dirty by the normal damage-notification path (Building.PostApplyDamage),
                // not by a bare property assignment. A directly-assigned HitPoints value
                // is genuinely damaged by every observable metric but native's own repair
                // WorkGiver never considers it repairable -- confirmed by a first live run
                // whose preview returned accepted=false, canTry=false, "No native service
                // WorkGiver would produce a job for this pawn and target."
                var targetHitPoints = System.Math.Max(1, maxHitPoints / 2);
                var guard = 0;
                while (wall.Spawned && wall.HitPoints > targetHitPoints && guard++ < 200)
                {
                    var amount = System.Math.Max(1, System.Math.Min(10, wall.HitPoints - targetHitPoints));
                    wall.TakeDamage(new DamageInfo(DamageDefOf.Blunt, amount));
                }
                if (!wall.Spawned) return Refuse("Fixture wall was destroyed while applying damage.");
                var damagedHitPoints = wall.HitPoints;
                if (damagedHitPoints >= maxHitPoints) return Refuse("Fixture wall failed to take any damage.");

                pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, pawn = pawn.GetUniqueLoadID(), wall = wall.GetUniqueLoadID(),
                    maxHitPoints, damagedHitPoints, constructPriority = pawn.workSettings.GetPriority(WorkTypeDefOf.Construction),
                    setup = "Test-only wall spawn/damage and work-priority setup; native repair dispatch and job completion remain native.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
