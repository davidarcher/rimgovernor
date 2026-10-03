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
    // The RoyaltyIntent arm of Actions/Apply (#1606, epic #1598): one royalty
    // write to a colonist's standing with a faction, a closed verb set so a
    // later verb (abdicate, reset permits) adds an arm here. "choose_permit"
    // spends permit points on one permit through Pawn_RoyaltyTracker.AddPermit
    // after the checks the game's own permit window (PermitsCardUtility)
    // applies: the permit belongs to the faction, the colonist's current title
    // reaches the permit's minimum, its prerequisite permit is held and the
    // faction's permit points cover its cost. A permit already held applies
    // again with nothing spent. An immediate write with no native job; the
    // postcondition reads back that the permit is held and, when this call
    // took it, that the points dropped by its cost.
    internal static class NativeRoyaltyIntent
    {
        private const string ChoosePermit = "choose_permit";

        private sealed class Target
        {
            public Pawn Pawn = null!;
            public Faction Faction = null!;
            public RoyalTitlePermitDef Permit = null!;
        }

        private static Common.Failure? Resolve(Operations.RoyaltyIntent? command, out Target? target)
        {
            target = null;
            if (command == null || !command.HasPawnId || !ProtoBoundary.IsIdentifier(command.PawnId) || !command.HasFactionDef || !ProtoBoundary.IsIdentifier(command.FactionDef) || !command.HasVerb)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A royalty command requires a pawn, a faction and a verb.");
            if (command.Verb != ChoosePermit)
                return ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Unsupported royalty verb.");
            if (!command.HasPermit || !ProtoBoundary.IsIdentifier(command.Permit))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Choosing a permit requires a permit def.");
            var pawn = PawnsFinder.AllMaps_FreeColonists.ById(command.PawnId);
            if (pawn == null || pawn.royalty == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact colonist with royalty is unavailable.");
            var faction = Verse.Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f?.def != null && f.def.defName == command.FactionDef);
            if (faction == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Faction is unavailable.");
            var permit = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail(command.Permit);
            if (permit == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Permit def is unavailable.");
            target = new Target { Pawn = pawn, Faction = faction, Permit = permit };
            var royalty = pawn.royalty;
            if (royalty.HasPermit(permit, faction)) return null;
            var title = royalty.GetCurrentTitle(faction);
            if (title == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The colonist holds no title with the faction.");
            if (permit.faction != null && permit.faction != faction.def)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The permit belongs to another faction.");
            if (permit.minTitle != null && title.seniority < permit.minTitle.seniority)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The colonist's title is below the permit's minimum.");
            if (permit.prerequisite != null && !royalty.HasPermit(permit.prerequisite, faction))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The permit's prerequisite is not held.");
            if (royalty.GetPermitPoints(faction) < permit.permitPointCost)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The colonist lacks the permit points.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Operations.RoyaltyIntent command, Target target, int pointsBefore) => new Receipts.EffectEvidence
        {
            Royalty = new Receipts.RoyaltyEffect
            {
                PawnId = command.PawnId, FactionDef = command.FactionDef, Verb = command.Verb, Permit = command.Permit,
                Held = target.Pawn.royalty.HasPermit(target.Permit, target.Faction),
                PermitPointsBefore = pointsBefore, PermitPoints = target.Pawn.royalty.GetPermitPoints(target.Faction)
            }
        };

        internal static Common.Failure? Validate(Operations.RoyaltyIntent command) => Resolve(command, out _);

        internal static Receipts.EffectEvidence Apply(Operations.RoyaltyIntent command)
        {
            var failure = Resolve(command, out var target);
            if (failure != null || target == null) throw new InvalidOperationException("Royalty write prerequisites changed before apply: " + failure?.Detail);
            var royalty = target.Pawn.royalty;
            var before = royalty.GetPermitPoints(target.Faction);
            var held = royalty.HasPermit(target.Permit, target.Faction);
            if (!held) royalty.AddPermit(target.Permit, target.Faction);
            var evidence = Evidence(command, target, before);
            if (!evidence.Royalty.Held) throw new InvalidOperationException("Native permit readback did not apply.");
            if (!held && evidence.Royalty.PermitPoints != before - target.Permit.permitPointCost)
                throw new InvalidOperationException("Native permit points did not drop by the permit's cost.");
            return evidence;
        }
    }

    internal sealed class RoyaltyActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeRoyaltyIntent.Validate(action.Royalty);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeRoyaltyIntent.Apply(action.Royalty);
    }
}
