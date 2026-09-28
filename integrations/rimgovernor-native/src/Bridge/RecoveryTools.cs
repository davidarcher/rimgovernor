#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class HomeRecoveryTools
    {
        internal static object Census()
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, error = "No map" };
            var conditions = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
            return new { success = true, tick = Find.TickManager.TicksGame, mapId = map.uniqueID,
                roofHazard = conditions.Any(c => c is GameCondition_ToxicFallout),
                areas = map.areaManager.AllAreas.OfType<Area_Allowed>().Where(a => a.TrueCount > 0
                    && a.ActiveCells.All(c => c.Roofed(map) && !c.Fogged(map))).Select(a => new { id = a.ID, label = a.Label, cells = a.TrueCount }).ToList(),
                restrictions = map.mapPawns.FreeColonistsSpawned.Select(p => new { pawn = p.GetUniqueLoadID(),
                    area = p.playerSettings?.AreaRestrictionInPawnCurrentMap?.ID }).ToList(),
                buildings = map.listerBuildings.allBuildingsColonist.Where(b => !b.Position.Fogged(map))
                    .OrderBy(b => b.thingIDNumber).Select(b => new {
                        thingId = b.GetUniqueLoadID(), defName = b.def.defName, position = BridgeCommon.Pos(b.Position),
                        hitPoints = b.HitPoints, maxHitPoints = b.MaxHitPoints, usesHitPoints = b.def.useHitPoints,
                        broken = b.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false,
                        fuel = b.TryGetComp<CompRefuelable>()?.Fuel,
                        fuelTarget = b.TryGetComp<CompRefuelable>()?.TargetFuelLevel,
                        fuelDefs = b.TryGetComp<CompRefuelable>()?.Props.fuelFilter.AllowedThingDefs.Select(d => d.defName).ToList(),
                        powerOn = b.TryGetComp<CompPowerTrader>() == null ? (bool?)null : b.TryGetComp<CompPowerTrader>().PowerOn,
                        powerConsumer = b.TryGetComp<CompPowerTrader>()?.Props.PowerConsumption > 0,
                        switchedOn = b.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true,
                        forbidden = b.IsForbidden(Faction.OfPlayer), burning = b.IsBurning()
                    }).ToList() };
        }
    }
}
