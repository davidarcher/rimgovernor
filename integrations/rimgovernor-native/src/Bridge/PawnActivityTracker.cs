#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    /// <summary>Passive game-thread observations. Never creates, changes or terminates a job.</summary>
    public static class PawnActivityTracker
    {

        private static TelemetryEmitter? emitter;
        private static PawnActivityLedger? ledger;
        private static Game? game;
        private static int lastTick = -1;
        private static bool installed;
        private static bool failed;


        internal static void Attach(TelemetryEmitter target)
        {
            emitter = target;
            ledger = new PawnActivityLedger(row => PawnActivityTelemetry.Write(target, row));
            try { Install(); Seed(); } catch (Exception error) { Fail(error); }
        }

        internal static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.pawn-activity");
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)),
                postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Started)));
            // Prefix captures the old job before cleanup/replacement and pooled job reuse.
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)),
                prefix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Ending)),
                postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Started)));
            harmony.Patch(AccessTools.Method(typeof(JobDriver), "TryActuallyStartNextToil"),
                postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(ToilChanged)));
            foreach (var name in new[] { "StartPath", "StopDead" })
                harmony.Patch(AccessTools.Method(typeof(Pawn_PathFollower), name),
                    postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Started)));
            foreach (var method in typeof(Pawn_CarryTracker).GetMethods().Where(m =>
                m.Name == "TryStartCarry" || m.Name == "TryDropCarriedThing" || m.Name == "DestroyCarriedThing"))
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Started)));
            foreach (var name in new[] { "MakeDowned", "MakeUndowned" })
                harmony.Patch(AccessTools.Method(typeof(Pawn_HealthTracker), name),
                    postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Started)));
            foreach (var name in new[] { "SpawnSetup", "SetFaction", "Kill" })
                harmony.Patch(AccessTools.Method(typeof(Pawn), name),
                    postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(PawnChanged)));
            harmony.Patch(AccessTools.Method(typeof(Pawn), "DeSpawn"),
                prefix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Despawning)),
                postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(PawnChanged)));
            installed = true;
        }

        private static bool Ready()
        {
            // The brain attaches at the main menu, where there is no game: Find.TickManager dereferences Current.Game and throws.
            var current = Current.Game;
            if (ledger == null || failed || current == null) return false;
            var now = current.tickManager?.TicksGame ?? -1;
            if (!ReferenceEquals(game, current) || now < lastTick)
            {
                ledger.Reset();
                game = current;
            }
            lastTick = now;
            return game != null && now >= 0;
        }

        private static bool Included(Pawn pawn) => pawn != null && pawn.Spawned && pawn.IsColonist && !pawn.Dead;
        private static string Location(Pawn pawn) => pawn.Position.x + "," + pawn.Position.z;
        private static string Target(LocalTargetInfo target) => !target.IsValid ? "" :
            target.HasThing ? target.Thing.GetUniqueLoadID() : target.Cell.x + "," + target.Cell.z;

        private static PawnActivitySample ReadSample(Pawn pawn)
        {
            var job = pawn.CurJob;
            var driver = pawn.jobs?.curDriver;
            var identity = Current.Game.GetComponent<ColonyIdentity>();
            var world = identity.ColonyId + "/" + identity.LoadToken + "/" + pawn.Map.uniqueID;
            // Path travel includes temporary congestion; it is not per-cell movement telemetry.
            var moving = pawn.pather?.Moving == true;
            var carrying = pawn.carryTracker?.CarriedThing != null;
            var activity = "unknown";
            if (pawn.Downed) activity = "recovery";
            else if (moving) activity = carrying ? "carrying" : "travel";
            else if (job == null) activity = "idle";
            else if (job.def == JobDefOf.Ingest || job.def == JobDefOf.LayDown || job.def.joyKind != null) activity = "needs";
            else if (job.def == JobDefOf.Wait || NativePawnActivities.IsHold(job)) activity = "waiting";
            else if (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic || job.def == JobDefOf.Wait_Combat) activity = "combat";
            else if (driver?.ActiveSkill != null && driver.GetType().Assembly == typeof(JobDriver).Assembly) activity = "work";
            // A stationary haul, an unnamed/modded toil and a standing pawn are not proof of work or input waiting.
            var purpose = job == null ? "unknown" : OperationIntent.PurposeOf(pawn, job)
                ?? job.workGiverDef?.workType?.defName ?? "unknown";
            return new PawnActivitySample(world, pawn.GetUniqueLoadID(), Find.TickManager.TicksGame,
                job?.def?.defName ?? "", job?.loadID ?? -1,
                (driver?.GetType().FullName ?? "") + ":" + (driver?.CurToilIndex ?? -1).ToString(CultureInfo.InvariantCulture),
                activity, purpose,
                job == null ? "" : Target(job.targetA) + "|" + Target(job.targetB) + "|" + Target(job.targetC),
                Location(pawn), NativePawnActivities.Read(pawn)?.Method, NativePawnActivities.Read(pawn)?.Assignment);
        }

        private static void Started(Pawn ___pawn)
        {
            try
            {
                if (!Ready() || ___pawn == null) return;
                if (!Included(___pawn))
                {
                    ledger!.Remove(___pawn.GetUniqueLoadID(), lastTick, Location(___pawn), "left_observation");
                    return;
                }
                var refused = ledger!.RefusedPawns;
                ledger.Observe(ReadSample(___pawn));
                if (ledger.RefusedPawns != refused) emitter!.Event(TelemetryLevel.Warn, "pawn-activity", "pawn_activity_capacity", "Colonists beyond the open-interval cap are unobserved.", new Dictionary<string, object?> { ["refused_observations"] = ledger.RefusedPawns });
            }
            catch (Exception error) { Fail(error); }
        }

        private static void Ending(Pawn ___pawn, JobCondition condition)
        {
            try
            {
                if (!Ready() || ___pawn == null) return;
                ledger!.End(___pawn.GetUniqueLoadID(), ___pawn.CurJob?.loadID ?? -1,
                    Find.TickManager.TicksGame, Location(___pawn), condition.ToString());
            }
            catch (Exception error) { Fail(error); }
        }

        private static void ToilChanged(JobDriver __instance)
        {
            // Instant toils may recurse or replace the job during init. Observe only the live driver.
            if (__instance.pawn?.jobs?.curDriver == __instance) Started(__instance.pawn);
        }

        private static void PawnChanged(Pawn __instance) => Started(__instance);

        private static void Despawning(Pawn __instance)
        {
            try
            {
                if (Ready()) ledger!.Remove(__instance.GetUniqueLoadID(), lastTick, Location(__instance), "despawn");
            }
            catch (Exception error) { Fail(error); }
        }

        private static void Seed()
        {
            // One census when attaching to an already running game, never a periodic scan.
            if (!Ready()) return;
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned) Started(pawn);
        }
        internal static void Execution(RimGovernor.Contracts.Wire.Operations.PawnActivityExecution row, Pawn pawn)
        {
            emitter?.EmitRow("pawn_activity_execution", new RowContext { Component = "pawn-activity", Tick = row.Tick },
                new Dictionary<string, object?> { ["pawn"] = pawn.GetUniqueLoadID(), ["assignment"] = row.Assignment,
                    ["method"] = row.Method, ["revision"] = row.Revision, ["step"] = row.Step, ["state"] = row.State,
                    ["reason"] = row.Reason, ["job_id"] = row.JobId });
        }
        private static void Fail(Exception error)
        {
            failed = true;
            emitter?.Event(TelemetryLevel.Error, "pawn-activity", "pawn_activity_fault",
                "Activity observation stopped; subsequent time is unknown.",
                new Dictionary<string, object?> { ["error"] = error.ToString() });
            ModLog.Error("observe", "Pawn activity observation stopped: " + error);
        }

        internal static void Output(string kind, string source, string resource, int units, Pawn? pawn = null)
        {
            try
            {
                if (!Ready()) return;
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                emitter!.EmitRow(PawnActivityTelemetry.OutputKind,
                    new RowContext { Component = "pawn-activity", Tick = lastTick },
                    new Dictionary<string, object?>
                    {
                        ["colony"] = identity.ColonyId, ["load"] = identity.LoadToken,
                        ["map"] = pawn?.Map?.uniqueID, [PawnActivityTelemetry.PawnKey] = pawn?.GetUniqueLoadID(), ["job_id"] = pawn?.CurJob?.loadID,
                        ["source"] = source, ["output_kind"] = kind, [PawnActivityTelemetry.ResourceKey] = resource,
                        [PawnActivityTelemetry.UnitsKey] = units, ["tick"] = lastTick,
                    });
            }
            catch (Exception error) { Fail(error); }
        }
    }
}
