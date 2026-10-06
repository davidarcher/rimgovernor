using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (issue #2288). One op, three actions,
    // for the non-food field cases (farm/cotton-field, farm/healroot-field):
    //
    //   prepare -- empties the shortage the case names: every loose item of
    //              the product's resource (and, for cotton, every spawned
    //              fabric and apparel stack the clothing floor reads; for
    //              healroot, every medicine), every wild plant that harvests
    //              the product, and any growing zone. Raises Growing and the
    //              Plants skill so the colonists can sow and harvest. Sows,
    //              harvests and orders nothing: the planner must open the field.
    //   census  -- the growing zones of `plant` (cells, sown, mature) and the
    //              loose units of `product` on the map.
    //   mature  -- sets every sown `plant` to full growth so the harvest does
    //              not wait out the crop's grow days.
    public sealed class NonFoodFieldFixture
    {
        [Tool("test/nonfood_field", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: action prepare|census|mature for the non-food field cases (plant = sowable crop def, product = what it harvests).")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "census", string plant = "Plant_Cotton", string product = "Cloth")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || Faction.OfPlayerSilentFail == null) return Refuse("A disposable colony map is required.");
                var plantDef = DefDatabase<ThingDef>.GetNamedSilentFail(plant);
                var productDef = DefDatabase<ThingDef>.GetNamedSilentFail(product);
                if (plantDef?.plant == null || productDef == null || plantDef.plant.harvestedThingDef != productDef || !plantDef.plant.Sowable)
                    return Refuse("plant must be a sowable crop harvesting product: " + plant + " / " + product);
                switch (action)
                {
                    case "prepare": return Prepare(map, plantDef, productDef);
                    case "mature": return Mature(map, plantDef);
                    case "census": return Census(map, plantDef, productDef);
                    default: return Refuse("unknown action " + action);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Prepare(Map map, ThingDef plantDef, ThingDef productDef)
        {
            if (!Find.TickManager.Paused) return Refuse("A paused disposable colony map is required.");
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).ToList();
            var sowers = people.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)).ToList();
            if (sowers.Count == 0) return Refuse("No colonist able to sow.");
            if (!PlantUtility.GrowthSeasonNow(map, plantDef)) return Refuse("Not a growth season for " + plantDef.defName + ".");

            int items = 0;
            foreach (var thing in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && Clears(t, productDef)).ToList())
            {
                thing.Destroy();
                items++;
            }
            int wild = 0;
            foreach (var wildPlant in map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.plant != null && p.def.plant.harvestedThingDef == productDef && !p.def.plant.Sowable).ToList())
            {
                wildPlant.Destroy();
                wild++;
            }
            foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Growing>().ToList()) zone.Delete();

            foreach (var p in people)
            {
                p.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                p.needs.rest.CurLevelPercentage = .95f;
                p.needs.food.CurLevelPercentage = .95f;
                for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, TimeAssignmentDefOf.Anything);
                foreach (var work in new[] { WorkTypeDefOf.Growing, WorkTypeDefOf.PlantCutting, WorkTypeDefOf.Hauling })
                    if (!p.WorkTypeIsDisabled(work)) p.workSettings.SetPriority(work, 1);
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)) p.skills.GetSkill(SkillDefOf.Plants).Level = 20;
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            var identity = Current.Game.GetComponent<ColonyIdentity>();
            return new {
                success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                tick = Find.TickManager.TicksGame, colonists = people.Count, sowers = sowers.Count,
                destroyedItems = items, destroyedWildPlants = wild,
                plant = plantDef.defName, product = productDef.defName,
                sowMinSkill = plantDef.plant.sowMinSkill, harvestYield = plantDef.plant.harvestYield, growDays = plantDef.plant.growDays,
            };
        }

        // Clears is what the shortage empties: the product itself, and for the
        // clothing floor every stuff of the product's category and every loose
        // apparel (a stored outfit shrinks the floor); for a medicine herb every
        // medicine.
        private static bool Clears(Thing t, ThingDef productDef)
        {
            if (t.def == productDef) return true;
            if (productDef.stuffProps != null && t.def.stuffProps != null && t.def.stuffProps.categories.Any(c => productDef.stuffProps.categories.Contains(c))) return true;
            if (productDef.stuffProps != null && t is Apparel) return true;
            return productDef.IsMedicine && t.def.IsMedicine;
        }

        private static object Mature(Map map, ThingDef plantDef)
        {
            int grown = 0;
            foreach (var plant in map.listerThings.AllThings.OfType<Plant>().Where(p => p.def == plantDef && p.Spawned && p.Position.GetZone(map) is Zone_Growing).ToList())
            {
                plant.Growth = 1f;
                grown++;
            }
            return new { success = true, tick = Find.TickManager.TicksGame, grown };
        }

        private static object Census(Map map, ThingDef plantDef, ThingDef productDef)
        {
            var zones = map.zoneManager.AllZones.OfType<Zone_Growing>().Select(zone => {
                var def = zone.GetPlantDefToGrow();
                var sown = zone.Cells.Select(c => c.GetPlant(map)).Where(p => p != null && p.def == def).ToList();
                return new { id = zone.ID, plant = def?.defName, cells = zone.Cells.Count, sown = sown.Count, mature = sown.Count(p => p.LifeStage == PlantLifeStage.Mature) };
            }).ToList();
            int units = map.listerThings.AllThings.Where(t => t.def == productDef && t.Spawned).Sum(t => t.stackCount);
            int wild = map.listerThings.AllThings.OfType<Plant>().Count(p => p.def.plant != null && p.def.plant.harvestedThingDef == productDef && !p.def.plant.Sowable);
            return new { success = true, tick = Find.TickManager.TicksGame, zones, productUnits = units, wildPlants = wild };
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
