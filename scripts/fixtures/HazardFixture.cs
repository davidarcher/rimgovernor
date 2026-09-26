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
    // Disposable test setup only (#626). Injects one hazard of a declared
    // class into a running colony so the hazard/* acceptance cases can
    // measure the supervisor's detection gap at Ultrafast against the bound
    // docs/developers/architecture/hazard-detection-bounds.md declares. Every
    // op reports the game tick it acted on: the stop's detect_ticks (#621)
    // measures from the hazard's own occurrence tick, the case's inject gap
    // from this one. The ops run while the clock runs (main-thread
    // invocation lands between frames); cleanup vanishes what was spawned
    // and heals what was hurt.
    public sealed class HazardFixture
    {
        private static readonly List<Thing> spawned = new List<Thing>();

        [Tool("test/hazard_inject", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: inject one supervisor hazard class into the running colony: op=hostile|downed|injury|predator|cleanup.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string op, string kind = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null) return Refuse("A loaded colony map is required.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                switch (op)
                {
                    case "hostile": return Hostile(map, colonists, kind);
                    case "downed": return Downed(colonists);
                    case "injury": return Injury(colonists);
                    case "predator": return Predator(map, colonists, kind == "" ? "Cougar" : kind);
                    case "cleanup": return Cleanup(map);
                    default: return Refuse("Unknown op " + op + ".");
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        // A hostile pawn 8-12 cells from a standing colonist: Pawn.SpawnSetup
        // is the hook, the spawn tick the occurrence.
        private static object Hostile(Map map, List<Pawn> colonists, string kind)
        {
            var kindDef = DefDatabase<PawnKindDef>.GetNamedSilentFail(kind == "" ? "Mech_Scyther" : kind);
            if (kindDef == null) return Refuse("kind must name a PawnKindDef.");
            var faction = kindDef.defaultFactionDef != null ? Find.FactionManager.FirstFactionOfDef(kindDef.defaultFactionDef) : null;
            if (faction == null) faction = Find.FactionManager.AllFactions.FirstOrDefault(f => f.HostileTo(Faction.OfPlayer) && !f.IsPlayer);
            if (faction == null) return Refuse("The world has no faction hostile to the player.");
            var near = colonists.FirstOrDefault();
            if (near == null) return Refuse("No standing colonist to threaten.");
            var cell = NearCell(map, near.Position);
            if (!cell.IsValid) return Refuse("No standable cell 8-12 cells from a colonist.");
            var pawn = PawnGenerator.GeneratePawn(kindDef, faction);
            GenSpawn.Spawn(pawn, cell, map);
            if (!pawn.Spawned) return Refuse("The " + kindDef.defName + " did not spawn.");
            if (!pawn.HostileTo(Faction.OfPlayer)) { pawn.Destroy(DestroyMode.Vanish); return Refuse("The spawned pawn is not hostile to the player."); }
            spawned.Add(pawn);
            return new { success = true, pawn = pawn.GetUniqueLoadID(), pawnId = pawn.thingIDNumber, kind = kindDef.defName, faction = faction.def.defName,
                x = cell.x, z = cell.z, distance = cell.DistanceTo(near.Position), near = near.GetUniqueLoadID(), tick = Find.TickManager.TicksGame };
        }

        // Anesthetic at full severity downs the colonist through the health
        // tracker: Pawn_HealthTracker.MakeDowned is the hook.
        private static object Downed(List<Pawn> colonists)
        {
            var pawn = colonists.FirstOrDefault();
            if (pawn == null) return Refuse("No standing colonist to down.");
            var hediff = HediffMaker.MakeHediff(HediffDefOf.Anesthetic, pawn);
            hediff.Severity = 1f;
            pawn.health.AddHediff(hediff);
            if (!pawn.Downed) { pawn.health.RemoveHediff(hediff); return Refuse("Anesthetic did not down the colonist."); }
            return new { success = true, pawn = pawn.GetUniqueLoadID(), pawnId = pawn.thingIDNumber, downed = pawn.Downed, tick = Find.TickManager.TicksGame };
        }

        // One cut on a standing colonist, deliberately under the stop tier's
        // severity floor (#584): polled by the injury snapshot; the wound's
        // age is the occurrence. bleedOutTicks reports the game's own
        // estimate (int.MaxValue when the pawn is not bleeding out, reported
        // as 0) so the case can assert the wound is a demoted tier, not a
        // danger.
        private static object Injury(List<Pawn> colonists)
        {
            var pawn = colonists.FirstOrDefault();
            if (pawn == null) return Refuse("No standing colonist to injure.");
            var part = pawn.health.hediffSet.GetNotMissingParts().FirstOrDefault(p => p.def == BodyPartDefOf.Torso) ?? pawn.RaceProps.body.corePart;
            var before = pawn.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            // Small on purpose (#584): a 6-damage cut dropped summary health
            // past the watch's healthDropFraction and stopped the window as a
            // combat threshold crossing, which is a different tier. This one
            // is a new wound and nothing else.
            var result = pawn.TakeDamage(new DamageInfo(DamageDefOf.Cut, 2f, 0f, -1f, null, part));
            var after = pawn.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            if (after <= before) return Refuse("The cut left no injury.");
            var bleedOut = HealthUtility.TicksUntilDeathDueToBloodLoss(pawn);
            return new { success = true, pawn = pawn.GetUniqueLoadID(), pawnId = pawn.thingIDNumber, damage = result.totalDamageDealt,
                injuries = after, downed = pawn.Downed, bleedOutTicks = bleedOut == int.MaxValue ? 0 : bleedOut,
                tick = Find.TickManager.TicksGame };
        }

        // A hungry predator 8-12 cells from a colonist on a PredatorHunt job
        // for it (as the defense fixture stages one): polled; the job's startTick
        // is the occurrence.
        private static object Predator(Map map, List<Pawn> colonists, string kind)
        {
            var kindDef = DefDatabase<PawnKindDef>.GetNamedSilentFail(kind);
            if (kindDef == null || !kindDef.RaceProps.predator) return Refuse("kind must name a predator PawnKindDef.");
            var prey = colonists.FirstOrDefault();
            if (prey == null) return Refuse("No standing colonist to hunt.");
            var cell = NearCell(map, prey.Position);
            if (!cell.IsValid) return Refuse("No standable cell 8-12 cells from the prey.");
            var predator = PawnGenerator.GeneratePawn(kindDef, null);
            GenSpawn.Spawn(predator, cell, map);
            if (predator.needs?.food != null) predator.needs.food.CurLevelPercentage = 0.02f;
            var job = JobMaker.MakeJob(JobDefOf.PredatorHunt, prey);
            job.killIncappedTarget = true;
            predator.jobs.StartJob(job, JobCondition.InterruptForced);
            var hunting = predator.CurJobDef == JobDefOf.PredatorHunt && predator.CurJob?.GetTarget(TargetIndex.A).Thing == prey;
            if (!hunting) { predator.Destroy(DestroyMode.Vanish); return Refuse("The predator did not take the PredatorHunt job."); }
            spawned.Add(predator);
            return new { success = true, predator = predator.GetUniqueLoadID(), pawnId = predator.thingIDNumber, kind = kindDef.defName,
                prey = prey.GetUniqueLoadID(), x = cell.x, z = cell.z, jobStartTick = predator.CurJob.startTick, tick = Find.TickManager.TicksGame };
        }

        // Vanish what the ops spawned, heal every colonist (injuries and the
        // anesthetic) so the next case starts from a quiet colony.
        private static object Cleanup(Map map)
        {
            var vanished = 0;
            foreach (var thing in spawned.ToList())
            {
                if (thing != null && !thing.Destroyed) { thing.Destroy(DestroyMode.Vanish); vanished++; }
            }
            spawned.Clear();
            var healed = 0;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead))
            {
                var hediffs = pawn.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == HediffDefOf.Anesthetic || h.TendableNow(true)).ToList();
                foreach (var h in hediffs) pawn.health.RemoveHediff(h);
                healed += hediffs.Count;
            }
            return new { success = true, vanished, healed, tick = Find.TickManager.TicksGame };
        }

        private static IntVec3 NearCell(Map map, IntVec3 origin)
        {
            return GenRadial.RadialCellsAround(origin, 12, true).FirstOrDefault(c => c.InBounds(map)
                && c.Standable(map) && !c.Fogged(map) && c.DistanceTo(origin) >= 8
                && map.reachability.CanReach(c, origin, PathEndMode.Touch, TraverseMode.NoPassClosedDoors, Danger.Deadly));
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
