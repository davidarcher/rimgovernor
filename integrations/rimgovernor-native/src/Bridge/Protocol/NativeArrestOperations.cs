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
    // GiveJobIntent Arrest: the arrester's owned draft (the
    // plan couples a draft intent ahead of it) takes JobDefOf.Arrest onto the
    // exact prisoner bed. Checked live at apply; an arrester already running
    // the arrest applies again.
    internal static class NativeArrestOperations
    {
        private static bool Running(Pawn pawn, Pawn target, Building_Bed bed) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Arrest
            && pawn.CurJob.targetA.Thing == target && pawn.CurJob.targetB.Thing == bed;

        private static Common.Failure? Resolve(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out Pawn? target, out Building_Bed? bed)
        {
            target = null; bed = null;
            var failure = NativeGiveJob.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            if (!intent.HasBedId || !ProtoBoundary.IsIdentifier(intent.BedId))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Arrest requires the exact prisoner bed.");
            var map = ProtoBoundary.LoadedMap(context);
            target = map.mapPawns.AllPawnsSpawned.ById(intent.TargetId);
            bed = RefIndex.Thing<Building_Bed>(map, intent.BedId);
            if (target == null || bed == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact arrest target or bed is not spawned on this map.");
            if (!NativeMovementOperations.Owns(snapshot!))
                return ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "Arrest requires an eligible drafted pawn.");
            if (Running(pawn!, target, bed)) return null;
            string? reason = null;
            if (target.Dead || !target.InMentalState && !(target.Faction == Faction.OfAncients && !target.HostileTo(pawn) && !target.Downed && target.RaceProps.Humanlike && !target.IsPrisonerOfColony)) reason = "target is not living in a mental state or a standing neutral ancient";
            else if (pawn!.equipment?.Primary == null || !pawn.equipment.Primary.def.IsWeapon || pawn.WorkTagIsDisabled(WorkTags.Violent))
                reason = "arrester is unarmed or incapable of violence";
            else if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) reason = "arrester cannot manipulate";
            else if (!target.CanBeArrestedBy(pawn) || target.Downed && target.guilt.IsGuilty
                || target.InAggroMentalState && !target.health.hediffSet.HasHediff(HediffDefOf.Scaria) && target.HostileTo(pawn))
                reason = "native arrest eligibility refuses the target (hostile aggressive mental states cannot be arrested)";
            else if (pawn.InSameExtraFaction(target, ExtraFactionType.HomeFaction) || pawn.InSameExtraFaction(target, ExtraFactionType.MiniFaction))
                reason = "native same-extra-faction restriction";
            else if (!bed.ForPrisoners || bed.Faction != Faction.OfPlayerSilentFail
                || !RestUtility.IsValidBedFor(bed, target, pawn, false, guestStatus: GuestStatus.Prisoner))
                reason = "destination must be a usable native prisoner bed";
            else if (!pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.Deadly)) reason = "target cannot be reserved and reached";
            return reason == null ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Arrest refused: " + reason + ".");
        }

        internal static Common.Failure? Validate(JobOrder intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(JobOrder intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var target, out var bed);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(pawn!, target!, bed!)) return NativeGiveJob.Evidence(pawn!, target!, pawn!.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.Arrest, target, bed); job.count = 1;
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != job.loadID)
                throw new InvalidOperationException("The arrester did not take the arrest job.");
            return NativeGiveJob.Evidence(pawn, target!, job, true);
        }
    }
}
