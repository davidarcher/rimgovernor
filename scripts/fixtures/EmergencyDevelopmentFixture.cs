using System;
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
        [Tool("test/b04f_setup", Description = "Disposable B04f initial conditions: stocks, two manhunters, wound, mental state or native player-order equivalent. No completed-work injection.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "stocks, combat-equipment, development-settings (Peaceful), opponents, wound, low-health, unavailable-doctor, external-order")] string op,
            [ToolParameter(Description = "Exact colonist identity for pawn cases.", DefaultValue = "")] string pawn = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                var actor = people.FirstOrDefault(p => p.GetUniqueLoadID() == pawn);
                var center = people[0].Position;
                var weapons = new System.Collections.Generic.List<string>();
                if (op == "combat-equipment") {
                    for (var i=0;i<4;i++) {
                        var weapon = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Club"),ThingDefOf.WoodLog);
                        if (!GenPlace.TryPlaceThing(weapon,center,map,ThingPlaceMode.Near)) throw new InvalidOperationException("Weapon setup refused");
                        weapons.Add(weapon.GetUniqueLoadID());
                    }
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
                } else if (op == "opponents") {
                    if (actor != null) center = actor.Position;
                    for (var i=0;i<2;i++) {
                        var animal = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Hare"));
                        var cell = GenRadial.RadialCellsAround(center,12,true).First(c => c.InBounds(map) && c.Walkable(map) && c.DistanceTo(center)>8 && !c.Fogged(map));
                        GenSpawn.Spawn(animal,cell,map);
                        if (!animal.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent)) throw new InvalidOperationException("Manhunter fixture refused");
                    }
                } else {
                    if (actor == null) throw new ArgumentException("Exact observed colonist required");
                    OrderedWorkHistory.Read(actor);
                    if (op == "external-order") {
                        actor.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto,actor.Position),JobTag.Misc);
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
                return new { success=true, op, pawn, weapons, tick=Find.TickManager.TicksGame,
                    orderGeneration=actor==null?(long?)null:OrderedWorkHistory.Read(actor),
                    setupOnly=true, completedWorkInjected=false };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
