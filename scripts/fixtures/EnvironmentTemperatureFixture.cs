using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools
{
    // The EnvironmentCold/EnvironmentHot probe (#3328) and the hypothermia
    // growth probe (#2797): the first colonist
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
                if (room == null || room.UsesOutdoorTemperature || room.OpenRoofCount != 0 || room.TouchesMapEdge || room.CellCount != (Box - 2) * (Box - 2))
                {
                    room = Seal(map, pawn);
                }

                var ideo = ModsConfig.IdeologyActive ? pawn.Ideo : null;
                var ideology = ideo != null;
                if (tough && !ideology) throw new System.InvalidOperationException("Temperature_Tough needs Ideology and a colony ideoligion.");
                if (ideo != null)
                {
                    var issue = PreceptDefOf.Temperature_Tough.issue;
                    foreach (var precept in ideo.PreceptsListForReading.Where(p => p.def.issue == issue).ToList()) ideo.RemovePrecept(precept);
                    if (tough) ideo.AddPrecept(PreceptMaker.MakePrecept(PreceptDefOf.Temperature_Tough), true);
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
                return FixtureKit.Reply(new Fx.EnvironmentTemperatureHeld { Pawn = pawn.GetUniqueLoadID(), Ideology = ideology,
                    Tough = ideo != null && ideo.HasPrecept(PreceptDefOf.Temperature_Tough),
                    Precepts = ideo != null ? ideo.PreceptsListForReading.Select(p => p.def.defName).ToList() : new System.Collections.Generic.List<string>(),
                    Traits = pawn.story.traits.allTraits.Select(t => t.def.defName).ToList() });
            }, cancellationToken);

        [Tool("test/hypothermia_tuning", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture (#2797): the Verse.HealthTuning hypothermia constants and the first colonist's comfortable-to-safe minimum offset, the onset HediffGiver_Hypothermia applies. Test builds only.")]
        public async Task<object> Tuning(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var map = Find.CurrentMap ?? throw new System.InvalidOperationException("A loaded map is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
                return FixtureKit.Reply(new Fx.HypothermiaTuningRead
                {
                    GrowthPerDegreeUnder = HealthTuning.HypothermiaGrowthPerDegreeUnder,
                    MinProgress = HealthTuning.MinHypothermiaProgress,
                    HarmfulOffsetC = HealthTuning.HarmfulTemperatureOffset,
                    SafeMinOffsetC = pawn.ComfortableTemperatureRange().min - pawn.SafeTemperatureRange().min,
                    IntervalTicks = HealthTuning.HediffGiverUpdateInterval,
                });
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
