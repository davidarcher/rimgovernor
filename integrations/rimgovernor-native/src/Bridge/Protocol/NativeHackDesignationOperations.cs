using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeHackDesignationOperations
    {
        internal static bool ValidShape(RimGovernor.Brain.GameOrders.Operations.HackDesignationIntent? c) => c != null && c.Target != null
            && GameThread.IsIdentifier(c.Target.Id);
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.HackDesignationIntent c, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out CompHackable? hack, out Command_Toggle? toggle)
        {
            hack = null; toggle = null;
            if (!ValidShape(c)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "An exact hack target and enabled state are required.");
            var map = GameThread.ResolveMap(context);
            var thing = map == null ? null : RefIndex.Thing(map, c.Target?.Id ?? "");
            hack = thing?.TryGetComp<CompHackable>();
            if (thing == null || !thing.Spawned || thing.Destroyed || thing.Position.Fogged(map) || hack == null || hack.Props.onlyRemotelyHackable)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The exact native autohack target is unavailable.");
            if (c.Enabled && (hack.IsHacked || hack.LockedOut || !NativeQuestHackGift.EligibleHackers(thing).Any()))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "No normal research worker can hack this target.");
            toggle = hack.CompGetGizmosExtra().OfType<Command_Toggle>().SingleOrDefault();
            if (toggle?.toggleAction == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The native autohack toggle is unavailable.");
            return null;
        }
        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.HackDesignationIntent c, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(c, context, out _, out _);
        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.HackDesignationIntent c, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(c, context, out var hack, out var toggle);
            if (failure != null) throw new ApplyRefusedException(failure);
            if ((hack ?? throw new System.InvalidOperationException("Required native value is unavailable: hack.")).Autohack != c.Enabled) (toggle ?? throw new System.InvalidOperationException("Required native value is unavailable: toggle.")).toggleAction();
            if (hack.Autohack != c.Enabled) throw new InvalidOperationException("Native autohack state did not change.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.HackDesignationEffect { TargetId = c.Target?.Id ?? "", Enabled = hack.Autohack } };
        }
    }
    internal sealed class HackDesignationActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeHackDesignationOperations.Validate(action.Intent?.HackDesignation ?? throw new InvalidOperationException("Hack intent missing"), context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeHackDesignationOperations.Apply(action.Intent?.HackDesignation ?? throw new InvalidOperationException("Hack intent missing"), context);
    }
}
