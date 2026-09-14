using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Spawns one unpowered Heater near an
    // existing colonist so temperatureaccept can exercise the PatchBuilding
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
}
