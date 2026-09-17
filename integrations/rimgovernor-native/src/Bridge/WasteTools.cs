#nullable enable

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
    public sealed class HomeWasteTools
    {
        [Tool("home/waste_state", Description = "Read exact visible waste identities and containment. Rotten anonymous animal corpses and spoiled goods are automatic candidates. Other unwanted items require exact unwanted IDs. Forbidden, quest, named/colony corpses, held possessions and graves are protected. Relocation is not destruction.")]
        public async Task<object> State(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string unwanted = "", string bury = "") => await ctx.MainThread.InvokeAsync(() => Census(unwanted, bury), cancellationToken);

        [Tool("home/manage_waste", Description = "Preview or prioritize one exact waste haul through native hauling WorkGivers. Preserves player orders, filters and zones. Requires an outdoor destination outside the home area, separated from colony buildings, or native burial in an empty grave. Never deletes items. Rechecks eligibility and destination at dispatch.")]
        public async Task<object> Manage(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string thingId, string pawn, string unwanted = "", string bury = "", bool dryRun = true)
            => await ctx.MainThread.InvokeAsync(() => Haul(thingId, pawn, unwanted, bury, dryRun), cancellationToken);

        private static HashSet<string> Ids(string text) => new HashSet<string>((text ?? "").Split(',')
            .Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.Ordinal);
        private static string Id(Thing thing) => thing.GetUniqueLoadID();

        private static string? Protection(Thing thing, bool burialAllowed = false)
        {
            if (!thing.Spawned || thing.Position.Fogged(thing.Map)) return "held_or_unobserved";
            if (thing.IsForbidden(Faction.OfPlayer)) return "player_forbidden";
            if (thing.questTags != null && thing.questTags.Count > 0) return "quest_item";
            if (thing.def.comps != null && thing.def.comps.Any(c => c is CompProperties_Dissolution
                || c is CompProperties_GasOnDamage || c is CompProperties_Explosive)) return "hazardous_item_requires_specialized_containment";
            if (thing is MinifiedThing || thing is Pawn || thing is Building) return "protected_possession";
            var corpse = thing as Corpse;
            if (corpse != null)
            {
                var inner = corpse.InnerPawn;
                if (inner == null) return "corpse_identity_unknown";
                if (!burialAllowed && (inner.Faction == Faction.OfPlayer || inner.Name != null)) return "named_or_colony_corpse";
                if (!burialAllowed && inner.RaceProps.Humanlike) return "human_corpse_requires_funeral_policy";
            }
            return null;
        }

        private static string? Kind(Thing thing, HashSet<string> unwanted)
        {
            var rot = thing.TryGetComp<CompRottable>();
            if (thing is Corpse && rot != null && rot.Stage != RotStage.Fresh) return "corpse";
            if (!(thing is Corpse) && rot != null && rot.Stage != RotStage.Fresh) return "spoiled";
            return unwanted.Contains(Id(thing)) ? "unwanted" : null;
        }

        // A conservative separation contract, not a claim that any outdoor dump is harmless.
        private static bool DirtyCell(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map) || cell.Fogged(map) || cell.Roofed(map)
                || map.areaManager.Home[cell] || cell.GetRoom(map)?.UsesOutdoorTemperature != true) return false;
            return !map.listerBuildings.allBuildingsColonist.Any(b => b.Position.DistanceToSquared(cell) < 144);
        }

        private static bool Stored(Thing thing)
        {
            var zone = thing.Position.GetZone(thing.Map) as Zone_Stockpile;
            return zone != null && zone.GetStoreSettings().AllowedToAccept(thing) && DirtyCell(thing.Map, thing.Position);
        }

        internal static object Census(string unwanted, string bury)
        {
            var map = Find.CurrentMap;
            if (map == null) return BridgeCommon.Failure("home/waste_state", "No loaded map");
            var selected = Ids(unwanted);
            var burials = Ids(bury);
            var rows = new List<object>();
            foreach (var thing in map.listerThings.AllThings.OrderBy(Id))
            {
                if (thing.Position.Fogged(map)) continue;
                var kind = burials.Contains(Id(thing)) && thing is Corpse ? "corpse" : Kind(thing, selected);
                if (kind == null && !(thing is Corpse)) continue;
                var protection = Protection(thing, burials.Contains(Id(thing)));
                var stored = !burials.Contains(Id(thing)) && protection == null && Stored(thing);
                rows.Add(new { thingId = Id(thing), defName = thing.def.defName, count = thing.stackCount,
                    kind, protectedReason = protection, eligible = protection == null && kind != null,
                    position = BridgeCommon.Pos(thing.Position), rotStage = thing.TryGetComp<CompRottable>()?.Stage.ToString(),
                    state = stored ? "relocated" : "exposed", zoneId = (thing.Position.GetZone(map) as Zone_Stockpile)?.ID });
            }
            foreach (var grave in map.listerThings.AllThings.OfType<Building_Grave>().Where(g => !g.Position.Fogged(map)))
                foreach (var body in grave.GetDirectlyHeldThings().OfType<Corpse>())
                    rows.Add(new { thingId = Id(body), defName = body.def.defName, count = 1, kind = "corpse",
                        protectedReason = "grave", eligible = false, state = "buried", graveId = Id(grave) });
            return new { success = true, mapId = map.uniqueID, tick = Find.TickManager.TicksGame, items = rows,
                meaning = "Exposed and relocated are live item states; buried is native containment. Absence does not prove destruction." };
        }

        private static object Haul(string thingId, string pawnId, string unwanted, string bury, bool dryRun)
        {
            var map = Find.CurrentMap;
            var burials = Ids(bury);
            object Refuse(string reason) => new { success = dryRun, accepted = false, reason, error = dryRun ? null : reason, dryRun };
            if (map == null) return Refuse("No loaded map");
            var thing = map.listerThings.AllThings.FirstOrDefault(t => Id(t) == thingId);
            var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => Id(p) == pawnId);
            if (thing == null || pawn == null) return Refuse("Exact current-map thing and colonist IDs required");
            var protection = Protection(thing, burials.Contains(Id(thing)));
            if (protection != null || (Kind(thing, Ids(unwanted)) == null && !(thing is Corpse && burials.Contains(thingId)))) return Refuse(protection ?? "Not eligible waste");
            if (!burials.Contains(thingId) && Stored(thing)) return Refuse("Already relocated; no further haul needed");
            if (pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState || pawn.CurJob?.playerForced == true)
                return Refuse("Pawn unavailable or controlled by player");
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) || pawn.workSettings == null
                || !pawn.workSettings.WorkIsActive(WorkTypeDefOf.Hauling)) return Refuse("Hauling disabled by capability or player schedule");
            if (thing.IsBurning() || !pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.None))
                return Refuse("Unsafe, reserved or inaccessible target");
            foreach (var giver in WorkTypeDefOf.Hauling.workGiversByPriority)
            {
                if (giver == null || !giver.directOrderable || !(giver.Worker is WorkGiver_Scanner scanner)) continue;
                // The float-menu scanner checks that the giver claims this target
                // before calling subtype-specific HasJobOnThing implementations.
                bool claims = scanner.PotentialWorkThingRequest.Accepts(thing)
                    || (scanner.PotentialWorkThingsGlobal(pawn)?.Contains(thing) ?? false);
                if (!claims || scanner.ShouldSkip(pawn, true) || !scanner.HasJobOnThing(pawn, thing, true)) continue;
                var job = scanner.JobOnThing(pawn, thing, true);
                if (job == null || job.targetA.Thing != thing) continue;
                var grave = job.targetB.Thing as Building_Grave;
                bool burial = grave != null && !grave.HasCorpse && thing is Corpse;
                bool relocation = !burials.Contains(thingId) && job.def == JobDefOf.HaulToCell && job.targetB.IsValid
                    && DirtyCell(map, job.targetB.Cell)
                    && (job.targetB.Cell.GetZone(map) as Zone_Stockpile)?.GetStoreSettings().AllowedToAccept(thing) == true;
                // Preserve the observed identity: splitting/merging cannot certify exact delivery.
                if (relocation && (job.count < thing.stackCount
                    || job.targetB.Cell.GetThingList(map).Any(t => t.def == thing.def))) continue;
                if (!burial && !relocation) continue;
                if (!pawn.CanReach(job.targetB, PathEndMode.Touch, Danger.None)) continue;
                job.workGiverDef = giver;
                if (!dryRun)
                {
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    if (pawn.CurJob != job) return Refuse("Native job not verified; observe before retrying");
                }
                return new { success = true, accepted = true, dryRun, thingId, pawn = pawnId,
                    method = burial ? "burial" : "relocation", destination = BridgeCommon.Pos(job.targetB.Cell),
                    graveId = grave == null ? null : Id(grave), job = job.def.defName,
                    count = job.count, meaning = "Order only; exact native item containment still required" };
            }
            return Refuse("No native hauling job with an eligible separated storage or burial destination; check filters, space, pawn preferences and reachability");
        }
    }
}
