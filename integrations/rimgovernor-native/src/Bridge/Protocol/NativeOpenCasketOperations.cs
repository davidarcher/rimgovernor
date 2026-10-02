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
    // PawnOrderIntent kind OPEN_CASKET (#460, #939): the opener of
    // ClearAncientShrine's melee lock runs the vanilla Open job on one filled
    // ancient cryptosleep casket. Opening one casket ejects every casket of
    // the shrine group, so a plan carries one order after the drafts and
    // moves that stand a melee colonist at each casket's interaction cell.
    // Unlike Repair the pawn may be drafted: JobDriver_Open is an ordered
    // job the draft does not refuse, and the opener is one of the lockers.
    // JobDriver_Open fails without an Open designation on the target, so the
    // order adds one right before the job, and the job's finish action
    // removes it again when the job ends with the casket still full, so no
    // undrafted colonist opens it behind the lock through WorkGiver_Open.
    // Checked live at apply; an opener already on the job applies again.
    internal static class NativeOpenCasketOperations
    {
        private static void Undesignate(Building_Casket casket)
        {
            if (casket.Destroyed || !casket.Spawned || casket.Map == null) return;
            var designation = casket.Map.designationManager.DesignationOn(casket, DesignationDefOf.Open);
            if (designation != null) casket.Map.designationManager.RemoveDesignation(designation);
        }

        private static bool Running(Pawn pawn, Thing casket) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Open && pawn.CurJob.targetA.Thing == casket;

        private static Common.Failure? Resolve(Operations.PawnOrderIntent intent, Common.ObservationContext context, out Pawn? pawn, out Building_AncientCryptosleepCasket? casket)
        {
            casket = null;
            var failure = NativePawnOrderIntent.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            if (!snapshot!.Eligible)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Opening a casket requires an eligible colonist.");
            var map = ProtoBoundary.LoadedMap(context);
            casket = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).OfType<Building_AncientCryptosleepCasket>()
                .ById(intent.TargetId);
            if (casket == null || casket.Destroyed || !casket.Spawned || casket.Map != map)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact spawned ancient cryptosleep casket is unavailable.");
            if (Running(pawn!, casket)) return null;
            if (!casket.HasAnyContents || !casket.CanOpen)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Casket is empty.");
            if (!pawn!.CanReach(casket, PathEndMode.InteractionCell, Danger.Some))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot reach the casket interaction cell.");
            if (!pawn!.CanReserve(casket))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Casket is reserved by another pawn.");
            if (pawn!.WorkTagIsDisabled(WorkTags.ManualDumb))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn is incapable of the dumb labor the Open job needs.");
            return null;
        }

        internal static Common.Failure? Validate(Operations.PawnOrderIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.PawnOrderIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var casket);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(pawn!, casket!)) return NativePawnOrderIntent.Evidence(pawn!, casket!, pawn!.CurJob, false);
            var map = casket!.Map;
            if (map.designationManager.DesignationOn(casket, DesignationDefOf.Open) == null)
                map.designationManager.AddDesignation(new Designation(casket, DesignationDefOf.Open));
            var job = JobMaker.MakeJob(JobDefOf.Open, casket);
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != job.loadID || pawn.jobs.curDriver == null)
            {
                Undesignate(casket);
                throw new InvalidOperationException("The pawn did not take the Open job.");
            }
            pawn.jobs.curDriver.AddFinishAction(_ => { if (casket.HasAnyContents) Undesignate(casket); });
            return NativePawnOrderIntent.Evidence(pawn, casket, job, true);
        }
    }
}
