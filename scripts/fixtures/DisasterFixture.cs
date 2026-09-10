using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable environmental inputs only; never included in production builds.
    public sealed class DisasterFixture
    {
        private static bool configured;

        [Tool("test/disaster_setup", Description = "Seed a powerless stove and bounded solar flare in a disposable test colony.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (configured) throw new InvalidOperationException("Fixture already configured");
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("Load a disposable colony first");
                var definition = DefDatabase<GameConditionDef>.GetNamed("SolarFlare");
                var eclipseDef = DefDatabase<GameConditionDef>.GetNamed("Eclipse");
                var stoveDef = DefDatabase<ThingDef>.GetNamed("ElectricStove");
                var anchor = map.mapPawns.FreeColonistsSpawned.First().Position;
                var cell = GenRadial.RadialCellsAround(anchor, 15, true).First(c =>
                    GenAdj.OccupiedRect(c, Rot4.North, stoveDef.size).Cells.All(p =>
                        p.InBounds(map) && !p.Fogged(map) && p.Standable(map)
                        && p.GetEdifice(map) == null
                        && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                configured = true;
                var stove = ThingMaker.MakeThing(stoveDef);
                stove.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(stove, cell, map);
                var flare = GameConditionMaker.MakeCondition(definition, 600);
                map.gameConditionManager.RegisterCondition(flare);
                var eclipse = GameConditionMaker.MakeCondition(eclipseDef, 600);
                eclipse.Permanent = true;
                map.gameConditionManager.RegisterCondition(eclipse);
                return new { success = true, stove = stove.ThingID, tick = Find.TickManager.TicksGame,
                    temporary = definition.defName, permanent = eclipseDef.defName,
                    setup = "Test-only spawned unpowered stove and registered native conditions. Pawn work and elapsed condition expiry remain native." };
            }, cancellationToken);
        }
    }
}
