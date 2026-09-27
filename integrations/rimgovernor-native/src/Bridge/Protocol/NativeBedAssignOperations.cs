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
    // BedAssignIntent (#941): one free colonist's bed ownership, a synchronous
    // CompAssignableToPawn.TryAssignPawn write with the legacy
    // home/upkeep_bed tool's eligibility rules. Native checks the pawn, the
    // bed and the pawn's previous ownership live when it applies; a pawn that
    // already owns the bed applies again.
    internal sealed class BedAssignActionHandler : IActionHandler
    {
        /// <summary>Why bed cannot be assigned to pawn right now, or null when it can; each gate names itself so a harness can tell them apart.</summary>
        private static string? BedRefusal(Building_Bed bed, Pawn pawn, Map map)
        {
            if (!bed.Spawned || bed.Faction != Faction.OfPlayerSilentFail || !bed.def.building.bed_humanlike)
                return "Bed unavailable: not a spawned player-owned humanlike bed.";
            if (bed.Medical || bed.ForPrisoners) return "Bed unavailable: medical or prisoner bed.";
            // A willing love partner may join a partner's bed with a free
            // slot (#812); nobody else is ever put in an owned bed.
            if (bed.OwnersForReading.Any() && (!bed.AnyUnownedSleepingSlot || bed.OwnersForReading.Any(o => o == pawn
                    || !LovePartnerRelationUtility.LovePartnerRelationExists(pawn, o) || !BedUtility.WillingToShareBed(pawn, o))))
                return "Bed unavailable: already assigned.";
            if (bed.IsForbidden(pawn)) return "Bed unavailable: forbidden to the pawn.";
            if (bed.IsBurning()) return "Bed unavailable: burning.";
            if (!bed.OccupiedRect().All(c => c.Roofed(map))) return "Bed unavailable: not fully roofed.";
            var restriction = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
            if (restriction != null && !bed.OccupiedRect().All(c => restriction[c])) return "Bed unavailable: outside the pawn's allowed area.";
            if (!pawn.CanReach(bed, PathEndMode.OnCell, Danger.None)) return "Bed unavailable: pawn cannot reach it safely.";
            var ambient = bed.AmbientTemperature;
            var comfyMin = pawn.GetStatValue(StatDefOf.ComfyTemperatureMin);
            var comfyMax = pawn.GetStatValue(StatDefOf.ComfyTemperatureMax);
            if (ambient < comfyMin || ambient > comfyMax)
                return $"Bed unavailable: ambient temperature {ambient:F1} is outside the pawn's comfy band [{comfyMin:F1}, {comfyMax:F1}].";
            return null;
        }

        private static bool Valid(Operations.BedAssignIntent? intent) => intent != null
            && intent.HasPawnId && ProtoBoundary.IsIdentifier(intent.PawnId) && intent.HasBedId && ProtoBoundary.IsIdentifier(intent.BedId)
            && intent.PawnId != intent.BedId && intent.ExpectedPreviousBed != null
            && intent.ExpectedPreviousBed.ValueCase != Operations.Assignment.ValueOneofCase.None
            && (intent.ExpectedPreviousBed.ValueCase != Operations.Assignment.ValueOneofCase.EntityId
                || (ProtoBoundary.IsIdentifier(intent.ExpectedPreviousBed.EntityId) && intent.ExpectedPreviousBed.EntityId != intent.BedId));

        // Resolve returns null with assignable null when the pawn already
        // owns the bed: the intent holds and applies again.
        private static Common.Failure? Resolve(Operations.BedAssignIntent? intent, Common.ObservationContext context,
            out Pawn pawn, out Building_Bed bed, out CompAssignableToPawn? assignable)
        {
            pawn = null!; bed = null!; assignable = null;
            if (!Valid(intent))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed assignment requires an exact pawn, bed and expected previous bed.");
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Current map required.");
            var found = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(x => x.GetUniqueLoadID() == intent!.PawnId);
            var target = map.listerThings.AllThings.OfType<Building_Bed>().SingleOrDefault(b => b.GetUniqueLoadID() == intent!.BedId);
            if (found?.ownership != null && target != null && found.ownership.OwnedBed == target)
            { pawn = found; bed = target; return null; }
            // Assigning a bed interrupts no job, so the pawn's current order,
            // whoever gave it, is no reason to refuse (#461).
            if (found == null || found.Dead || found.Downed || found.Drafted || found.InMentalState || found.ownership == null
                || found.health.HasHediffsNeedingTend())
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Pawn unavailable for bed assignment.");
            var previousID = found.ownership.OwnedBed?.GetUniqueLoadID() ?? "";
            var expectPrevious = intent!.ExpectedPreviousBed.ValueCase == Operations.Assignment.ValueOneofCase.EntityId ? intent.ExpectedPreviousBed.EntityId : "";
            if (previousID != expectPrevious)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Previous bed assignment changed; observe before recovery.");
            var refusal = target == null ? "Bed unavailable: not found on the map." : BedRefusal(target, found, map);
            if (refusal != null || target == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, refusal ?? "Bed unavailable.");
            var comp = target.GetComp<CompAssignableToPawn>();
            if (comp == null || !comp.AssigningCandidates.Contains(found) || !comp.CanAssignTo(found).Accepted || comp.IdeoligionForbids(found))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native bed assignment eligibility refused.");
            pawn = found; bed = target; assignable = comp;
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.BedAssign, context, out _, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.BedAssign;
            var failure = Resolve(intent, context, out var pawn, out var bed, out var assignable);
            if (failure != null) throw new InvalidOperationException("Bed assignment prerequisites changed before apply: " + failure.Detail);
            assignable?.TryAssignPawn(pawn);
            if (pawn.ownership!.OwnedBed != bed) throw new InvalidOperationException("Native bed assignment did not take effect.");
            var effect = new Receipts.BedEffect { PawnId = pawn.GetUniqueLoadID(), BedId = bed.GetUniqueLoadID(), Assigned = true, Sleeping = pawn.CurrentBed() == bed };
            if (intent.ExpectedPreviousBed.ValueCase == Operations.Assignment.ValueOneofCase.EntityId) effect.PreviousBedId = intent.ExpectedPreviousBed.EntityId;
            return new Receipts.EffectEvidence { Bed = effect };
        }
    }
}
