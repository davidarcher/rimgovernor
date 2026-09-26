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
        [Tool("test/herd_removal", Description = "UNSAFE FOR MODEL EXECUTION. Set or read standing Manual removal flags on the husbandry fixture; disable handler labor while testing cancellation.")]
        public async Task<object> HerdRemoval(IRimBridgeContext ctx, CancellationToken cancellationToken, bool apply = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var animals = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer && p.RaceProps.Animal).ToList();
                var dog = animals.Single(p => p.def.defName == "Husky" && p.gender == Gender.Female);
                var cow = animals.Single(p => p.def.defName == "Cow");
                if (apply)
                {
                    foreach (var handler in map.mapPawns.FreeColonistsSpawned)
                        handler.workSettings.SetPriority(WorkTypeDefOf.Handling, 0);
                    map.designationManager.AddDesignation(new Designation(dog, DesignationDefOf.Slaughter));
                    map.designationManager.AddDesignation(new Designation(cow, DesignationDefOf.ReleaseAnimalToWild));
                }
                return new { success = true, dog = dog.GetUniqueLoadID(), cow = cow.GetUniqueLoadID(),
                    slaughter = map.designationManager.DesignationOn(dog, DesignationDefOf.Slaughter) != null,
                    release = map.designationManager.DesignationOn(cow, DesignationDefOf.ReleaseAnimalToWild) != null,
                    training = dog.training.GetWanted(TrainableDefOf.Obedience) };
            }, cancellationToken);
        }

        [Tool("test/milk_eggs_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable one-cow, four-hen product fixture with one ordinary handler, no fields or food stock, and ready production comps. Products must be laid and gathered by normal jobs afterwards.")]
        public async Task<object> MilkEggsPrepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            await Setup(ctx, cancellationToken);
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var cow = map.mapPawns.AllPawnsSpawned.Single(p => p.def.defName == "Cow");
                foreach (var animal in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p != cow).ToList())
                    animal.Destroy(DestroyMode.Vanish);
                foreach (var food in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible).ToList())
                    food.Destroy(DestroyMode.Vanish);
                foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Growing>().ToList()) zone.Delete();
                foreach (var plant in map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.plant?.harvestedThingDef?.IsNutritionGivingIngestible == true).ToList()) plant.Destroy(DestroyMode.Vanish);
                var hens = Enumerable.Range(0, 4).Select(i => {
                    var hen = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Chicken"), Faction.OfPlayer, fixedGender: Gender.Female, fixedBiologicalAge: 1));
                    GenSpawn.Spawn(hen, cow.Position + new IntVec3(i % 2, 0, 1 + i / 2), map);
                    AccessTools.Field(typeof(CompEggLayer), "eggProgress").SetValue(hen.GetComp<CompEggLayer>(), 1f);
                    hen.needs.food.CurLevelPercentage = 1;
                    return hen.GetUniqueLoadID();
                }).ToArray();
                cow.needs.food.CurLevelPercentage = 1;
                var handler = map.mapPawns.FreeColonistsSpawned.Single();
                for (var hour = 0; hour < 24; hour++) handler.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                return new { success = true, cow = cow.GetUniqueLoadID(), hens, handler = handler.GetUniqueLoadID() };
            }, cancellationToken);
        }

        // Per map, not per process: a kept game hosts one debug start after
        // another, and each new map may seed the fixture once.
        private static readonly System.WeakReference<Map> createdOn = new System.WeakReference<Map>(null);
        [Tool("test/husbandry_setup", Description = "Seed a disposable husbandry acceptance fixture; test builds only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (createdOn.TryGetTarget(out var seeded) && ReferenceEquals(seeded, map)) throw new InvalidOperationException("Fixture already created on this map");
                var handler = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Handling))
                    ?? throw new InvalidOperationException("husbandry fixture: no spawned colonist can do Handling");
                var removed = map.mapPawns.FreeColonistsSpawned.Where(p => p != handler).ToArray();
                foreach (var other in removed) { other.jobs.StopAll(); other.DeSpawn(); }
                // The herd is the fixture's alone: a debug start's own pet or
                // livestock sorts ahead of it by ID, takes the training request
                // and doubles the herd_removal lookups.
                foreach (var own in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == Faction.OfPlayer).ToList()) own.Destroy(DestroyMode.Vanish);
                // The debug-start map is random and a natural 11x11
                // heavy-affordance clearing is not guaranteed (#185). Take
                // the nearest 13x11 site (enclosure plus the column the wild
                // muffalo stands in) that is unfogged, dry, and free of pawns,
                // player buildings and work in progress, then level it: clear
                // rock, plants, items and filth and lay concrete so the walls
                // have their affordance whatever terrain the world generated.
                const int siteWidth = 13, siteHeight = 11;
                var site = map.AllCells.OrderBy(c => c.DistanceToSquared(handler.Position)).Select(c => new CellRect(c.x, c.z, siteWidth, siteHeight))
                    .FirstOrDefault(r => r.FullyContainedWithin(CellRect.WholeMap(map)) && r.Cells.All(p => !p.Fogged(map)
                        && !p.GetTerrain(map).IsWater && p.GetTerrain(map).passability != Traversability.Impassable
                        && p.GetZone(map) == null && p.GetEdifice(map)?.def.building.isNaturalRock != false
                        && !p.GetThingList(map).Any(t => t is Pawn || t is Blueprint || t is Frame
                            || t is Building b && !b.def.building.isNaturalRock)));
                if (site.IsEmpty) throw new InvalidOperationException("husbandry fixture: no 13x11 dry unfogged site free of pawns and buildings near " + handler.Position + "; reroll the world");
                foreach (var cell in site.Cells)
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t is Filth || t.def.category == ThingCategory.Item
                        || t is Building rock && rock.def.building.isNaturalRock).ToList()) thing.Destroy(DestroyMode.Vanish);
                    map.roofGrid.SetRoof(cell, null);
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                }
                var origin = site.Min;
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
                // A factionless muffalo just outside the enclosure: the tame
                // target. Muffalo wildness is below 1 so TameUtility.CanTame
                // accepts it; the handler's level-20 Animals skill clears the
                // minimum handling requirement.
                var wild = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Muffalo"), null,
                    fixedGender: Gender.Female, fixedBiologicalAge: 4));
                GenSpawn.Spawn(wild, origin + new IntVec3(12, 0, 5), map);
                var trainingSteps = (DefMap<TrainableDef, int>)AccessTools.Field(typeof(Pawn_TrainingTracker), "steps").GetValue(dog.training);
                trainingSteps[TrainableDefOf.Obedience] = TrainableDefOf.Obedience.steps - 1;
                // A second husky that already knows Obedience: the master and
                // following writes require it (PlayerSettings.Master refuses
                // otherwise), and the dog above must stay one step short so
                // the training vertical keeps a real request to make.
                var guard = animal("Husky", Gender.Male, 8);
                guard.training.Train(TrainableDefOf.Obedience, handler, true);
                if (!guard.training.HasLearned(TrainableDefOf.Obedience)) throw new InvalidOperationException("husbandry fixture: guard did not learn Obedience");
                // Learning Obedience auto-assigns the trainer as master; start unmastered so the master write is observed.
                guard.playerSettings.Master = null;
                // A fresh allowed area covering the enclosure interior for the
                // allowed-area write; the area write refuses ids it cannot find.
                if (!map.areaManager.TryMakeNewAllowed(out var area)) throw new InvalidOperationException("husbandry fixture: could not make an allowed area");
                foreach (var cell in CellRect.FromLimits(origin + new IntVec3(1, 0, 1), origin + new IntVec3(9, 0, 9)).Cells) area[cell] = true;
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
                createdOn.SetTarget(map);
                return new { success = true, mother = mother.GetUniqueLoadID(), father = father.GetUniqueLoadID(),
                    cow = cow.GetUniqueLoadID(), dog = dog.GetUniqueLoadID(), wild = wild.GetUniqueLoadID(), handler = handler.GetUniqueLoadID(),
                    guard = guard.GetUniqueLoadID(), area = area.GetUniqueLoadID(),
                    removedColonists = removed.Select(p => p.GetUniqueLoadID()).ToArray(),
                    setup = "Single-handler fixture: other colonists despawned; roofed enclosure, bed, food and full initial handler needs. Mature full-producing animals, near-term pregnancy, one remaining training step, an obedient guard husky, an allowed area over the enclosure and one factionless tameable muffalo outside. No completed outcome credited to setup; subsequent needs and work use normal rules." };
            }, cancellationToken);
        }
    }
}
