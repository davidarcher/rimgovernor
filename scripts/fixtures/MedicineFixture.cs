using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class MedicineFixture
    {
        [Tool("test/medicine_setup", Description = "Prepare a disposable medicine shortage and mature native wild medicine plants. Does not harvest or create medicine.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState).ToList();
                foreach (var medicine in map.listerThings.AllThings.Where(t => t.def.IsMedicine).ToList()) medicine.Destroy();
                var def = DefDatabase<ThingDef>.AllDefsListForReading.First(d => d.plant != null
                    && d.plant.harvestedThingDef == ThingDefOf.MedicineHerbal && !d.plant.Sowable);
                var cells = GenRadial.RadialCellsAround(people.First().Position, 15, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null
                    && map.zoneManager.ZoneAt(c) == null && map.fertilityGrid.FertilityAt(c) >= def.plant.fertilityMin).Take(40).ToList();
                foreach (var cell in cells) {
                    cell.GetPlant(map)?.Destroy();
                    var plant = (Plant)ThingMaker.MakeThing(def);
                    plant.Growth = 1f;
                    GenSpawn.Spawn(plant, cell, map);
                }
                foreach (var p in people) {
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                    p.needs.rest.CurLevelPercentage = .95f;
                    p.needs.food.CurLevelPercentage = .95f;
                    for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, TimeAssignmentDefOf.Anything);
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting)) {
                        p.workSettings.SetPriority(WorkTypeDefOf.PlantCutting, 1);
                        p.skills.GetSkill(SkillDefOf.Plants).Level = 20;
                    }
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                return new { success = true, plant = def.defName, count = cells.Count,
                    growMinSkill = def.plant.sowMinSkill, harvestYield = def.plant.harvestYield };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
