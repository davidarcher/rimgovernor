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
    // The generic strip op (#1117): vanilla's Strip designation on one exact
    // spawned pawn or corpse (DesignateIntent with THING_DESIGNATION_STRIP).
    // The designation is the whole write; colonists strip through ordinary
    // Hauling work. Who to strip is decided in Go (#1079).
    internal static class NativeStrip
    {
        internal const string Kind = "Strip";
        internal const string DesignationDef = "Strip";

        internal static bool Designated(Thing thing) => thing.Map.designationManager.DesignationOn(thing, DesignationDefOf.Strip) != null;

        // The apply-time precondition list (action-contracts.md), one rule at
        // a time so a refusal names the fact that moved. Unlike CutPlant an
        // already designated target refuses (the issue's contract).
        private static ApplyPreconditions Rules(Operations.DesignateIntent intent, Map map, out Thing? target)
        {
            Thing? found = null;
            var rules = new ApplyPreconditions(Kind)
                .Require(() => intent.HasThingId && ProtoBoundary.IsIdentifier(intent.ThingId), "Strip requires an exact pawn or corpse")
                .Present(() => (found = map.listerThings.AllThings.FirstOrDefault(t => (t is Pawn || t is Corpse) && t.GetUniqueLoadID() == intent.ThingId)) != null
                    && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact pawn or corpse is not spawned on this map")
                .Require(() => !Designated(found!), "the target is already designated for stripping")
                .Require(() => found is IStrippable strippable && strippable.AnythingToStrip(), "the target has nothing to strip")
                .Require(() => new Designator_Strip().CanDesignateThing(found!).Accepted, "the native strip designator refuses the target");
            target = found;
            return rules;
        }

        internal static Common.Failure? Validate(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var rules = Rules(intent, ProtoBoundary.LoadedMap(context), out _);
            return rules.Holds ? null : rules.Failure();
        }

        internal static Receipts.EffectEvidence Apply(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var rules = Rules(intent, ProtoBoundary.LoadedMap(context), out var target);
            if (!rules.Holds) throw new InvalidOperationException("Strip prerequisites changed before apply: " + rules.Reason);
            new Designator_Strip().DesignateThing(target!);
            if (!Designated(target!)) throw new InvalidOperationException("Native Strip designation was not observed.");
            return new Receipts.EffectEvidence { Designation = new Receipts.DesignationEffect { ThingId = target!.GetUniqueLoadID(), DesignationDef = DesignationDef,
                Present = true, ResourceDef = target.def.defName, Cell = new Common.Cell { X = target.Position.x, Z = target.Position.z } } };
        }
    }
}
