using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // GiveJobIntent Equip: FloatMenuOptionProvider_Equip's
    // gates in order, then the exact JobDefOf.Equip job a player's float-menu
    // click would produce. Equip applies no draft gate, so this checks pawn
    // eligibility but not Drafted. Checked live at apply; a pawn already
    // holding or walking to the weapon applies again.
    internal static class NativeEquipOperations
    {
        internal static bool Eligible(Thing? weapon) => weapon != null && NativeSupplyAllow.Eligible(weapon)
            && weapon.def.IsWeapon && (weapon as ThingWithComps)?.GetComp<CompEquippable>() != null;

        private static bool Holds(Pawn pawn, Thing weapon) => ReferenceEquals(pawn.equipment?.Primary, weapon);
        private static bool Running(Pawn pawn, Thing weapon) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Equip && pawn.CurJob.targetA.Thing == weapon;

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out Thing? weapon)
        {
            weapon = null;
            if (!NativeGiveJob.TryPawn(intent, context, out pawn, out var snapshot, out var failure)) return failure;
            if (!snapshot.Eligible) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Equip requires an eligible pawn.");
            var map = GameThread.LoadedMap(context);
            weapon = RefIndex.Is(pawn.equipment?.Primary, intent.TargetId) ? (pawn.equipment ?? throw new System.InvalidOperationException("Required native value is unavailable: pawn.equipment.")).Primary
                : RefIndex.Thing(map, intent.TargetId);
            if (weapon != null && (Holds(pawn, weapon) || Running(pawn, weapon))) return null;
            if (weapon == null || !Eligible(weapon)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact equippable weapon is unavailable.");
            if (pawn.equipment == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn has no equipment tracker and can carry no weapon.");
            if (pawn.WorkTagIsDisabled(WorkTags.Violent))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn has WorkTags.Violent disabled and cannot equip a weapon.");
            if (weapon.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn has WorkTags.Shooting disabled and cannot equip a ranged weapon.");
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn is incapable of manipulation and cannot pick anything up.");
            if (weapon.IsBurning()) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Weapon is on fire.");
            if (!pawn.CanReach(weapon, PathEndMode.ClosestTouch, Danger.Deadly))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn cannot reach the weapon.");
            if (!EquipmentUtility.CanEquip(weapon, pawn, out var cantReason, false))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn cannot equip the weapon: " + (string.IsNullOrEmpty(cantReason) ? "refused" : cantReason));
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var weapon);
            if (failure != null || pawn == null || weapon == null) throw new InvalidOperationException(failure?.Detail);
            if (Running(pawn, weapon)) return NativeGiveJob.Evidence(pawn, weapon, pawn.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.Equip, weapon);
            if (Holds(pawn, weapon)) return NativeGiveJob.Evidence(pawn, weapon, job, false);
            // Equipping a weapon at the pawn's own feet can complete within
            // the call; the weapon in hand counts as taken.
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !Holds(pawn, weapon) && (pawn.CurJob == null || pawn.CurJob.loadID != job.loadID))
                throw new InvalidOperationException("The pawn did not take the equip job.");
            return NativeGiveJob.Evidence(pawn, weapon, job, true);
        }
    }
}
