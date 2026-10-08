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
    internal static class NativeHackDesignationOperations
    {
        internal static bool ValidShape(Operations.HackDesignationIntent? c) => c != null && c.Target != null
            && ProtoBoundary.IsIdentifier(c.Target.Id) && c.HasEnabled;
        private static Common.Failure? Resolve(Operations.HackDesignationIntent c, Common.ObservationContext context, out CompHackable? hack, out Command_Toggle? toggle)
        {
            hack = null; toggle = null;
            if (!ValidShape(c)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An exact hack target and enabled state are required.");
            var map = ProtoBoundary.ResolveMap(context);
            var thing = map == null ? null : RefIndex.Thing(map, c.Target.Id);
            hack = thing?.TryGetComp<CompHackable>();
            if (thing == null || !thing.Spawned || thing.Destroyed || thing.Position.Fogged(map) || hack == null || hack.Props.onlyRemotelyHackable)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The exact native autohack target is unavailable.");
            if (c.Enabled && (hack.IsHacked || hack.LockedOut || !NativeQuestHackGift.EligibleHackers(thing).Any()))
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "No normal research worker can hack this target.");
            toggle = hack.CompGetGizmosExtra().OfType<Command_Toggle>().SingleOrDefault();
            if (toggle?.toggleAction == null) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The native autohack toggle is unavailable.");
            return null;
        }
        internal static Common.Failure? Validate(Operations.HackDesignationIntent c, Common.ObservationContext context) => Resolve(c, context, out _, out _);
        internal static Receipts.EffectEvidence Apply(Operations.HackDesignationIntent c, Common.ObservationContext context)
        {
            var failure = Resolve(c, context, out var hack, out var toggle);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            if (hack!.Autohack != c.Enabled) toggle!.toggleAction();
            if (hack.Autohack != c.Enabled) throw new InvalidOperationException("Native autohack state did not change.");
            return new Receipts.EffectEvidence { HackDesignation = new Receipts.HackDesignationEffect { TargetId = c.Target.Id, Enabled = hack.Autohack } };
        }
    }
    internal sealed class HackDesignationActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeHackDesignationOperations.Validate(action.HackDesignation, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeHackDesignationOperations.Apply(action.HackDesignation, context);
    }
}
