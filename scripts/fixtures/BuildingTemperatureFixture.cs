using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Spawns one unpowered Heater near an
    // existing colonist so temperatureaccept can exercise the BuildingPatchIntent
    // target-temperature CAS path (NativeBuildingTemperature.cs) without
    // depending on native random building generation or an actual power
    // network -- CompTempControl.targetTemperature is settable and readable
    // regardless of power state.
    public sealed class BuildingTemperatureFixture
    {
        [Tool("test/building_temperature_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: spawn one unpowered Heater with CompTempControl near an existing colonist on the current paused map.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                if (colonist == null) return Refuse("At least one existing colonist is required.");
                var heaterDef = DefDatabase<ThingDef>.GetNamed("Heater");
                var cell = GenAdjFast.AdjacentCells8Way(colonist.Position)
                    .FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null);
                if (cell == default) return Refuse("No open adjacent cell for the fixture heater.");
                var heater = ThingMaker.MakeThing(heaterDef, GenStuff.DefaultStuffFor(heaterDef));
                heater.SetFaction(player);
                GenSpawn.Spawn(heater, cell, map);
                var comp = heater.TryGetComp<CompTempControl>();
                if (comp == null) return Refuse("Fixture heater unexpectedly has no CompTempControl.");
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    thingId = heater.GetUniqueLoadID(), initialTargetTemperature = comp.targetTemperature,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }

    // The EnvironmentCold/EnvironmentHot probe (#3328): the first colonist
    // (lowest thing id) sits sealed in a doorless, roofed 3x3 room, and every
    // call puts its ambient temperature `deficit` degrees past the pawn's own
    // ComfyTemperatureMin (cold) or ComfyTemperatureMax (hot), or mid-band for
    // a deficit of 0. The lab's outdoor pin is moved to the same temperature,
    // so wall equalization holds the room steady. With Ideology the colony
    // ideoligion carries Temperature_Tough exactly when `tough` is set.
    public sealed class EnvironmentTemperatureFixture
    {
        private const int Box = 5;

        [Tool("test/environment_temperature", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture (#3328): seal the first colonist in a doorless roofed room and hold its ambient temperature `deficit` past its comfy band (cold or hot side), or mid-band at 0, through the lab outdoor pin; Temperature_Tough on or off under Ideology. Test builds only.")]
        public async Task<object> Hold(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "cold (below ComfyTemperatureMin) or hot (above ComfyTemperatureMax).")] string side = "cold",
            [ToolParameter(Description = "Degrees past the comfy bound; 0 holds the middle of the band.")] float deficit = 0f,
            [ToolParameter(Description = "Give the colony ideoligion Temperature_Tough (true) or strip it (false); true needs Ideology.")] bool tough = false)
            => await ctx.MainThread.InvokeAsync<object>(() =>
            {
                if (side != "cold" && side != "hot") throw new System.ArgumentException("side must be cold or hot.");
                if (deficit < 0f) throw new System.ArgumentOutOfRangeException(nameof(deficit));
                var map = Find.CurrentMap ?? throw new System.InvalidOperationException("A loaded map is required.");
                if (float.IsNaN(AcceptanceWorld.Lab)) throw new System.InvalidOperationException("A lab start (pinned outdoor temperature) is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
                var room = pawn.GetRoom();
                var built = false;
                if (room == null || room.UsesOutdoorTemperature || room.OpenRoofCount != 0 || room.TouchesMapEdge || room.CellCount != (Box - 2) * (Box - 2))
                {
                    room = Seal(map, pawn);
                    built = true;
                }

                var ideology = ModsConfig.IdeologyActive && pawn.Ideo != null;
                if (tough && !ideology) throw new System.InvalidOperationException("Temperature_Tough needs Ideology and a colony ideoligion.");
                if (ideology)
                {
                    var issue = PreceptDefOf.Temperature_Tough.issue;
                    foreach (var precept in pawn.Ideo.PreceptsListForReading.Where(p => p.def.issue == issue).ToList()) pawn.Ideo.RemovePrecept(precept);
                    if (tough) pawn.Ideo.AddPrecept(PreceptMaker.MakePrecept(PreceptDefOf.Temperature_Tough), true);
                }

                // Hypothermia and heatstroke would down the colonist over the hottest and coldest steps; neither feeds the thought.
                var cleared = pawn.health.hediffSet.hediffs.Where(h => h.def == HediffDefOf.Hypothermia || h.def == HediffDefOf.Heatstroke).ToList();
                foreach (var h in cleared) pawn.health.RemoveHediff(h);

                var comfyMin = pawn.GetStatValue(StatDefOf.ComfyTemperatureMin);
                var comfyMax = pawn.GetStatValue(StatDefOf.ComfyTemperatureMax);
                var target = deficit == 0f ? (comfyMin + comfyMax) / 2f : side == "cold" ? comfyMin - deficit : comfyMax + deficit;
                AcceptanceWorld.SetLab(target);
                if (System.Math.Abs(map.mapTemperature.OutdoorTemp - target) > 0.01f)
                    throw new System.InvalidOperationException($"The lab outdoor pin did not take: outdoor {map.mapTemperature.OutdoorTemp:F2}, wanted {target:F2}.");
                room.Temperature = target;
                pawn.needs?.mood?.thoughts?.situational?.Notify_SituationalThoughtsDirty();
                var ambient = pawn.AmbientTemperature;
                return new { success = true, tick = Find.TickManager.TicksGame, pawn = pawn.GetUniqueLoadID(), side, deficit, built,
                    room = room.ID, cells = room.CellCount, comfyMin, comfyMax, target, ambient,
                    observedDeficit = side == "cold" ? comfyMin - ambient : ambient - comfyMax,
                    ideology, tough = ideology && pawn.Ideo.HasPrecept(PreceptDefOf.Temperature_Tough),
                    precepts = ideology ? pawn.Ideo.PreceptsListForReading.Select(p => p.def.defName).ToList() : new System.Collections.Generic.List<string>(),
                    traits = pawn.story.traits.allTraits.Select(t => t.def.defName).ToList(),
                    cleared = cleared.Select(h => h.def.defName).ToList() };
            }, cancellationToken);

        // A doorless Box x Box ring of wood walls round the pawn, roofed, the
        // ground inside cleared and the pawn on its middle cell.
        private static Room Seal(Map map, Pawn pawn)
        {
            var player = Faction.OfPlayer;
            var rect = CellRect.CenteredOn(pawn.Position, Box / 2);
            if (!rect.ExpandedBy(1).InBounds(map)) throw new System.InvalidOperationException($"No room for the fixture box round {pawn.Position}.");
            foreach (var c in rect.Cells)
            {
                foreach (var t in c.GetThingList(map).Where(t => t != pawn && !(t is Pawn)).ToList()) t.Destroy(DestroyMode.Vanish);
                if (rect.IsOnEdge(c))
                {
                    if (c.GetFirstPawn(map) is Pawn other && other != pawn) throw new System.InvalidOperationException($"{other} stands on the fixture wall line.");
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                    wall.SetFaction(player);
                    GenSpawn.Spawn(wall, c, map);
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            pawn.jobs?.StopAll();
            pawn.Position = rect.CenterCell;
            pawn.Notify_Teleported(true, true);
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var room = pawn.GetRoom();
            if (room == null || room.UsesOutdoorTemperature || room.OpenRoofCount != 0 || room.CellCount != (Box - 2) * (Box - 2))
                throw new System.InvalidOperationException($"The fixture box is not a sealed roofed room: room={room?.ID} outdoor={room?.UsesOutdoorTemperature} openRoof={room?.OpenRoofCount} cells={room?.CellCount}");
            return room;
        }
    }
}
