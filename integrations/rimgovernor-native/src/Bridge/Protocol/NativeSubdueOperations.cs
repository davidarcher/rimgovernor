#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The SUBDUE pawn order (#939): a colonist beats a standing colonist in
    // an aggressive mental break down with blunt melee. Ordinary melee damage
    // applies. This is containment, never execution or custody. The subduer
    // is drafted when it is not; the plan's draft keeps it drafted.
    internal static class NativeSubdueOperations
    {
        internal static Common.Failure? Resolve(Operations.PawnOrderIntent intent, Common.ObservationContext context, out Pawn? pawn, out Pawn? target)
        {
            target = null;
            var failure = NativePawnOrderIntent.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            target = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent.TargetId);
            if (target == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Subdue target is not spawned.");
            string? reason = null;
            if (!snapshot!.Eligible || !pawn!.IsColonistPlayerControlled || pawn.WorkTagIsDisabled(WorkTags.Violent)) reason = "incapable colonist";
            else if (pawn.equipment?.Primary != null && !pawn.equipment.Primary.def.IsMeleeWeapon) reason = "ranged weapons are refused";
            else if (target.Faction != Faction.OfPlayer || !target.RaceProps.Humanlike || target.IsPrisonerOfColony || target.Dead || target.Downed || !target.InAggroMentalState) reason = "target must be a standing aggressive colonist";
            else if (!Running(pawn, target) && !pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)) reason = "target is unreachable";
            return reason == null ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Subdue refused: " + reason);
        }

        internal static Common.Failure? Validate(Operations.PawnOrderIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.PawnOrderIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var victim);
            if (failure != null) throw new InvalidOperationException("Subdue prerequisites changed before apply: " + failure.Detail);
            if (Running(pawn!, victim!)) return NativePawnOrderIntent.Evidence(pawn!, victim!, pawn!.CurJob, false);
            if (!pawn!.drafter.Drafted) pawn.drafter.Drafted = true;
            if (!pawn.drafter.Drafted) throw new InvalidOperationException("Subduer could not be drafted.");
            var job = MakeJob(pawn, victim!);
            if (!Take(pawn, job, victim!)) throw new InvalidOperationException("Native subdue job was not taken.");
            return NativePawnOrderIntent.Evidence(pawn, victim!, job, true);
        }

        private static bool Running(Pawn pawn, Pawn target) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.AttackMelee && ReferenceEquals(pawn.CurJob.targetA.Thing, target);

        // A containment melee job: a legal native blunt attack (including
        // fists) when available, never killing the downed victim.
        // JobDriver_AttackMelee passes this verb to TryMeleeAttack unchanged.
        private static Job MakeJob(Pawn pawn, Pawn victim)
        {
            var job = JobMaker.MakeJob(JobDefOf.AttackMelee, victim); job.killIncappedTarget = false;
            job.verbToUse = pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false)
                .Select(v => v.verb).FirstOrDefault(v => v.IsUsableOn(victim) && v.verbProps.meleeDamageDef == DamageDefOf.Blunt);
            return job;
        }
        // Orders the job; it ends once the victim is downed or out of the break.
        private static bool Take(Pawn pawn, Job job, Pawn victim)
        {
            bool accepted = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (accepted && pawn.CurJob == job) pawn.jobs.curDriver.AddEndCondition(() => victim.Dead || victim.Destroyed ? JobCondition.Incompletable
                : victim.Downed || !victim.InAggroMentalState ? JobCondition.Succeeded : JobCondition.Ongoing);
            return accepted;
        }
    }
}
