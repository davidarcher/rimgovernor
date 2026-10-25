using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // The generic strip op: vanilla's Strip designation on one exact
    // spawned pawn or corpse (DesignateIntent with THING_DESIGNATION_STRIP).
    // The designation is the whole write; colonists strip through ordinary
    // Hauling work. Who to strip is decided by the brain.
    internal static class NativeStrip
    {
        internal const string Kind = "Strip";
        internal const string DesignationDef = "Strip";

        internal static bool Designated(Thing thing) => thing.Map.designationManager.DesignationOn(thing, DesignationDefOf.Strip) != null;

        // The apply-time precondition list (action-contracts.md), one rule at
        // a time so a refusal names the fact that moved. Unlike CutPlant an
        // already designated target refuses (the issue's contract).
        private static ApplyPreconditions Rules(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, Map map, out Thing? target)
        {
            Thing? found = null;
            var rules = new ApplyPreconditions(Kind)
                .Require(() => intent.ThingId is not null && GameThread.IsIdentifier(intent.ThingId), "Strip requires an exact pawn or corpse")
                .Present(() => (found = RefIndex.Thing(map, intent.ThingId) is Thing t && (t is Pawn || t is Corpse) ? t : null) != null
                    && !found.Destroyed && found.Spawned && GameThread.IsLoaded(found.Map), "the exact pawn or corpse is not spawned on this map")
                .Require(() => found != null && (!Designated(found)), "the target is already designated for stripping")
                .Require(() => found is IStrippable strippable && strippable.AnythingToStrip(), "the target has nothing to strip")
                .Require(() => new Designator_Strip().CanDesignateThing(found).Accepted, "the native strip designator refuses the target");
            target = found;
            return rules;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var rules = Rules(intent, GameThread.LoadedMap(context), out _);
            return rules.Holds ? null : rules.Failure();
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var rules = Rules(intent, GameThread.LoadedMap(context), out var target);
            if (!rules.Holds || target == null) throw new InvalidOperationException("Strip prerequisites changed before apply: " + rules.Reason);
            new Designator_Strip().DesignateThing(target);
            if (!Designated(target)) throw new InvalidOperationException("Native Strip designation was not observed.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.DesignationEffect { ThingId = target.GetUniqueLoadID(), DesignationDef = DesignationDef,
                Present = true } };
        }
    }
}
