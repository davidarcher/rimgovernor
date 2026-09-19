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
        [Tool("test/feed_setup", Description = "Prepare disposable pet, a butcher spot inside its restricted roofed feeding area, an earlier butcher spot outside it, and ingredients outside it. No feed or production bill is created.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Spawn the butcher spot inside the pet's area; false leaves only the one outside it (#311).", DefaultValue = true)] bool benchInside = true)
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
                // The decoy spawns first so it carries the lower thing id: a
                // bench choice by id alone lands the kibble bill outside the
                // pet's area (#237).
                var outsideCell = GenRadial.RadialCellsAround(center, 12, false).First(c => c.DistanceTo(center) >= 8
                    && c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null);
                var outside = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ButcherSpot"));
                outside.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(outside, outsideCell, map);
                Thing bench = null;
                if (benchInside) {
                    bench = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ButcherSpot"));
                    bench.SetFaction(Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(bench, center, map);
                }
                if (!map.areaManager.TryMakeNewAllowed(out var area)) throw new System.InvalidOperationException("No fixture allowed-area slot");
                // The area is roofed off one corner wall so a feed stockpile
                // (which needs covered ground) can be zoned inside it (#311).
                var post = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                post.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(post, center + new IntVec3(3, 0, 3), map);
                foreach (var c in CellRect.CenteredOn(center, 3)) { area[c] = true; map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed); }
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
                return new { success = true, pet = pet.GetUniqueLoadID(), bench = bench?.GetUniqueLoadID() ?? "",
                    outsideBench = outside.GetUniqueLoadID(), food = pet.needs.food.CurLevelPercentage, area = area.ID,
                    areaCells = CellRect.CenteredOn(center, 3).Cells.Select(c => new { x = c.x, z = c.z }).ToList() };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
