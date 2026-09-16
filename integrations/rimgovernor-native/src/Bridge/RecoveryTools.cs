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
    public sealed class HomeRecoveryTools
    {
        [Tool("home/recovery_state", Description = "Observe native building damage, breakdowns, fuel and actual electrical service. Missing buildings do not prove recovery.")]
        public async Task<object> State(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() => Census(), cancellationToken);

        internal static object Census()
        {
            RecoveryAreaOwnership.Ensure();
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, error = "No map" };
            var conditions = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
            return new { success = true, tick = Find.TickManager.TicksGame, mapId = map.uniqueID,
                roofHazard = conditions.Any(c => c is GameCondition_ToxicFallout),
                areas = map.areaManager.AllAreas.OfType<Area_Allowed>().Where(a => a.TrueCount > 0
                    && a.ActiveCells.All(c => c.Roofed(map) && !c.Fogged(map))).Select(a => new { id = a.ID, label = a.Label, cells = a.TrueCount }).ToList(),
                restrictions = map.mapPawns.FreeColonistsSpawned.Select(p => new { pawn = p.GetUniqueLoadID(),
                    area = p.playerSettings?.AreaRestrictionInPawnCurrentMap?.ID,
                    leased = Current.Game.GetComponent<RecoveryAreas>().Claims.Any(c => c.Pawn == p && c.Assigned?.Map == map) }).ToList(),
                buildings = map.listerBuildings.allBuildingsColonist.Where(b => !b.Position.Fogged(map))
                    .OrderBy(b => b.thingIDNumber).Select(b => new {
                        thingId = b.GetUniqueLoadID(), defName = b.def.defName, position = BridgeCommon.Pos(b.Position),
                        hitPoints = b.HitPoints, maxHitPoints = b.MaxHitPoints, usesHitPoints = b.def.useHitPoints,
                        broken = b.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false,
                        fuel = b.TryGetComp<CompRefuelable>()?.Fuel,
                        fuelTarget = b.TryGetComp<CompRefuelable>()?.TargetFuelLevel,
                        fuelDefs = b.TryGetComp<CompRefuelable>()?.Props.fuelFilter.AllowedThingDefs.Select(d => d.defName).ToList(),
                        powerOn = b.TryGetComp<CompPowerTrader>() == null ? (bool?)null : b.TryGetComp<CompPowerTrader>().PowerOn,
                        powerConsumer = b.TryGetComp<CompPowerTrader>()?.Props.PowerConsumption > 0,
                        switchedOn = b.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true,
                        forbidden = b.IsForbidden(Faction.OfPlayer), burning = b.IsBurning()
                    }).ToList() };
        }

        [Tool("home/recovery_area", Description = "Lease an existing wholly roofed allowed area for one colonist during native toxic fallout. Intersects player restrictions by refusing wider areas. Expires after 600 ticks or load; never edits areas or replaces a player override. This is a work-area restriction, not protection from every exposure source.")]
        public async Task<object> Area(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string pawn, int areaId, bool dryRun = true)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                RecoveryAreaOwnership.Ensure();
                object Refuse(string reason) => new { success = dryRun, accepted = false, reason, error = dryRun ? null : reason };
                var map = Find.CurrentMap;
                var person = map?.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawn);
                var area = map?.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(a => a.ID == areaId);
                var conditions = new List<GameCondition>();
                map?.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
                if (person?.playerSettings == null || area == null || !conditions.Any(c => c is GameCondition_ToxicFallout))
                    return Refuse("Exact colonist, existing area and native roof-sensitive hazard required");
                if (person.Drafted || person.Downed || person.InMentalState || person.CurJob?.playerForced == true)
                    return Refuse("Pawn unavailable or controlled by player");
                var prior = person.playerSettings.AreaRestrictionInPawnCurrentMap;
                if (area.TrueCount == 0 || area.ActiveCells.Any(c => !c.Roofed(map) || c.Fogged(map)
                    || c.GetDangerFor(person, map) != Danger.None || prior != null && !prior[c]))
                    return Refuse("Area has exposed or unsafe cells or would widen player restrictions");
                if (!area.ActiveCells.Any(c => person.CanReach(c, PathEndMode.OnCell, Danger.None)))
                    return Refuse("Roofed refuge inaccessible");
                var leases = Current.Game.GetComponent<RecoveryAreas>();
                var claim = leases.Claims.FirstOrDefault(c => c.Pawn == person && c.Assigned?.Map == map);
                if (claim != null && prior != claim.Assigned) return Refuse("Player replaced recovery restriction");
                if (!dryRun && prior != area)
                {
                    person.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                    leases.Claims.Add(new RecoveryAreaClaim { Pawn = person, Before = prior, Assigned = area,
                        Owner = Current.Game.GetComponent<ColonyIdentity>().LoadToken, Until = Find.TickManager.TicksGame + 600 });
                }
                else if (!dryRun && claim != null) claim.Until = Find.TickManager.TicksGame + 600;
                return new { success = true, accepted = true, dryRun, pawn, areaId, until = Find.TickManager.TicksGame + 600 };
            }, cancellationToken);

        [Tool("home/recover_service", Description = "Preview or order one exact repair, breakdown repair or refuel through the native WorkGiver. Honors work settings, allowed areas, forbidden supplies, player jobs and reservations. A receipt is not completed labor.")]
        public async Task<object> Recover(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string thingId, string pawn, string method, bool dryRun = true)
            => await ctx.MainThread.InvokeAsync(() => Order(thingId, pawn, method, dryRun), cancellationToken);

        private static object Order(string thingId, string pawnId, string method, bool dryRun)
        {
            object Refuse(string reason) => new { success = dryRun, accepted = false, reason, error = dryRun ? null : reason };
            var map = Find.CurrentMap;
            var target = map?.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.GetUniqueLoadID() == thingId);
            var pawn = map?.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
            if (target == null || pawn == null || target.Position.Fogged(map)) return Refuse("Exact visible current-map identities required");
            if (target.IsForbidden(Faction.OfPlayer) || target.IsBurning() || pawn.Dead || pawn.Downed
                || pawn.Drafted || pawn.InMentalState || pawn.CurJob?.playerForced == true)
                return Refuse("Unsafe target or pawn controlled by player");
            var area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
            if ((area != null && !area[target.Position]) || !pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None))
                return Refuse("Target outside allowed work area, reserved or inaccessible");
            var expected = method == "repair" ? "WorkGiver_Repair" : method == "breakdown" ? "WorkGiver_FixBrokenDownBuilding"
                : method == "refuel" ? "WorkGiver_Refuel" : null;
            if (expected == null) return Refuse("Unknown recovery method");
            if (method == "repair" && target.HitPoints >= target.MaxHitPoints
                || method == "breakdown" && target.TryGetComp<CompBreakdownable>()?.BrokenDown != true
                || method == "refuel" && (target.TryGetComp<CompRefuelable>() == null
                    || target.TryGetComp<CompRefuelable>().Fuel >= target.TryGetComp<CompRefuelable>().TargetFuelLevel))
                return Refuse("Observed service no longer needs this method");
            foreach (var def in DefDatabase<WorkGiverDef>.AllDefsListForReading.OrderBy(d => d.defName))
            {
                if (def.giverClass.Name != expected || !def.directOrderable || !(def.Worker is WorkGiver_Scanner scanner)
                    || pawn.WorkTypeIsDisabled(def.workType) || pawn.workSettings == null
                    || !pawn.workSettings.WorkIsActive(def.workType)) continue;
                bool claims = scanner.PotentialWorkThingRequest.Accepts(target)
                    || (scanner.PotentialWorkThingsGlobal(pawn)?.Contains(target) ?? false);
                if (!claims || scanner.ShouldSkip(pawn, true) || !scanner.HasJobOnThing(pawn, target, true)) continue;
                var job = scanner.JobOnThing(pawn, target, true);
                if (job == null || job.targetA.Thing != target) continue;
                if (job.targetB.HasThing && (job.targetB.Thing.IsForbidden(pawn)
                    || area != null && !area[job.targetB.Cell]
                    || !pawn.CanReserveAndReach(job.targetB, PathEndMode.Touch, Danger.None))) continue;
                job.workGiverDef = def;
                if (!dryRun)
                {
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    if (pawn.CurJob != job) return Refuse("Native job unverified; observe before replacement");
                }
                return new { success = true, accepted = true, dryRun, thingId, pawn = pawnId, method,
                    job = job.def.defName, hitPoints = target.HitPoints, fuel = target.TryGetComp<CompRefuelable>()?.Fuel,
                    supply = job.targetB.Thing?.GetUniqueLoadID(), count = job.count,
                    meaning = "Order only; observe exact target service after native labor" };
            }
            return Refuse("No native recovery job with current supplies, work permissions and access");
        }
    }
}
