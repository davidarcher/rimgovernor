using System;
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
    // Explicit disposable-fixture setup. Never compiled into production builds.
    public sealed class EmergencyDevelopmentFixture
    {
        private static Game opponentGame;
        private static Map opponentMap;
        private static readonly List<Pawn> SpawnedOpponents = new List<Pawn>();
        [Tool("test/b04f_setup", Description = "Disposable B04f initial conditions: stocks, two manhunters, wound, mental state or native player-order equivalent. No completed-work injection.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "stocks, combat-equipment, ranged-equipment, development-settings (Peaceful), opponents, ranged-opponents, clear-opponents, wound, low-health, resting-patient, resting-injury, unavailable-doctor, external-order, external-draft")] string op,
            [ToolParameter(Description = "Exact colonist identity for pawn cases.", DefaultValue = "")] string pawn = "",
            [ToolParameter(Description = "Desired external-draft value.", DefaultValue = false)] bool drafted = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                var actor = people.FirstOrDefault(p => p.GetUniqueLoadID() == pawn);
                var center = people[0].Position;
                var weapons = new System.Collections.Generic.List<string>();
                if (op == "harvest-plant" || op == "harvest-regrowth") {
                    Plant plant;
                    if (op == "harvest-plant") {
                        var cell = GenRadial.RadialCellsAround(center,8,true).First(c => c.InBounds(map)
                            && c.Standable(map) && !c.Fogged(map) && c.DistanceTo(center)>3
                            && map.fertilityGrid.FertilityAt(c) >= .7f
                            && !c.GetThingList(map).Any(t => t is Plant || t is Building)
                            && map.zoneManager.ZoneAt(c) == null);
                        plant = (Plant)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Plant_Berry")),cell,map);
                    } else {
                        plant = map.listerThings.AllThings.OfType<Plant>().Single(p => p.GetUniqueLoadID() == pawn);
                        if (map.designationManager.DesignationOn(plant) != null || plant.HarvestableNow)
                            throw new InvalidOperationException("First native harvest must finish before fixture regrowth");
                    }
                    plant.Growth = 1f;
                    return new { success=true, op, plant=plant.GetUniqueLoadID(), setupOnly=true,
                        growthInjected=true, completedWorkInjected=false };
                } else if (op == "combat-equipment") {
                    for (var i=0;i<4;i++) {
                        var weapon = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Club"),ThingDefOf.WoodLog);
                        if (!GenPlace.TryPlaceThing(weapon,center,map,ThingPlaceMode.Near)) throw new InvalidOperationException("Weapon setup refused");
                        weapons.Add(weapon.GetUniqueLoadID());
                    }
                } else if (op == "ranged-equipment") {
                    if (actor == null || actor.equipment == null || actor.Downed || actor.InMentalState
                        || actor.WorkTagIsDisabled(WorkTags.Violent) || actor.WorkTagIsDisabled(WorkTags.Shooting))
                        throw new InvalidOperationException("Exact capable ranged fixture pawn required");
                    var prior = actor.equipment.Primary;
                    if (prior != null && !actor.equipment.TryDropEquipment(prior, out _, actor.Position, false))
                        throw new InvalidOperationException("Existing equipment could not be preserved on the ground");
                    var weapon = (ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle"));
                    actor.equipment.AddEquipment(weapon);
                    if (actor.equipment.Primary != weapon) throw new InvalidOperationException("Initial ranged equipment was not assigned");
                    weapons.Add(weapon.GetUniqueLoadID());
                } else if (op == "development-settings") {
                    Current.Game.storyteller = new Storyteller(Find.Storyteller.def, DefDatabase<DifficultyDef>.GetNamed("Peaceful"));
                } else if (op == "stocks") {
                    foreach (var item in new[] { ("WoodLog", 2500), ("Steel", 1200), ("ComponentIndustrial", 40), ("MealSurvivalPack", 300), ("MedicineHerbal", 30) }) {
                        var def = DefDatabase<ThingDef>.GetNamed(item.Item1);
                        for (var left = item.Item2; left > 0;) {
                            var thing = ThingMaker.MakeThing(def); thing.stackCount = Math.Min(def.stackLimit,left); left -= thing.stackCount;
                            if (!GenPlace.TryPlaceThing(thing,center,map,ThingPlaceMode.Near)) throw new InvalidOperationException("Fixture stock placement failed");
                        }
                    }
                } else if (op == "opponents" || op == "ranged-opponents") {
                    if (opponentGame == Current.Game && SpawnedOpponents.Any(p => !p.Destroyed))
                        throw new InvalidOperationException("Fixture opponents already exist; observe them before another setup.");
                    opponentGame = Current.Game; opponentMap = map; SpawnedOpponents.Clear();
                    if (actor != null) center = actor.Position;
                    for (var i=0;i<2;i++) {
                        var animal = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Hare"));
                        var ranged = op == "ranged-opponents";
                        var cell = GenRadial.RadialCellsAround(center,ranged ? 28 : 12,true).First(c => c.InBounds(map)
                            && c.Walkable(map) && c.DistanceTo(center)>(ranged ? 24 : 8) && !c.Fogged(map)
                            && (!ranged || GenSight.LineOfSight(center,c,map)));
                        GenSpawn.Spawn(animal,cell,map);
                        SpawnedOpponents.Add(animal);
                        if (!animal.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent)) throw new InvalidOperationException("Manhunter fixture refused");
                    }
                } else if (op == "clear-opponents") {
                    if (opponentGame != Current.Game || opponentMap != map || SpawnedOpponents.Count != 2
                        || SpawnedOpponents.Any(p => p.Destroyed || !p.Spawned || p.Map != map))
                        throw new InvalidOperationException("Exact fixture-owned opponents are unavailable in this game/map.");
                    var cleared = SpawnedOpponents.Select(p => p.GetUniqueLoadID()).ToArray();
                    foreach (var opponent in SpawnedOpponents) opponent.Destroy(DestroyMode.Vanish);
                    if (SpawnedOpponents.Any(p => !p.Destroyed)) throw new InvalidOperationException("Fixture opponent removal did not complete.");
                    SpawnedOpponents.Clear();
                    return new { success=true, op, cleared, tick=Find.TickManager.TicksGame, setupOnly=true, completedWorkInjected=false };
                } else {
                    if (actor == null) throw new ArgumentException("Exact observed colonist required");
                    OrderedWorkHistory.Read(actor);
                    if (op == "resting-patient") {
                        var cell = GenRadial.RadialCellsAround(center, 8, true).First(c => c.InBounds(map)
                            && c.Standable(map) && !c.Fogged(map) && c.GetEdifice(map) == null
                            && map.thingGrid.ThingsListAt(c).Count == 0);
                        var bed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("SleepingSpot"));
                        bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, cell, map); bed.Medical = true;
                        actor.drafter.Drafted = false;
                        actor.health.AddHediff(HediffDef.Named("CatatonicBreakdown"));
                        actor.Position = bed.Position;
                        actor.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
                        actor.needs.food.CurLevelPercentage = .15f;
                    } else if (op == "resting-injury") {
                        actor.TakeDamage(new DamageInfo(DamageDefOf.Cut, 4, 100));
                    } else if (op == "idle-pawn") {
                        actor.jobs.ClearQueuedJobs();
                        actor.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                        if (actor.CurJob != null || actor.jobs.jobQueue.Count != 0)
                            throw new InvalidOperationException("Exact idle pawn fixture did not become idle.");
                        return new { success=true, op, pawn, currentJobAbsent=true, queuedJobs=0,
                            tick=Find.TickManager.TicksGame, setupOnly=true, completedWorkInjected=false };
                    } else if (op == "external-draft") {
                        var before = actor.Drafted;
                        actor.drafter.Drafted = drafted;
                        return new { success=true, op, pawn, before, after=actor.Drafted,
                            tick=Find.TickManager.TicksGame, setupOnly=true, completedWorkInjected=false };
                    } else if (op == "external-order") {
                        var accepted = actor.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,actor.Position),JobTag.Misc);
                        return new { success=true, op, pawn, accepted, jobId=actor.CurJob?.loadID,
                            jobDef=actor.CurJob?.def.defName, tick=Find.TickManager.TicksGame,
                            setupOnly=true, completedWorkInjected=false };
                    } else if (op == "unavailable-doctor") {
                        if (!actor.mindState.mentalStateHandler.TryStartMentalState(DefDatabase<MentalStateDef>.GetNamed("Wander_Sad"),forceWake:true))
                            throw new InvalidOperationException("Native mental-state fixture refused");
                    } else if (op == "wound" || op == "low-health") {
                        var times = op == "wound" ? 1 : 20;
                        for (var i=0;i<times && !actor.Dead && !actor.Downed;i++) {
                            actor.TakeDamage(new DamageInfo(DamageDefOf.Cut,8,100));
                            if (op == "low-health" && actor.health.summaryHealth.SummaryHealthPercent <= .5f) break;
                        }
                    } else throw new ArgumentException("Unknown fixture operation");
                }
                return new { success=true, op, pawn, weapons, opponents=(op == "opponents" || op == "ranged-opponents") ? SpawnedOpponents.Select(p => p.GetUniqueLoadID()).ToArray() : new string[0], tick=Find.TickManager.TicksGame,
                    orderGeneration=actor==null?(long?)null:OrderedWorkHistory.Read(actor),
                    setupOnly=true, completedWorkInjected=false };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
