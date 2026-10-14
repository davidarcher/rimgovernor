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
        public const int SampleTicks = 15;
        private static TelemetryEmitter? emitter;
        private static PawnActivityLedger? ledger;
        private static Game? game;
        private static int lastTick = -1;
        private static bool installed;
        private static bool failed;
        private static readonly HashSet<string> present = new(StringComparer.Ordinal);

        internal static void Attach(TelemetryEmitter target)
        {
            emitter = target;
            ledger = new PawnActivityLedger(row => PawnActivityTelemetry.Write(target, row), SampleTicks);
            try { Install(); } catch (Exception error) { Fail(error); }
        }

        internal static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.pawn-activity");
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)),
                postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Started)));
            // Prefix captures the old job before cleanup/replacement and pooled job reuse.
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)),
                prefix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Ending)));
            harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                postfix: new HarmonyMethod(typeof(PawnActivityTracker), nameof(Ticked)));
            installed = true;
        }

        private static bool Ready()
        {
            if (ledger == null || failed) return false;
            var now = Find.TickManager?.TicksGame ?? -1;
            if (!ReferenceEquals(game, Current.Game) || now < lastTick)
            {
                ledger.Reset();
                game = Current.Game;
            }
            lastTick = now;
            return game != null && now >= 0;
        }

        private static bool Included(Pawn pawn) => pawn != null && pawn.Spawned && pawn.IsColonist && !pawn.Dead;
        private static string Location(Pawn pawn) => pawn.Position.x + "," + pawn.Position.z;
        private static string Target(LocalTargetInfo target) => !target.IsValid ? "" :
            target.HasThing ? target.Thing.GetUniqueLoadID() : target.Cell.x + "," + target.Cell.z;

        public static PawnActivitySample ReadSample(Pawn pawn)
        {
            var job = pawn.CurJob;
            var driver = pawn.jobs?.curDriver;
            var identity = Current.Game.GetComponent<ColonyIdentity>();
            var world = identity.ColonyId + "/" + identity.LoadToken + "/" + pawn.Map.uniqueID;
            var moving = pawn.pather?.MovingNow == true;
            var carrying = pawn.carryTracker?.CarriedThing != null;
            var activity = "unknown";
            if (pawn.Downed) activity = "recovery";
            else if (moving) activity = carrying ? "carrying" : "travel";
            else if (job == null) activity = "idle";
            else if (job.def == JobDefOf.Ingest || job.def == JobDefOf.LayDown || job.def.joyKind != null) activity = "needs";
            else if (job.def == JobDefOf.Wait) activity = "waiting";
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
                Location(pawn));
        }

        private static void Started(Pawn ___pawn)
        {
            try { if (Ready() && Included(___pawn)) ledger!.Observe(ReadSample(___pawn)); }
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

        private static void Ticked()
        {
            try
            {
                if (!Ready() || lastTick % SampleTicks != 0) return;
                var refused = ledger!.RefusedPawns;
                present.Clear();
                foreach (var map in Find.Maps)
                    foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                    {
                        if (!Included(pawn)) continue;
                        present.Add(pawn.GetUniqueLoadID());
                        ledger!.Observe(ReadSample(pawn));
                    }
                ledger!.RemoveExcept(present, lastTick);
                if (ledger.RefusedPawns != refused) emitter!.Event(TelemetryLevel.Warn, "pawn-activity", "pawn_activity_capacity", "Colonists beyond the open-interval cap are unobserved.", new Dictionary<string, object?> { ["refused_observations"] = ledger.RefusedPawns });
            }
            catch (Exception error) { Fail(error); }
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
                        ["map"] = pawn?.Map?.uniqueID, ["pawn"] = pawn?.GetUniqueLoadID(), ["job_id"] = pawn?.CurJob?.loadID,
                        ["source"] = source, ["output_kind"] = kind, ["resource"] = resource,
                        ["units"] = units, ["tick"] = lastTick,
                    });
            }
            catch (Exception error) { Fail(error); }
        }
    }
}
