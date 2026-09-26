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
    // Private disposable acceptance only (#639, epic #638). The startup-labor
    // reproduction fixture: eight colonists with mixed construction skill, no
    // completed shelter, reachable building resources, a real animal-feed
    // deficit and scattered unstored supplies -- the startup state the
    // starvation diagnosis is read against. It establishes preconditions and
    // hands the colony back to the ordinary governor: it creates no plan, no
    // bill and no designation, and it never touches goal ranking.
    //
    // Three variants, so the same seed separates the causes:
    //   wood_sufficient -- enough unforbidden wood for the beds and a shell.
    //   wood_shortage   -- a token stack: the material is genuinely absent.
    //   bed_blocked     -- the same wood as wood_sufficient, forbidden, so an
    //                      admitted bed action can be seen to be held on
    //                      material it can see and cannot take.
    public sealed class StartupLaborFixture
    {
        // Enough for eight wooden beds (45 each) and a shell around them.
        private const int SufficientWood = 800;
        private const int ShortageWood = 25;
        // Construction skill per colonist, in thing-id order: mixed, and the
        // same every run on the same save.
        private static readonly int[] ConstructionSkills = { 0, 2, 3, 4, 5, 6, 8, 10 };

        [Tool("test/startup_labor_setup", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: establish the eight-colonist startup-labor precondition (no shelter, mixed construction skill, an animal-feed deficit, scattered supplies and a variant wood supply) and report it.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "wood_sufficient, wood_shortage or bed_blocked.", DefaultValue = "wood_sufficient")] string variant = "wood_sufficient")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (variant != "wood_sufficient" && variant != "wood_shortage" && variant != "bed_blocked")
                    return Refuse("Unknown variant '" + variant + "'; one of wood_sufficient, wood_shortage, bed_blocked.");
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count != ConstructionSkills.Length)
                    return Refuse("The fixture needs exactly " + ConstructionSkills.Length + " free colonists; this colony has " + colonists.Count + ".");

                // 1. No completed shelter: every player bed, sleeping spot,
                // wall and door goes, and the constructed roof with it.
                var razed = 0;
                foreach (var b in map.listerThings.AllThings.OfType<Building>().Where(b => b.Faction == player).ToList())
                {
                    if (!(b is Building_Bed) && b.def != ThingDefOf.Wall && b.def.defName != "Door" && b.def.defName != "SleepingSpot") continue;
                    var cells = b.OccupiedRect().Cells.ToList();
                    b.Destroy();
                    razed++;
                    foreach (var c in cells) if (c.InBounds(map) && map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed) map.roofGrid.SetRoof(c, null);
                }
                foreach (var f in map.listerThings.AllThings.OfType<Frame>().Where(f => f.Faction == player).ToList()) f.Destroy();
                foreach (var b in map.listerThings.AllThings.OfType<Blueprint>().Where(b => b.Faction == player).ToList()) b.Destroy();

                // 2. Mixed construction skill, work enabled, needs topped up
                // so the colony survives long enough to be watched building.
                var anchor = colonists[0].Position;
                var people = new List<object>();
                for (var i = 0; i < colonists.Count; i++)
                {
                    var p = colonists[i];
                    var level = ConstructionSkills[i];
                    var skill = p.skills?.GetSkill(SkillDefOf.Construction);
                    if (skill != null && !skill.TotallyDisabled) { skill.Level = level; skill.xpSinceLastLevel = 0f; }
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) p.workSettings.SetPriority(WorkTypeDefOf.Construction, 3);
                    if (p.needs?.food != null) p.needs.food.CurLevelPercentage = .9f;
                    if (p.needs?.rest != null) p.needs.rest.CurLevelPercentage = .9f;
                    for (var h = 0; h < 24; h++) p.timetable.SetAssignment(h, TimeAssignmentDefOf.Anything);
                    if (p.Drafted) p.drafter.Drafted = false;
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    people.Add(new {
                        id = p.GetUniqueLoadID(), name = p.Name?.ToStringFull ?? p.LabelShort,
                        construction = skill == null || skill.TotallyDisabled ? -1 : skill.Level,
                        constructionDisabled = skill == null || skill.TotallyDisabled,
                    });
                }

                // 3. Building resources: one reachable pile, per variant.
                foreach (var t in map.listerThings.AllThings.Where(t => t.def == ThingDefOf.WoodLog && t.Spawned).ToList()) t.Destroy();
                var woodCell = GenRadial.RadialCellsAround(anchor, 12, false).FirstOrDefault(c => c.InBounds(map) && !c.Fogged(map)
                    && c.Standable(map) && c.GetEdifice(map) == null && colonists[0].CanReach(c, PathEndMode.OnCell, Danger.Deadly));
                if (woodCell == default) return Refuse("No reachable cell for the fixture wood pile.");
                var wood = variant == "wood_shortage" ? ShortageWood : SufficientWood;
                var forbidden = variant == "bed_blocked";
                var placed = 0;
                while (placed < wood)
                {
                    var stack = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    stack.stackCount = System.Math.Min(ThingDefOf.WoodLog.stackLimit, wood - placed);
                    placed += stack.stackCount;
                    GenPlace.TryPlaceThing(stack, woodCell, map, ThingPlaceMode.Near);
                    stack.SetForbidden(forbidden, false);
                }
                var reachable = colonists.Count(p => p.CanReach(woodCell, PathEndMode.ClosestTouch, Danger.Deadly));

                // 4. A real animal-feed deficit: one hungry pet and no feed.
                foreach (var t in map.listerThings.AllThings.Where(t => t.Spawned
                    && (t.def.defName == "Hay" || t.def.defName == "Kibble")).ToList()) t.Destroy();
                var pet = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.RaceProps.Animal && p.Faction == player && !p.Dead);
                if (pet == null)
                {
                    pet = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Husky"), player);
                    GenSpawn.Spawn(pet, CellFinder.RandomClosewalkCellNear(anchor, map, 6), map);
                }
                if (pet.needs?.food != null) pet.needs.food.CurLevelPercentage = .25f;

                // 5. Scattered supplies: unstored stacks away from the colony,
                // which is what SecureSupplies is meant to see.
                var scattered = new List<object>();
                var scatterDefs = new[] { ThingDefOf.Steel, ThingDefOf.MedicineHerbal, ThingDefOf.WoodLog };
                var sites = GenRadial.RadialCellsAround(anchor, 40, false).Where(c => c.DistanceTo(anchor) >= 24
                    && c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && map.zoneManager.ZoneAt(c) == null && c.GetThingList(map).Count == 0).Take(scatterDefs.Length).ToList();
                if (sites.Count < scatterDefs.Length) return Refuse("No open ground for the scattered supply stacks.");
                for (var i = 0; i < scatterDefs.Length; i++)
                {
                    var stack = ThingMaker.MakeThing(scatterDefs[i]);
                    stack.stackCount = 40;
                    GenPlace.TryPlaceThing(stack, sites[i], map, ThingPlaceMode.Near);
                    stack.SetForbidden(false, false);
                    scattered.Add(new { def = scatterDefs[i].defName, count = stack.stackCount, x = sites[i].x, z = sites[i].z });
                }

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, variant, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    seed = Find.World?.info?.seedString ?? "", colonists = people, pawnCount = colonists.Count,
                    wood = placed, woodForbidden = forbidden, woodCell = new { x = woodCell.x, z = woodCell.z },
                    woodReachableBy = reachable, razedBuildings = razed,
                    beds = map.listerThings.AllThings.OfType<Building_Bed>().Count(b => b.Faction == player),
                    pet = pet.GetUniqueLoadID(), petFood = pet.needs?.food?.CurLevelPercentage ?? -1f,
                    feedStacks = map.listerThings.AllThings.Count(t => t.Spawned && (t.def.defName == "Hay" || t.def.defName == "Kibble")),
                    scattered,
                    setup = "Test-only startup precondition; ranking, planning and dispatch remain the governor's.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/startup_labor_read", Description = "Read the startup-labor precondition back: colonists and construction skill, wood on the map and whether it is forbidden, feed stacks, player beds and scattered stacks. Never writes.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || player == null) return Refuse("No colony map.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                var wood = map.listerThings.AllThings.Where(t => t.def == ThingDefOf.WoodLog && t.Spawned).ToList();
                return new {
                    success = true, tick = Find.TickManager.TicksGame,
                    pawnCount = colonists.Count,
                    colonists = colonists.Select(p => new {
                        id = p.GetUniqueLoadID(), name = p.Name?.ToStringFull ?? p.LabelShort,
                        construction = p.skills?.GetSkill(SkillDefOf.Construction)?.Level ?? -1,
                        job = p.CurJob?.def?.defName ?? "", drafted = p.Drafted, downed = p.Downed,
                    }).ToList(),
                    wood = wood.Sum(t => t.stackCount),
                    woodForbidden = wood.Count > 0 && wood.All(t => t.IsForbidden(player)),
                    feedStacks = map.listerThings.AllThings.Count(t => t.Spawned && (t.def.defName == "Hay" || t.def.defName == "Kibble")),
                    beds = map.listerThings.AllThings.OfType<Building_Bed>().Count(b => b.Faction == player),
                    animals = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == player && !p.Dead)
                        .Select(p => new { id = p.GetUniqueLoadID(), food = p.needs?.food?.CurLevelPercentage ?? -1f }).ToList(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
