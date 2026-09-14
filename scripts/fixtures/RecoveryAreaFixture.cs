using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds one small roofed refuge
    // Area and one outdoor Area near an existing colonist, restricts that
    // colonist to the outdoor area, and registers a ToxicFallout hazard
    // condition on the current paused map, so recoveryareaaccept can
    // exercise NativeWorkSettings' PatchPawn AllowedArea dispatch (the
    // native write surface RecoveryAreaProposal ultimately reaches) without
    // depending on native random colony layout or an existing named safe
    // area, mirroring RecoveryServiceFixture's/DisasterFixture's own
    // minimal disposable-spawn pattern.
    public sealed class RecoveryAreaFixture
    {
        [Tool("test/recovery_area_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one small roofed refuge Area and one outdoor Area near an existing colonist, restrict that colonist to the outdoor area, and register a ToxicFallout hazard condition on the current paused map.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var pawn = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && p.Faction == player && !p.Dead && !p.Downed
                    && !p.Drafted && !p.InMentalState && p.workSettings != null && p.workSettings.Initialized && p.workSettings.EverWork)
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("No existing colonist eligible for an allowed-area assignment.");
                var outdoorCell = pawn.Position;
                if (!outdoorCell.InBounds(map) || outdoorCell.Fogged(map) || outdoorCell.Roofed(map))
                    return Refuse("Fixture colonist is not standing on an open, unroofed cell.");

                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                if (wallDef == null || !wallDef.MadeFromStuff || !GenStuff.AllowedStuffsFor(wallDef).Contains(ThingDefOf.WoodLog))
                    return Refuse("Wall def unavailable or WoodLog is not an allowed stuff in this ruleset.");
                var origin = GenRadial.RadialCellsAround(pawn.Position, 20, true).FirstOrDefault(c =>
                    !new CellRect(c.x, c.z, 5, 5).Contains(outdoorCell)
                    && new CellRect(c.x, c.z, 5, 5).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.Standable(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                    && pawn.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable area for the fixture refuge.");

                for (var x = 0; x < 5; x++) for (var z = 0; z < 5; z++)
                {
                    var c = new IntVec3(origin.x + x, 0, origin.z + z);
                    if (x == 0 || z == 0 || x == 4 || z == 4)
                    {
                        var wall = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                        wall.SetFaction(player);
                        GenSpawn.Spawn(wall, c, map);
                    }
                    else map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    map.areaManager.Home[c] = true;
                }
                if (!map.areaManager.TryMakeNewAllowed(out var refuge)) return Refuse("Could not allocate the fixture refuge area.");
                refuge.SetLabel("RimGovernor fixture refuge");
                for (var x = 1; x < 4; x++) for (var z = 1; z < 4; z++)
                    refuge[new IntVec3(origin.x + x, 0, origin.z + z)] = true;
                if (refuge.TrueCount == 0) return Refuse("Fixture refuge area unexpectedly has no cells.");

                if (!map.areaManager.TryMakeNewAllowed(out var outdoor)) return Refuse("Could not allocate the fixture outdoor area.");
                outdoor.SetLabel("RimGovernor fixture outdoor");
                outdoor[outdoorCell] = true;
                if (outdoor.TrueCount == 0) return Refuse("Fixture outdoor area unexpectedly has no cells.");

                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = outdoor;
                if (pawn.playerSettings.AreaRestrictionInPawnCurrentMap?.GetUniqueLoadID() != outdoor.GetUniqueLoadID())
                    return Refuse("Fixture colonist restriction did not take effect.");

                var toxic = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("ToxicFallout"), 1200);
                map.gameConditionManager.RegisterCondition(toxic);

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, pawn = pawn.GetUniqueLoadID(),
                    refuge = refuge.GetUniqueLoadID(), outdoor = outdoor.GetUniqueLoadID(),
                    setup = "Test-only roofed refuge/outdoor area, hazard registration and restriction setup; native PatchPawn AllowedArea dispatch remains native.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
