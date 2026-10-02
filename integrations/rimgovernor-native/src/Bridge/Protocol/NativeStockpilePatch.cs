#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeStockpilePatch
    {
        // StorageEligible is a spawned, loaded storage building the player
        // owns with storage settings: the building listing reports its
        // storage token as the row's settings snapshot.
        internal static bool StorageEligible(Thing thing) => thing is Building_Storage storage && !storage.Destroyed && storage.Spawned
            && ProtoBoundary.IsLoaded(storage.Map) && storage.Faction != null && storage.Faction == Faction.OfPlayerSilentFail
            && storage.GetStoreSettings()?.filter != null;

        // StorageToken covers exactly what a stockpile intent writes: priority
        // and the filter signature, the same contribution the per-zone token
        // makes.
        internal static Obs.SnapshotRef StorageToken(Building_Storage storage, Common.ObservationContext context) =>
            NativeObservationSnapshot.Snapshot("storage", context, storage.GetUniqueLoadID(), w => {
                var settings = storage.GetStoreSettings();
                w.Write((int)settings.Priority);
                NativeStockpileSettings.WriteSignature(w, settings.filter);
            });

        // Settings is the storage building's listing-row settings: its
        // storage token and, since it is by definition the player's, the
        // faction reading.
        internal static Obs.BuildingSettings Settings(Building_Storage storage, Common.ObservationContext context) =>
            new Obs.BuildingSettings { Snapshot = StorageToken(storage, context), PlayerOwned = true };

        internal static IStoreSettingsParent? Resolve(Map map, string id)
        {
            if (RefIndex.Zone(map, id) is Zone_Stockpile zone) return zone;
            var thing = RefIndex.Thing(map, id);
            return thing != null && StorageEligible(thing) ? (Building_Storage)thing : null;
        }
    }

    /// <summary>
    /// StockpileIntent on Actions/Apply: replaces the priority and/or patches
    /// the filter of one existing storage target, a stockpile zone or a
    /// player storage building (Building_Storage, e.g. Shelf). Absent parts of
    /// the body preserve the live setting; the intent is refused outright when
    /// any selector fails to resolve, so a body never applies partially.
    /// Cells are never touched (ZoneCellsIntent owns them). Settings that
    /// already hold apply again.
    /// </summary>
    internal sealed class StockpileActionHandler : IActionHandler
    {
        private const string Kind = "Stockpile patch";

        private sealed class Target
        {
            internal IStoreSettingsParent? Parent;
            internal NativeStockpileSettings.Resolved? Body;
        }

        private static bool Valid(Operations.StockpileIntent? intent) =>
            intent != null && intent.HasTargetId && ProtoBoundary.IsIdentifier(intent.TargetId) && NativeStockpileSettings.Valid(intent.Settings);

        private static Target Resolve(Operations.StockpileIntent? intent, Map map)
        {
            var target = new Target();
            if (!Valid(intent)) return target;
            target.Parent = NativeStockpilePatch.Resolve(map, intent!.TargetId);
            if (target.Parent?.GetStoreSettings()?.filter != null)
                target.Body = NativeStockpileSettings.Resolve(intent.Settings, StockpileFilter.StorableDefs(target.Parent));
            return target;
        }

        private static bool Standing(Target target) => target.Parent != null && target.Body != null && NativeStockpileSettings.Matches(target.Parent, target.Body);

        // The apply-time precondition list for stockpile settings
        // (action-contracts.md), one rule at a time.
        private static ApplyPreconditions Rules(Operations.StockpileIntent? intent, Target target) => new ApplyPreconditions(Kind)
            .Require(() => Valid(intent), "a patch requires a target id and a valid settings body")
            .Present(() => target.Parent != null, "the exact stockpile zone or storage building no longer exists on this map")
            .Require(() => target.Parent!.GetStoreSettings()?.filter != null, "the target has no storage settings")
            .Require(() => target.Body != null, "the settings body does not resolve against the target's storable definitions");

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var target = Resolve(action.Stockpile, ProtoBoundary.LoadedMap(context));
            if (Standing(target)) return null;
            var rules = Rules(action.Stockpile, target);
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var target = Resolve(action.Stockpile, map);
            if (!Standing(target))
            {
                var rules = Rules(action.Stockpile, target);
                if (!rules.Holds) throw new InvalidOperationException("Stockpile patch prerequisites changed before apply: " + rules.Reason);
                var settings = target.Parent!.GetStoreSettings();
                if (target.Body!.Priority.HasValue) settings.Priority = target.Body.Priority.Value;
                NativeStockpileSettings.Apply(settings.filter, target.Body, StockpileFilter.ParentFilter(target.Parent), StockpileFilter.StorableDefs(target.Parent));
                if (!Standing(target)) throw new InvalidOperationException("Native stockpile settings readback did not apply.");
            }
            if (target.Parent is Building_Storage storage)
                return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect { Snapshot = new Receipts.SnapshotEvidence {
                    EntityId = storage.GetUniqueLoadID(), AfterToken = NativeStockpilePatch.StorageToken(storage, context).Token } } };
            return NativeZoneCreation.Evidence(action.Stockpile.TargetId, target.Parent as Zone, map);
        }
    }
}
