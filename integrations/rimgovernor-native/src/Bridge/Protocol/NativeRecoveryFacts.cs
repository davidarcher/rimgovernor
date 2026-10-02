#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeRecoveryFacts
    {
        // Pure census: reading recovery never acquires or repairs area leases.
        internal static Obs.RecoveryReply Read(Map map, Common.ObservationContext context)
        {
            try {
                var buildings = map.listerBuildings.allBuildingsColonist.Where(b => !b.Position.Fogged(map)).OrderBy(b => b.thingIDNumber).ToList();
                var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                var result = new Obs.RecoverySnapshot { Context = context };
                foreach (var building in buildings) {
                    var service = new Obs.BuildingServiceState { BrokenDown = building.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false };
                    var fuel = building.TryGetComp<CompRefuelable>();
                    if (fuel != null) {
                        service.Fuel = Number(fuel.Fuel); service.TargetFuel = Number(fuel.TargetFuelLevel);
                        var defs = fuel.Props.fuelFilter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal).ToList();
                        service.AllowedFuelDefs.Add(defs);
                    } else service.Issues.Add(new Obs.ReadIssue { Field = "fuel", Unavailable = new Common.Unavailable {
                        Reason = Common.UnavailableReason.NotApplicable, Detail = "Building has no refuelable component." } });
                    var row = new Obs.BuildingState { Building = Entity(building), UsesHitPoints = building.def.useHitPoints,
                        Burning = building.IsBurning(), Service = service, Settings = new Obs.BuildingSettings { Forbidden = building.IsForbidden(Faction.OfPlayer) } };
                    if (building.def.useHitPoints) { row.HitPoints = building.HitPoints; row.MaxHitPoints = building.MaxHitPoints; }
                    result.Buildings.Add(row);
                }
                foreach (var pawn in pawns) {
                    var row = new Obs.RecoveryRestriction { Pawn = Entity(pawn) };
                    var area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
                    if (area != null) row.AreaId = area.GetUniqueLoadID();
                    result.Restrictions.Add(row);
                }
                return new Obs.RecoveryReply { Observed = result };
            } catch (Exception) {
                return new Obs.RecoveryReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = "Complete bounded recovery census is unavailable." } };
            }
        }
        private static double Number(double value) { if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new InvalidOperationException("Invalid service quantity."); return value; }
        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
        private static Obs.EntityRef Entity(Thing t) => new Obs.EntityRef { Id = t.GetUniqueLoadID(), DefName = t.def.defName, MapId = t.Map.uniqueID, Position = Cell(t.Position) };
    }
}
