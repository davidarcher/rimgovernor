using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // GiveJobIntent DropWeapon: the colonist drops the weapon in its
    // hands, the job a player's float-menu "Drop" click on a held weapon
    // gives. DropWeapon is this protocol's own token, not a
    // JobDef name: the JobDef is found at runtime as the one whose driver is
    // JobDriver_DropEquipment, so no def name is written here. Like Equip it
    // applies no draft gate. Checked live at apply; a pawn already running the
    // job on the weapon, or whose weapon already lies on the ground, applies
    // again.
    internal static class NativeDropOperations
    {
        // The drop job's def, by its driver class. No def, or more than one,
        // is a native defect that fails the order loudly.
        private static JobDef? Def(out string? failure)
        {
            failure = null;
            var defs = DefDatabase<JobDef>.AllDefsListForReading.Where(d => d.driverClass == typeof(JobDriver_DropEquipment)).ToList();
            if (defs.Count == 1) return defs[0];
            failure = defs.Count == 0
                ? "The game has no job whose driver is JobDriver_DropEquipment, so a weapon cannot be dropped."
                : "More than one job uses the JobDriver_DropEquipment driver (" + string.Join(", ", defs.Select(d => d.defName)) + "), so the drop job is ambiguous.";
            return null;
        }

        private static bool Holds(Pawn pawn, Thing weapon) => ReferenceEquals(pawn.equipment?.Primary, weapon);
        private static bool Running(Pawn pawn, Thing weapon, JobDef def) => pawn.CurJob != null && pawn.CurJob.def == def && pawn.CurJob.targetA.Thing == weapon;

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out Thing? weapon, out JobDef? def)
        {
            weapon = null;
            def = Def(out var missing);
            if (def == null)
            {
                pawn = null;
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unsupported, (missing ?? throw new System.InvalidOperationException("Required native value is unavailable: missing.")));
            }
            if (!NativeGiveJob.TryPawn(intent, context, out pawn, out var snapshot, out var failure)) return failure;
            if (!snapshot.Eligible) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Drop requires an eligible pawn.");
            if (pawn.equipment == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn has no equipment tracker and holds no weapon.");
            var map = GameThread.LoadedMap(context);
            weapon = RefIndex.Is(pawn.equipment.Primary, intent.TargetId) ? pawn.equipment.Primary : RefIndex.Thing(map, intent.TargetId);
            if (weapon == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact weapon is unavailable.");
            if (Holds(pawn, weapon) || Running(pawn, weapon, def)) return null;
            // A weapon already on the ground is a drop that happened.
            if (weapon.Spawned) return null;
            return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Pawn does not hold that weapon.");
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var weapon, out var def);
            if (failure != null || pawn == null || weapon == null || def == null) throw new InvalidOperationException(failure?.Detail);
            if (Running(pawn, weapon, def)) return NativeGiveJob.Evidence(pawn, weapon, pawn.CurJob, false);
            var job = JobMaker.MakeJob(def, weapon);
            if (!Holds(pawn, weapon)) return NativeGiveJob.Evidence(pawn, weapon, job, false);
            // Dropping can complete within the call; the weapon out of hand
            // counts as dropped.
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) || Holds(pawn, weapon) && (pawn.CurJob == null || pawn.CurJob.loadID != job.loadID))
                throw new InvalidOperationException("The pawn did not take the drop job.");
            return NativeGiveJob.Evidence(pawn, weapon, job, true);
        }
    }
}
