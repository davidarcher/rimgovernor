#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // A rectangle drag, with vanilla's stockpile cell rules and one zone per
    // cardinal component. Existing zones are excluded: creating a store does
    // not select, expand, adopt or reconfigure the player's zones.
    internal sealed class StockpilePlacementActionHandler : IActionHandler
    {
        internal static bool Valid([NotNullWhen(true)] Operations.ZoneIntent? intent)
        {
            var r = intent?.AddCells?.Selection?.Rectangle;
            return intent != null && intent.Kind is not null && intent.Kind.GetValueOrDefault() == Operations.ZoneType.Stockpile
                && intent.Zone == null && intent.RemoveCells == null && !intent.Delete.GetValueOrDefault() && intent.Growing == null && intent.Fishing == null
                && intent.Label is not null && ProtoBoundary.IsIdentifier(intent.Label) && !intent.RequireCoveredEmpty.GetValueOrDefault()
                && intent.Stockpile != null && intent.Stockpile.Priority is not null && NativeStockpileSettings.Valid(intent.Stockpile)
                && r != null && r.Origin is { X: >= 0, Z: >= 0 } origin
                && r.Width is > 0 && r.Height is > 0
                && (long)origin.X.GetValueOrDefault() + r.Width.GetValueOrDefault() <= int.MaxValue && (long)origin.Z.GetValueOrDefault() + r.Height.GetValueOrDefault() <= int.MaxValue;
        }

        private static List<IntVec3[]> Components(Operations.ZoneIntent intent, Map map)
        {
            var r = intent.AddCells?.Selection?.Rectangle ?? throw new InvalidOperationException("Stockpile placement has no rectangle.");
            int width = r.Width.GetValueOrDefault(), height = r.Height.GetValueOrDefault();
            int originX = r.Origin?.X.GetValueOrDefault() ?? 0, originZ = r.Origin?.Z.GetValueOrDefault() ?? 0;
            var remaining = new HashSet<IntVec3>();
            for (int x = 0; x < width; x++) for (int z = 0; z < height; z++)
            {
                var c = new IntVec3(originX + x, 0, originZ + z);
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
            if (!Valid(intent) || intent.Stockpile is not { } stockpile || NativeStockpileSettings.Resolve(stockpile, StockpileFilter.StorableDefs(null)) == null)
                return new Operations.ZonePreviewReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stockpile placement requires a rectangle and resolvable settings.") };
            var components = Components(intent, ProtoBoundary.LoadedMap(context));
            var result = new Operations.ZonePreview { Context = context, Accepted = components.Count > 0 };
            if (components.Count == 0) result = result with { Reason = "Rectangle has no free vanilla-zoneable stockpile cells." };
            foreach (var component in components)
            {
                var cells = new Operations.CellList();
                cells = cells with { Cells = [.. cells.Cells, .. component.Select(c => new Common.Cell { X = c.x, Z = c.z })] };
                result = result with { Components = [.. result.Components, cells] };
            }
            return new Operations.ZonePreviewReply { Outcome = result };
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var zoneIntent = action.Intent?.Zone;
            if (!Valid(zoneIntent) || zoneIntent.Stockpile is not { } stockpile) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stockpile placement requires a rectangle and settings.");
            var map = ProtoBoundary.LoadedMap(context);
            if (NativeStockpileSettings.Resolve(stockpile, StockpileFilter.StorableDefs(null)) == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stockpile settings cannot resolve.");
            return Components(zoneIntent, map).Count == 0
                ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Rectangle has no free vanilla-zoneable stockpile cells.") : null;
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var zoneIntent = action.Intent?.Zone ?? throw new InvalidOperationException("Action carries no zone intent.");
            var components = Components(zoneIntent, map);
            var resolved = NativeStockpileSettings.Resolve(zoneIntent.Stockpile ?? throw new InvalidOperationException("Stockpile settings are missing."), StockpileFilter.StorableDefs(null))
                ?? throw new InvalidOperationException("Stockpile settings stopped resolving.");
            var effect = new Receipts.ZoneEffect();
            foreach (var cells in components)
            {
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone); zone.label = zoneIntent.Label;
                foreach (var c in cells) zone.AddCell(c);
                zone.settings.Priority = resolved.Priority!.Value;
                NativeStockpileSettings.Apply(zone.settings.filter, resolved, StockpileFilter.ParentFilter(zone), StockpileFilter.StorableDefs(zone));
                zone.slotGroup.RemoveHaulDesignationOnStoredThings();
                var created = new Receipts.CreatedZone { ZoneId = zone.GetUniqueLoadID() };
                created = created with { Cells = [.. created.Cells, .. zone.Cells.OrderBy(c => c.x).ThenBy(c => c.z).Select(c => new Common.Cell { X = c.x, Z = c.z })] };
                effect = effect with { Created = [.. effect.Created, created] };
            }
            return new Receipts.EffectEvidence { Effect = effect };
        }
    }
}
