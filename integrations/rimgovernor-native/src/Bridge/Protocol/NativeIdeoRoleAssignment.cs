#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The ideology role target of AssignIntent (#1661, epic #1653): thing_id
    // is the role precept's load id as the ideology section lists it, and the
    // pawn's previous assignment of the kind is the role it holds now (a pawn
    // holds at most one, Ideo.GetRole). Native checks live that the role is
    // active, the pawn is a free colonist of the same ideoligion, the role's
    // own requirements are met and a place is free; Precept_Role.Assign then
    // writes the holder, after unassigning the expected previous role. A pawn
    // that already holds the role applies again.
    internal static class IdeoRoleAssignment
    {
        /// <summary>The primary ideoligion's role precept the thing id names, or null (the id then names an ordinary thing).</summary>
        internal static Precept_Role? Find(string thingId)
        {
            if (!ModsConfig.IdeologyActive) return null;
            var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo;
            return ideo?.PreceptsListForReading.OfType<Precept_Role>().FirstOrDefault(r => r.GetUniqueLoadID() == thingId);
        }

        private static Common.Failure? Resolve(Operations.AssignIntent intent, Common.ObservationContext context, Precept_Role role, out Pawn pawn, out Precept_Role? previous)
        {
            pawn = null!; previous = null;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Current map required.");
            var found = map.mapPawns.FreeColonistsSpawned.ById(intent.PawnId);
            if (found == null || found.Dead) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Pawn unavailable for assignment.");
            if (role.IsAssigned(found)) { pawn = found; return null; }
            previous = role.ideo.GetRole(found);
            var held = previous?.GetUniqueLoadID() ?? "";
            var expected = intent.ExpectedPrevious.ValueCase == Operations.Assignment.ValueOneofCase.EntityId ? intent.ExpectedPrevious.EntityId : "";
            if (held != expected) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Previous assignment changed; observe before recovery.");
            if (!role.Active) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Role unavailable: not active.");
            if (found.Ideo != role.ideo) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Role unavailable: the pawn does not hold the ideoligion.");
            var unmet = role.GetFirstUnmetRequirement(found);
            if (unmet != null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Role unavailable: requirement unmet: " + unmet.GetLabel(role));
            if (role.ChosenPawns().Count() >= role.def.maxCount) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Role unavailable: no free place.");
            pawn = found;
            return null;
        }

        internal static Common.Failure? Validate(Operations.AssignIntent intent, Common.ObservationContext context, Precept_Role role) => Resolve(intent, context, role, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.AssignIntent intent, Common.ObservationContext context, Precept_Role role)
        {
            var failure = Resolve(intent, context, role, out var pawn, out var previous);
            if (failure != null) throw new InvalidOperationException("Assignment prerequisites changed before apply: " + failure.Detail);
            if (!role.IsAssigned(pawn))
            {
                previous?.Unassign(pawn, true);
                role.Assign(pawn, true);
            }
            if (!role.IsAssigned(pawn)) throw new InvalidOperationException("Native role assignment did not take effect.");
            var effect = new Receipts.AssignEffect { PawnId = pawn.GetUniqueLoadID(), ThingId = role.GetUniqueLoadID(), Assigned = true };
            if (intent.ExpectedPrevious.ValueCase == Operations.Assignment.ValueOneofCase.EntityId) effect.PreviousThingId = intent.ExpectedPrevious.EntityId;
            return new Receipts.EffectEvidence { Assign = effect };
        }
    }
}
