#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeStockpilePatch
    {
        // StorageEligible is a spawned, loaded storage building the player
        // owns with storage settings.
        internal static bool StorageEligible(Thing thing) => thing is Building_Storage storage && !storage.Destroyed && storage.Spawned
            && ProtoBoundary.IsLoaded(storage.Map) && storage.Faction != null && storage.Faction == Faction.OfPlayerSilentFail
            && storage.GetStoreSettings()?.filter != null;

        internal static IStoreSettingsParent? Resolve(Map map, string id)
        {
            if (RefIndex.Zone(map, id) is Zone_Stockpile zone) return zone;
            var thing = RefIndex.Thing(map, id);
            return thing != null && StorageEligible(thing) ? (Building_Storage)thing : null;
        }
    }

    /// <summary>
    /// The zone intent's settings shape: replaces the priority and/or patches
    /// the filter of one existing storage target, a stockpile zone or a
    /// player storage building (Building_Storage, e.g. Shelf). Absent parts of
    /// the body preserve the live setting; the intent is refused outright when
    /// any selector fails to resolve, so a body never applies partially.
    /// Cells are never touched (the cells shape owns them). Settings that
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

        private static bool Valid(Operations.ZoneIntent? intent) =>
            NativeZoneIntent.Id(intent) != null && intent!.Kind == Operations.ZoneType.Existing && intent.AddCells == null && intent.RemoveCells == null && !intent.Delete && intent.Growing == null && intent.Fishing == null && intent.Label is null && NativeStockpileSettings.Valid(intent.Stockpile);

        private static Target Resolve(Operations.ZoneIntent? intent, Map map)
        {
            var target = new Target();
            if (!Valid(intent)) return target;
            target.Parent = NativeStockpilePatch.Resolve(map, (intent!.Zone?.Id ?? ""));
            if (target.Parent?.GetStoreSettings()?.filter != null)
                target.Body = NativeStockpileSettings.Resolve(intent.Stockpile, StockpileFilter.StorableDefs(target.Parent));
            return target;
        }

        private static bool Standing(Target target) => target.Parent != null && target.Body != null && NativeStockpileSettings.Matches(target.Parent, target.Body);

        // The apply-time precondition list for stockpile settings
        // (action-contracts.md), one rule at a time.
        private static ApplyPreconditions Rules(Operations.ZoneIntent? intent, Target target) => new ApplyPreconditions(Kind)
            .Require(() => Valid(intent), "a patch requires a target id and a valid settings body")
            .Present(() => target.Parent != null, "the exact stockpile zone or storage building no longer exists on this map")
            .Require(() => target.Parent!.GetStoreSettings()?.filter != null, "the target has no storage settings")
            .Require(() => target.Body != null, "the settings body does not resolve against the target's storable definitions");

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var target = Resolve(action.Intent?.Zone, ProtoBoundary.LoadedMap(context));
            if (Standing(target)) return null;
            var rules = Rules(action.Intent?.Zone, target);
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var target = Resolve(action.Intent?.Zone, map);
            if (!Standing(target))
            {
                var rules = Rules(action.Intent?.Zone, target);
                if (!rules.Holds) throw new InvalidOperationException("Stockpile patch prerequisites changed before apply: " + rules.Reason);
                var settings = target.Parent!.GetStoreSettings();
                if (target.Body!.Priority.HasValue) settings.Priority = target.Body.Priority.Value;
                NativeStockpileSettings.Apply(settings.filter, target.Body, StockpileFilter.ParentFilter(target.Parent), StockpileFilter.StorableDefs(target.Parent));
                if (!Standing(target)) throw new InvalidOperationException("Native stockpile settings readback did not apply.");
            }
            if (target.Parent is Building_Storage)
                return new Receipts.EffectEvidence { Effect = new Receipts.SettingsEffect() };
            return NativeZoneCreation.Evidence(action.Intent?.Zone?.Zone?.Id ?? "", target.Parent as Zone, map);
        }
    }
}
