using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class AnimalFeedFixture
    {
        [Tool("test/feed_setup", Description = "Prepare disposable pet, butcher spot, ingredients and a restricted feeding area. No feed or production bill is created.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var people = map.mapPawns.FreeColonistsSpawned.ToList();
                foreach (var p in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == Faction.OfPlayerSilentFail).ToList()) p.Destroy();
                var center = GenRadial.RadialCellsAround(people.First().Position, 30, true).First(c =>
                    CellRect.CenteredOn(c, 4).Cells.All(v => v.InBounds(map) && !v.Fogged(map)
                        && v.Standable(map) && v.GetEdifice(map) == null && map.zoneManager.ZoneAt(v) == null));
                foreach (var c in CellRect.CenteredOn(center, 4))
                    foreach (var t in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) t.Destroy();
                var bench = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ButcherSpot"));
                bench.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(bench, center, map);
                if (!map.areaManager.TryMakeNewAllowed(out var area)) throw new System.InvalidOperationException("No fixture allowed-area slot");
                foreach (var c in CellRect.CenteredOn(center, 3)) area[c] = true;
                var pet = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Husky"), Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(pet, center, map);
                pet.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                pet.needs.food.CurLevelPercentage = .2f;
                foreach (var def in new[] { DefDatabase<ThingDef>.GetNamed("Hay"), DefDatabase<ThingDef>.GetNamed("Meat_Muffalo") }) {
                    for (int i = 0; i < 12; i++) {
                        var stock = ThingMaker.MakeThing(def);
                        stock.stackCount = def.stackLimit;
                        GenPlace.TryPlaceThing(stock, center + new IntVec3(6, 0, 0), map, ThingPlaceMode.Near);
                        stock.SetForbidden(false, false);
                    }
                }
                foreach (var p in people) {
                    p.needs.rest.CurLevelPercentage = .95f;
                    p.needs.food.CurLevelPercentage = .95f;
                    for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, TimeAssignmentDefOf.Anything);
                    var cooking = DefDatabase<WorkTypeDef>.GetNamed("Cooking");
                    if (!p.WorkTypeIsDisabled(cooking)) {
                        p.workSettings.SetPriority(cooking, 1);
                        p.skills.GetSkill(SkillDefOf.Cooking).Level = 20;
                    }
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                return new { success = true, pet = pet.GetUniqueLoadID(), bench = bench.GetUniqueLoadID(),
                    food = pet.needs.food.CurLevelPercentage, area = area.ID };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
