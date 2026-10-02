#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The supply census's snapshot (Eligible, Snapshot) and the Allow/Forbid
    // half of DesignateIntent: one exact colony item's forbid state.
    internal static class NativeSupplyAllow
    {
        internal const string Kind = "Allow";
        internal static bool Eligible(Thing thing) => thing != null && !thing.Destroyed
            && thing.Spawned && ProtoBoundary.IsLoaded(thing.Map) && thing.def.EverHaulable
            && thing.def.category == ThingCategory.Item && !thing.Position.Fogged(thing.Map)
            && Faction.OfPlayerSilentFail != null
            && (thing.Faction == null || thing.Faction == Faction.OfPlayerSilentFail)
            && thing.TryGetComp<CompForbiddable>() != null;

        internal static string Token(Common.Identity identity, string id, string definition,
            int x, int z, int count, bool forbidden, string faction)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(id); writer.Write(definition); writer.Write(x); writer.Write(z);
                    writer.Write(count); writer.Write(forbidden); writer.Write(faction);
                }
                using (var hash = SHA256.Create())
                    return "allow-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, thing.GetUniqueLoadID(), thing.def.defName,
                    thing.Position.x, thing.Position.z, thing.stackCount, thing.IsForbidden(Faction.OfPlayer),
                    thing.Faction?.GetUniqueLoadID() ?? "") };
        }

        internal static bool Wants(Operations.DesignateIntent? intent) => intent != null && intent.HasDesignation
            && (intent.Designation == Operations.ThingDesignation.Allow || intent.Designation == Operations.ThingDesignation.Forbid);

        // The apply-time precondition list for Allow/Forbid
        // (action-contracts.md), one rule at a time; an item already in the
        // wanted state applies again.
        private static ApplyPreconditions Rules(Operations.DesignateIntent intent, Map map, out Thing? thing)
        {
            Thing? found = null;
            var forbid = intent.Designation == Operations.ThingDesignation.Forbid;
            Designator designator = forbid ? (Designator)new Designator_Forbid() : new Designator_Unforbid();
            var rules = new ApplyPreconditions(Kind)
                .Require(() => intent.HasThingId && ProtoBoundary.IsIdentifier(intent.ThingId), "Allow/Forbid requires an exact item")
                .Present(() => (found = RefIndex.Thing(map, intent.ThingId)) != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact item is no longer spawned on this map")
                .Require(() => found!.def.EverHaulable && found.def.category == ThingCategory.Item && found.TryGetComp<CompForbiddable>() != null, "the item is not a forbiddable haulable item")
                .Require(() => !found!.Position.Fogged(found.Map), "the item's cell is fogged")
                .Require(() => Faction.OfPlayerSilentFail != null && (found!.Faction == null || found.Faction == Faction.OfPlayerSilentFail), "the item belongs to another faction")
                .Require(() => found!.IsForbidden(Faction.OfPlayer) == forbid || designator.CanDesignateThing(found).Accepted, "the native forbid designator refuses the item");
            thing = found;
            return rules;
        }

        internal static Common.Failure? Validate(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var rules = Rules(intent, ProtoBoundary.LoadedMap(context), out _);
            return rules.Holds ? null : rules.Failure();
        }

        internal static Receipts.EffectEvidence Apply(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var rules = Rules(intent, ProtoBoundary.LoadedMap(context), out var thing);
            if (!rules.Holds) throw new InvalidOperationException("Allow/Forbid prerequisites changed before apply: " + rules.Reason);
            var forbid = intent.Designation == Operations.ThingDesignation.Forbid;
            if (thing!.IsForbidden(Faction.OfPlayer) != forbid)
                (forbid ? (Designator)new Designator_Forbid() : new Designator_Unforbid()).DesignateThing(thing);
            if (thing!.IsForbidden(Faction.OfPlayer) != forbid) throw new InvalidOperationException("Native supply designation did not achieve its forbid state.");
            return new Receipts.EffectEvidence { Designation = new Receipts.DesignationEffect { ThingId = thing!.GetUniqueLoadID(),
                DesignationDef = forbid ? "Forbid" : "Allow", Present = true,
                ResourceDef = thing!.def.defName, Cell = new Common.Cell { X = thing!.Position.x, Z = thing!.Position.z } } };
        }

    }
}
