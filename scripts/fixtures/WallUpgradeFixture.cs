using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class WallUpgradeFixture
    {
        [Tool("test/wall_material_loss", Description = "Remove or restore declared stone stock in a disposable wall scenario. Tests demolition safety under material loss; not production acceptance.")]
        public async Task<object> Materials(IRimBridgeContext ctx, CancellationToken cancellationToken, string material, int restore = 0)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var def = DefDatabase<ThingDef>.GetNamed(material);
                if (def.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) != true || restore < 0 || restore > 1000)
                    return new { success = false, error = "Expected bounded stone material input" };
                var stock = map.listerThings.ThingsOfDef(def).ToList();
                var count = stock.Sum(t => t.stackCount);
                if (restore == 0) foreach (var thing in stock) thing.Destroy();
                else {
                    var pawn = map.mapPawns.FreeColonistsSpawned.First();
                    for (int remaining = restore; remaining > 0;) {
                        var thing = ThingMaker.MakeThing(def); thing.stackCount = Math.Min(remaining, def.stackLimit);
                        remaining -= thing.stackCount;
                        GenPlace.TryPlaceThing(thing, pawn.Position, map, ThingPlaceMode.Near);
                        thing.SetForbidden(false, false);
                    }
                }
                return new { success = true, before = count, after = map.listerThings.ThingsOfDef(def).Sum(t => t.stackCount) };
            }, cancellationToken).ConfigureAwait(false);
        [Tool("test/stone_walls_spawn", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: stand finished player stone Walls of one stuff at exact empty cells (\"x,z;x,z\"), standing in for completed backup or permanent walls so guarded demolition can be exercised without construction time.")]
        public async Task<object> SpawnWalls(IRimBridgeContext ctx, CancellationToken cancellationToken, string cells, string stuff)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var def = DefDatabase<ThingDef>.GetNamedSilentFail(stuff ?? "");
                if (map == null || def?.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) != true || !GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Contains(def))
                    return new { success = false, error = "A current map and a stony Wall stuff are required" };
                var targets = (cells ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split(',')).ToList();
                if (targets.Count == 0 || targets.Count > 8 || targets.Any(p => p.Length != 2)) return new { success = false, error = "Expected 1..8 x,z cells" };
                var parsed = targets.Select(p => new IntVec3(int.Parse(p[0]), 0, int.Parse(p[1]))).ToList();
                if (parsed.Any(c => !c.InBounds(map) || c.GetEdifice(map) != null || c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame)))
                    return new { success = false, error = "Every cell must be in bounds and free of buildings" };
                var ids = new System.Collections.Generic.List<string>();
                foreach (var cell in parsed) {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, def);
                    wall.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(wall, cell, map);
                    ids.Add(wall.GetUniqueLoadID());
                }
                return new { success = true, walls = ids, stuff = def.defName };
            }, cancellationToken).ConfigureAwait(false);
    }
}
