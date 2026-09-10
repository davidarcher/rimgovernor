using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable prerequisites only. All asserted outcomes happen afterward through normal ticks.
    public sealed class HusbandryFixture
    {
        private static bool created;
        [Tool("test/husbandry_setup", Description = "Seed a disposable husbandry acceptance fixture; test builds only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (created) throw new InvalidOperationException("Fixture already created");
                var map = Find.CurrentMap;
                var handler = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Handling));
                var removed = map.mapPawns.FreeColonistsSpawned.Where(p => p != handler).ToArray();
                foreach (var other in removed) { other.jobs.StopAll(); other.DeSpawn(); }
                var origin = GenRadial.RadialCellsAround(handler.Position, 35, true).First(c =>
                    CellRect.FromLimits(c, c + new IntVec3(10, 0, 10)).Cells.All(p => p.InBounds(map)
                        && !p.Fogged(map) && p.Standable(map) && p.GetEdifice(map) == null
                        && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                Func<string, int, int, Thing> spawn = (name, x, z) => {
                    var def = ThingDef.Named(name);
                    var t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDef.Named("BlocksGranite") : null);
                    t.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(t, origin + new IntVec3(x, 0, z), map);
                    t.SetForbidden(false, false); return t;
                };
                for (var x = 0; x <= 10; x++) { spawn("Wall", x, 0); spawn("Wall", x, 10); }
                for (var z = 1; z < 10; z++) { spawn(z == 5 ? "Door" : "Wall", 0, z); spawn("Wall", 10, z); }
                var marker = DefDatabase<ThingDef>.AllDefsListForReading.Single(d =>
                    d.comps != null && d.comps.Any(c => c is CompProperties_AnimalPenMarker));
                spawn(marker.defName, 2, 2);
                foreach (var cell in CellRect.FromLimits(origin + new IntVec3(1, 0, 1), origin + new IntVec3(9, 0, 9)).Cells)
                {
                    cell.GetPlant(map)?.Destroy();
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                }
                var bed = (Building_Bed)spawn("Bed", 2, 5);
                handler.ownership.ClaimBedIfNonMedical(bed);
                for (var z = 2; z < 7; z++)
                {
                    var meal = ThingMaker.MakeThing(ThingDef.Named("MealSimple")); meal.stackCount = meal.def.stackLimit;
                    GenSpawn.Spawn(meal, origin + new IntVec3(7, 0, z), map); meal.SetForbidden(false, false);
                }
                handler.needs.food.CurLevelPercentage = 1;
                handler.needs.rest.CurLevelPercentage = 1;
                handler.needs.mood.CurLevelPercentage = 1;
                handler.needs.joy.CurLevelPercentage = 1;
                Func<string, Gender, int, Pawn> animal = (kind, gender, z) => {
                    var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named(kind), Faction.OfPlayer,
                        fixedGender: gender, fixedBiologicalAge: 4));
                    GenSpawn.Spawn(p, origin + new IntVec3(5, 0, z), map);
                    p.needs.food.CurLevelPercentage = .15f;
                    return p;
                };
                var mother = animal("Muffalo", Gender.Female, 3);
                var father = animal("Muffalo", Gender.Male, 4);
                var cow = animal("Cow", Gender.Female, 6);
                var dog = animal("Husky", Gender.Female, 7);
                var pregnancy = (Hediff_Pregnant)HediffMaker.MakeHediff(HediffDefOf.Pregnant, mother);
                mother.health.AddHediff(pregnancy); pregnancy.Severity = .999f;
                AccessTools.Field(typeof(CompHasGatherableBodyResource), "fullness").SetValue(mother.TryGetComp<CompShearable>(), 1f);
                AccessTools.Field(typeof(CompHasGatherableBodyResource), "fullness").SetValue(cow.TryGetComp<CompMilkable>(), 1f);
                var trainingSteps = (DefMap<TrainableDef, int>)AccessTools.Field(typeof(Pawn_TrainingTracker), "steps").GetValue(dog.training);
                trainingSteps[TrainableDefOf.Obedience] = TrainableDefOf.Obedience.steps - 1;
                for (var z = 2; z <= 8; z++)
                {
                    var hay = ThingMaker.MakeThing(ThingDef.Named("Hay")); hay.stackCount = hay.def.stackLimit;
                    GenSpawn.Spawn(hay, origin + new IntVec3(8, 0, z), map); hay.SetForbidden(false, false);
                }
                var kibble = ThingMaker.MakeThing(ThingDef.Named("Kibble")); kibble.stackCount = kibble.def.stackLimit;
                GenSpawn.Spawn(kibble, origin + new IntVec3(7, 0, 7), map); kibble.SetForbidden(false, false);
                handler.skills.GetSkill(SkillDefOf.Animals).Level = 20;
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                    if (!handler.WorkTypeIsDisabled(work)) handler.workSettings.SetPriority(work, work == WorkTypeDefOf.Handling ? 1 : 0);
                handler.Position = origin + new IntVec3(4, 0, 5);
                handler.jobs.StopAll();
                created = true;
                return new { success = true, mother = mother.GetUniqueLoadID(), father = father.GetUniqueLoadID(),
                    cow = cow.GetUniqueLoadID(), dog = dog.GetUniqueLoadID(), handler = handler.GetUniqueLoadID(),
                    removedColonists = removed.Select(p => p.GetUniqueLoadID()).ToArray(),
                    setup = "Single-handler fixture: other colonists despawned; roofed enclosure, bed, food and full initial handler needs. Mature full-producing animals, near-term pregnancy and one remaining training step. No completed outcome credited to setup; subsequent needs and work use normal rules." };
            }, cancellationToken);
        }
    }
}
