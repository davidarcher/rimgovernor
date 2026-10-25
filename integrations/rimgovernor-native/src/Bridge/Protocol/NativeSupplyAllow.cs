using System;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // The supply census's snapshot (Eligible, Snapshot) and the Allow/Forbid
    // half of DesignateIntent: one exact colony item's forbid state.
    internal static class NativeSupplyAllow
    {
        internal const string Kind = "Allow";
        internal static bool Eligible(Thing thing) => thing != null && !thing.Destroyed
            && thing.Spawned && GameThread.IsLoaded(thing.Map) && thing.def.EverHaulable
            && thing.def.category == ThingCategory.Item && !thing.Position.Fogged(thing.Map)
            && Faction.OfPlayerSilentFail != null
            && (thing.Faction == null || thing.Faction == Faction.OfPlayerSilentFail)
            && thing.TryGetComp<CompForbiddable>() != null;

        internal static string Token(RimGovernor.Brain.GameFacts.Common.Identity identity, string id, string definition,
            int x, int z, int count, bool forbidden, string faction)
            => NativeSnapshotVersions.Token("allow", identity, id, writer => {
                writer.Write(definition); writer.Write(x); writer.Write(z);
                writer.Write(count); writer.Write(forbidden); writer.Write(faction);
            });

        internal static RimGovernor.Brain.GameFacts.Observations.SnapshotRef? Snapshot(Thing thing, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = context, EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, thing.GetUniqueLoadID(), thing.def.defName,
                    thing.Position.x, thing.Position.z, thing.stackCount, thing.IsForbidden(Faction.OfPlayer),
                    thing.Faction?.GetUniqueLoadID() ?? "") };
        }

        internal static bool Wants(RimGovernor.Brain.GameOrders.Operations.DesignateIntent? intent) => intent != null
            && (intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Allow || intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Forbid);

        // The apply-time precondition list for Allow/Forbid
        // (action-contracts.md), one rule at a time; an item already in the
        // wanted state applies again.
        private static ApplyPreconditions Rules(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, Map map, out Thing? thing)
        {
            Thing? found = null;
            var forbid = intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Forbid;
            Designator designator = forbid ? (Designator)new Designator_Forbid() : new Designator_Unforbid();
            var rules = new ApplyPreconditions(Kind)
                .Require(() => intent.ThingId is not null && GameThread.IsIdentifier(intent.ThingId), "Allow/Forbid requires an exact item")
                .Present(() => (found = RefIndex.Thing(map, intent.ThingId)) != null && !found.Destroyed && found.Spawned && GameThread.IsLoaded(found.Map), "the exact item is no longer spawned on this map")
                .Require(() => found != null && (found.def.EverHaulable && found.def.category == ThingCategory.Item && found.TryGetComp<CompForbiddable>() != null), "the item is not a forbiddable haulable item")
                .Require(() => found != null && (!found.Position.Fogged(found.Map)), "the item's cell is fogged")
                .Require(() => found != null && (Faction.OfPlayerSilentFail != null && (found.Faction == null || found.Faction == Faction.OfPlayerSilentFail)), "the item belongs to another faction")
                .Require(() => found.IsForbidden(Faction.OfPlayer) == forbid || designator.CanDesignateThing(found).Accepted, "the native forbid designator refuses the item");
            thing = found;
            return rules;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var rules = Rules(intent, GameThread.LoadedMap(context), out _);
            return rules.Holds ? null : rules.Failure();
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var rules = Rules(intent, GameThread.LoadedMap(context), out var thing);
            if (!rules.Holds) throw new InvalidOperationException("Allow/Forbid prerequisites changed before apply: " + rules.Reason);
            var forbid = intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Forbid;
            if (thing.IsForbidden(Faction.OfPlayer) != forbid)
                (forbid ? (Designator)new Designator_Forbid() : new Designator_Unforbid()).DesignateThing(thing);
            if (thing.IsForbidden(Faction.OfPlayer) != forbid) throw new InvalidOperationException("Native supply designation did not achieve its forbid state.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.DesignationEffect { ThingId = (thing ?? throw new System.InvalidOperationException("Required native value is unavailable: thing.")).GetUniqueLoadID(),
                DesignationDef = forbid ? "Forbid" : "Allow", Present = true } };
        }

    }
}
