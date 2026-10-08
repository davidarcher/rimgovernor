#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // A rectangle drag, with vanilla's stockpile cell rules and one zone per
    // cardinal component. Existing zones are excluded: creating a store does
    // not select, expand, adopt or reconfigure the player's zones.
    internal sealed class StockpilePlacementActionHandler : IActionHandler
    {
        internal static bool Valid(Operations.ZoneIntent? intent)
        {
            var r = intent?.AddCells?.Rectangle;
            return intent != null && intent.HasKind && intent.Kind == Operations.ZoneType.Stockpile
                && intent.Zone == null && intent.RemoveCells == null && !intent.Delete && intent.Growing == null && intent.Fishing == null
                && intent.HasLabel && ProtoBoundary.IsIdentifier(intent.Label) && !intent.RequireCoveredEmpty
                && intent.Stockpile != null && intent.Stockpile.HasPriority && NativeStockpileSettings.Valid(intent.Stockpile)
                && r != null && r.Origin != null && r.Origin.HasX && r.Origin.HasZ && r.Origin.X >= 0 && r.Origin.Z >= 0
                && r.HasWidth && r.HasHeight && r.Width > 0 && r.Height > 0 && (long)r.Width * r.Height <= 4096
                && (long)r.Origin.X + r.Width <= int.MaxValue && (long)r.Origin.Z + r.Height <= int.MaxValue;
        }

        private static List<IntVec3[]> Components(Operations.ZoneIntent intent, Map map)
        {
            var r = intent.AddCells.Rectangle;
            var remaining = new HashSet<IntVec3>();
            for (int x = 0; x < r.Width; x++) for (int z = 0; z < r.Height; z++)
            {
                var c = new IntVec3(r.Origin.X + x, 0, r.Origin.Z + z);
                // Designator_ZoneAddStockpile.CanDesignateCell: the shared
                // zoneability rule plus passable terrain, on this map.
                if (Designator_ZoneAdd.IsZoneableCell(c, map).Accepted && c.GetTerrain(map).passability != Traversability.Impassable
                    && map.zoneManager.ZoneAt(c) == null && !map.zoneManager.AllZones.Any(zone => zone.Cells.Contains(c))) remaining.Add(c);
            }
            var components = new List<IntVec3[]>();
            while (remaining.Count > 0)
            {
                var seed = remaining.OrderBy(c => c.x).ThenBy(c => c.z).First();
                var queue = new Queue<IntVec3>();
                var cells = new List<IntVec3>();
                remaining.Remove(seed); queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var c = queue.Dequeue(); cells.Add(c);
                    foreach (var d in GenAdj.CardinalDirections) if (remaining.Remove(c + d)) queue.Enqueue(c + d);
                }
                components.Add(cells.OrderBy(c => c.x).ThenBy(c => c.z).ToArray());
            }
            return components;
        }

        internal static Operations.ZonePreviewReply Preview(Operations.ZoneIntent intent, Common.ObservationContext context)
        {
            if (!Valid(intent) || NativeStockpileSettings.Resolve(intent.Stockpile, StockpileFilter.StorableDefs(null)) == null)
                return new Operations.ZonePreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stockpile placement requires a rectangle and resolvable settings.") };
            var components = Components(intent, ProtoBoundary.LoadedMap(context));
            var result = new Operations.ZonePreview { Context = context.Clone(), Accepted = components.Count > 0 };
            if (components.Count == 0) result.Reason = "Rectangle has no free vanilla-zoneable stockpile cells.";
            foreach (var component in components)
            {
                var cells = new Operations.CellList();
                cells.Cells.Add(component.Select(c => new Common.Cell { X = c.x, Z = c.z }));
                result.Components.Add(cells);
            }
            return new Operations.ZonePreviewReply { Evaluated = result };
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            if (!Valid(action.Zone)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stockpile placement requires a rectangle and settings.");
            var map = ProtoBoundary.LoadedMap(context);
            if (NativeStockpileSettings.Resolve(action.Zone.Stockpile, StockpileFilter.StorableDefs(null)) == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stockpile settings cannot resolve.");
            return Components(action.Zone, map).Count == 0
                ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Rectangle has no free vanilla-zoneable stockpile cells.") : null;
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var components = Components(action.Zone, map);
            var resolved = NativeStockpileSettings.Resolve(action.Zone.Stockpile, StockpileFilter.StorableDefs(null))
                ?? throw new InvalidOperationException("Stockpile settings stopped resolving.");
            var effect = new Receipts.ZoneEffect();
            foreach (var cells in components)
            {
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone); zone.label = action.Zone.Label;
                foreach (var c in cells) zone.AddCell(c);
                zone.settings.Priority = resolved.Priority!.Value;
                NativeStockpileSettings.Apply(zone.settings.filter, resolved, StockpileFilter.ParentFilter(zone), StockpileFilter.StorableDefs(zone));
                zone.slotGroup.RemoveHaulDesignationOnStoredThings();
                var created = new Receipts.CreatedZone { ZoneId = zone.GetUniqueLoadID() };
                created.Cells.Add(zone.Cells.OrderBy(c => c.x).ThenBy(c => c.z).Select(c => new Common.Cell { X = c.x, Z = c.z }));
                effect.Created.Add(created);
            }
            return new Receipts.EffectEvidence { Zone = effect };
        }
    }
}
