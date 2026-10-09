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
    // AssignIntent: one free colonist's ownership of one
    // assignable thing, a synchronous CompAssignableToPawn.TryAssignPawn
    // write. A throne or a grave is refused when owned by someone else; a thing
    // without CompAssignableToPawn is refused. Native checks the pawn, the
    // thing and the pawn's previous assignment of that kind live when it
    // applies; a pawn that already owns the thing applies again.
    internal sealed class AssignActionHandler : IActionHandler
    {
        /// <summary>Why bed cannot be assigned at all, or null; legality beyond identity is the comp validator (CanAssignTo) in Resolve.</summary>
        internal static string? BedRefusal(Building_Bed bed)
        {
            if (!bed.Spawned || bed.Faction != Faction.OfPlayerSilentFail || !bed.def.building.bed_humanlike)
                return "Bed unavailable: not a spawned player-owned humanlike bed.";
            return null;
        }

        /// <summary>Why a non-bed thing cannot be assigned to pawn right now, or null when it can.</summary>
        internal static string? ThingRefusal(ThingWithComps thing, CompAssignableToPawn comp, Pawn pawn)
        {
            if (!thing.Spawned || thing.Faction != Faction.OfPlayerSilentFail) return "Thing unavailable: not a spawned player-owned thing.";
            if (comp.AssignedPawnsForReading.Any(o => o != pawn)) return "Thing unavailable: already assigned.";
            if (thing.IsForbidden(pawn)) return "Thing unavailable: forbidden to the pawn.";
            if (thing.IsBurning()) return "Thing unavailable: burning.";
            return null;
        }

        // The id of the thing of target's kind the pawn owns now, "" for none;
        // false when the kind is not one native reads a pawn's assignment of.
        private static bool TryPrevious(Thing target, Pawn pawn, out string previous)
        {
            previous = "";
            Thing? owned;
            switch (target)
            {
                case Building_Bed _: owned = pawn.ownership.OwnedBed; break;
                case Building_Throne _: owned = pawn.ownership.AssignedThrone; break;
                case Building_Grave _: owned = pawn.ownership.AssignedGrave; break;
                default: return false;
            }
            previous = owned?.GetUniqueLoadID() ?? "";
            return true;
        }

        private static bool Valid(Operations.AssignIntent? intent) => intent != null
            && intent.HasPawnId && ProtoBoundary.IsIdentifier(intent.PawnId) && intent.HasThingId && ProtoBoundary.IsIdentifier(intent.ThingId)
            && intent.PawnId != intent.ThingId && intent.ExpectedPrevious != null
            && intent.ExpectedPrevious.ValueCase != Operations.Assignment.ValueOneofCase.None
            && (intent.ExpectedPrevious.ValueCase != Operations.Assignment.ValueOneofCase.EntityId
                || (ProtoBoundary.IsIdentifier(intent.ExpectedPrevious.EntityId) && intent.ExpectedPrevious.EntityId != intent.ThingId));

        // Resolve returns null with assignable null when the pawn already
        // owns the thing: the intent holds and applies again.
        private static Common.Failure? Resolve(Operations.AssignIntent? intent, Common.ObservationContext context,
            out Pawn pawn, out ThingWithComps thing, out CompAssignableToPawn? assignable)
        {
            pawn = null!; thing = null!; assignable = null;
            if (!Valid(intent))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Assignment requires an exact pawn, thing and expected previous assignment.");
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Current map required.");
            // A hosted guest owns a bed like a colonist.
            var found = map.mapPawns.FreeColonistsSpawned.ById(intent!.PawnId)
                ?? map.mapPawns.AllPawnsSpawned.Where(NativeUpkeepFacts.HostedGuest).ById(intent.PawnId);
            var target = RefIndex.Thing<ThingWithComps>(map, intent!.ThingId);
            var comp = target?.GetComp<CompAssignableToPawn>();
            if (found != null && target != null && comp != null && comp.AssignedPawnsForReading.Contains(found))
            { pawn = found; thing = target; return null; }
            // Assigning interrupts no job, so the pawn's current order,
            // whoever gave it, is no reason to refuse.
            if (found == null || found.Dead || found.Downed || found.Drafted || found.InMentalState || found.ownership == null
                || found.health.HasHediffsNeedingTend())
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Pawn unavailable for assignment.");
            if (target == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Thing unavailable: not found on the map.");
            // A thing without CompAssignableToPawn is not assignable.
            if (comp == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Thing is not assignable to a pawn.");
            if (!TryPrevious(target, found, out var previousID))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Assignment of this kind of thing is not supported.");
            var expectPrevious = intent.ExpectedPrevious.ValueCase == Operations.Assignment.ValueOneofCase.EntityId ? intent.ExpectedPrevious.EntityId : "";
            if (previousID != expectPrevious)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Previous assignment changed; observe before recovery.");
            var refusal = target is Building_Bed bed ? BedRefusal(bed) : ThingRefusal(target, comp, found);
            if (refusal != null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, refusal);
            if (!comp.AssigningCandidates.Contains(found) || !comp.CanAssignTo(found).Accepted || comp.IdeoligionForbids(found))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native assignment eligibility refused.");
            pawn = found; thing = target; assignable = comp;
            return null;
        }

        // The ideology role the intent targets, when its thing id names one.
        private static Precept_Role? RoleTarget(Operations.AssignIntent? intent) => Valid(intent) ? IdeoRoleAssignment.Find(intent!.ThingId) : null;

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) =>
            RoleTarget(action.Assign) is Precept_Role role ? IdeoRoleAssignment.Validate(action.Assign, context, role) : Resolve(action.Assign, context, out _, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Assign;
            if (RoleTarget(intent) is Precept_Role role) return IdeoRoleAssignment.Apply(intent, context, role);
            var failure = Resolve(intent, context, out var pawn, out var thing, out var assignable);
            if (failure != null) throw new InvalidOperationException("Assignment prerequisites changed before apply: " + failure.Detail);
            if (assignable != null && thing is Building_Bed bed && intent.HasSwap && intent.Swap)
                foreach (var owner in bed.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
            assignable?.TryAssignPawn(pawn);
            var comp = thing.GetComp<CompAssignableToPawn>();
            if (comp == null || !comp.AssignedPawnsForReading.Contains(pawn)) throw new InvalidOperationException("Native assignment did not take effect.");
            var effect = new Receipts.AssignEffect { PawnId = pawn.GetUniqueLoadID(), ThingId = thing.GetUniqueLoadID(), Assigned = true };
            if (thing is Building_Bed assigned) effect.Sleeping = pawn.CurrentBed() == assigned;
            if (intent.ExpectedPrevious.ValueCase == Operations.Assignment.ValueOneofCase.EntityId) effect.PreviousThingId = intent.ExpectedPrevious.EntityId;
            return new Receipts.EffectEvidence { Assign = effect };
        }
    }
}
