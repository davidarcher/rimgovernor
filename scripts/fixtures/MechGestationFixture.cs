using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#1692, epic #1667): a mechanitor
    // gestates a mech inside its bandwidth and gives it a work order.
    // test/mech_gestation_prepare makes one colonist a mechanitor (a mechlink
    // on the brain), builds a powered mech gestator (a fueled generator on a
    // conduit line beside it), stocks the ingredients of every gestation
    // recipe the mechanitor's free bandwidth can afford, finishes those
    // recipes' research and raises the mechanitor to their skill minimums. The
    // mechanitor's first control group starts in a work mode the controller
    // never writes, so a Work or Escort readback after the birth is the
    // controller's. Which gestator, recipes, ingredients and skills apply are
    // read from the game's defs, never named here. Everything after the
    // staging (the gestation bill, the hauling, the forming, the control group
    // mode) is the game's and the controller's; test/mech_gestation_advance
    // only completes the forming bill's gestation cycles (vanilla's own
    // debug action) so the days-long gestation fits the case's budget.
    public sealed class MechGestationFixture
    {
        [Tool("test/mech_gestation_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#1692): make one colonist a mechanitor, build a mech gestator on a fueled generator's conduit line, stock the ingredients of the gestation recipes its free bandwidth affords, finish their research and skills, and park the mechanitor's first control group in a mode the controller never writes. Requires Biotech.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ModsConfig.BiotechActive) return Refuse("Biotech is not active.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && p.DevelopmentalStage.Adult())
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Any(MechanitorUtility.IsMechanitor)) return Refuse("A colonist is already a mechanitor.");
                if (map.listerBuildings.allBuildingsColonist.Any(b => b is Building_MechGestator || b.TryGetComp<CompPower>() != null))
                    return Refuse("The disposable colony already has a gestator or power buildings; the fixture needs a single deterministic network.");

                var gestatorDef = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(d => d.category == ThingCategory.Building && typeof(Building_MechGestator).IsAssignableFrom(d.thingClass) && d.BuildableByPlayer)
                    .OrderBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
                if (gestatorDef == null) return Refuse("No player-buildable mech gestator def.");
                var chargerDef = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(d => d.category == ThingCategory.Building && typeof(Building_MechCharger).IsAssignableFrom(d.thingClass) && d.BuildableByPlayer)
                    .OrderBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
                if (chargerDef == null) return Refuse("No player-buildable mech charger def.");
                var generatorDef = DefDatabase<ThingDef>.GetNamedSilentFail("WoodFiredGenerator");
                var conduitDef = DefDatabase<ThingDef>.GetNamedSilentFail("HiddenConduit");
                if (generatorDef == null || conduitDef == null) return Refuse("WoodFiredGenerator or HiddenConduit unavailable in this ruleset.");

                // The mechanitor: the first adult colonist who can do the work of
                // every gestation recipe the gestator offers.
                var recipes = gestatorDef.AllRecipes.Where(r => NativeMechBills.Kind(r) != null).ToList();
                if (recipes.Count == 0) return Refuse("The gestator offers no mech gestation recipe.");
                bool Able(Pawn p, RecipeDef r) {
                    var work = NativeBillsObservationTools.WorkType(gestatorDef, r);
                    return work != null && !p.WorkTypeIsDisabled(work)
                        && (r.skillRequirements == null || r.skillRequirements.All(s => p.skills?.GetSkill(s.skill) != null && !p.skills.GetSkill(s.skill).TotallyDisabled));
                }
                var overseer = colonists.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && recipes.Any(r => Able(p, r)));
                if (overseer == null) return Refuse("No adult colonist can do the work of a gestation recipe and haul.");
                var brain = overseer.health.hediffSet.GetBrain();
                if (brain == null) return Refuse("The mechanitor has no brain to carry a mechlink.");
                overseer.health.AddHediff(HediffDefOf.MechlinkImplant, brain);
                var tracker = overseer.mechanitor;
                if (!MechanitorUtility.IsMechanitor(overseer) || tracker == null) return Refuse("The mechlink did not make the colonist a mechanitor.");

                // The recipes this mechanitor can afford: bandwidth is the limit.
                var free = tracker.TotalBandwidth - tracker.UsedBandwidth - tracker.UsedBandwidthFromGestation;
                float Cost(RecipeDef r) => r.ProducedThingDef.GetStatValueAbstract(StatDefOf.BandwidthCost);
                var affordable = recipes.Where(r => Able(overseer, r) && Cost(r) > 0f && Cost(r) <= free).ToList();
                if (affordable.Count == 0) return Refuse("No gestation recipe fits the mechanitor's free bandwidth " + free + ".");

                // Only the cheapest recipe is researched, so the controller's
                // options (and the stock the fixture lays in) are the recipes
                // available without further research.
                var cheapest = affordable.OrderBy(r => r.ingredients.Sum(i => i.GetBaseCount()) * Math.Max(1, r.gestationCycles)).ThenBy(r => r.defName, StringComparer.Ordinal).First();
                foreach (var r in new[] { cheapest }) {
                    if (r.researchPrerequisite != null) Find.ResearchManager.FinishProject(r.researchPrerequisite, false);
                    foreach (var research in r.researchPrerequisites ?? new List<ResearchProjectDef>()) Find.ResearchManager.FinishProject(research, false);
                    if (r.skillRequirements == null) continue;
                    foreach (var s in r.skillRequirements) {
                        var skill = overseer.skills.GetSkill(s.skill);
                        if (skill.Level < s.minLevel) { skill.Level = s.minLevel; skill.xpSinceLastLevel = 0f; }
                    }
                }
                foreach (var research in gestatorDef.researchPrerequisites ?? new List<ResearchProjectDef>()) Find.ResearchManager.FinishProject(research, false);
                affordable = affordable.Where(r => r.AvailableNow).ToList();
                if (!affordable.Contains(cheapest)) return Refuse("The cheapest gestation recipe " + cheapest.defName + " is not available after its research.");
                overseer.workSettings.EnableAndInitialize();
                foreach (var work in affordable.Select(r => NativeBillsObservationTools.WorkType(gestatorDef, r)).Distinct())
                    overseer.workSettings.SetPriority(work, 1);
                foreach (var p in colonists.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))) {
                    p.workSettings.EnableAndInitialize();
                    p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                }

                // A 20x14 clearing near the mechanitor: the conduit line along
                // z=6, the generator just below it and the gestator just above,
                // the stock on the four rows at the bottom and the two at the top.
                const int width = 20, height = 14, line = 6;
                var origin = GenRadial.RadialCellsAround(overseer.Position, 40, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(x => x.InBounds(map) && !x.Fogged(map) && x.Standable(map) && x.GetEdifice(map) == null
                        && x.GetZone(map) == null && !x.GetTerrain(map).IsWater && !x.GetThingList(map).Any(t => t is Pawn))
                    && overseer.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No clear reachable " + width + "x" + height + " area for the gestator.");
                IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);
                Thing Spawn(ThingDef def, int minX, int minZ) {
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    if (def.CanHaveFaction) thing.SetFaction(player);
                    GenSpawn.Spawn(thing, At(minX + (def.size.x - 1) / 2, minZ + (def.size.z - 1) / 2), map, Rot4.North);
                    thing.SetForbidden(false, false);
                    return thing;
                }
                if (generatorDef.size.z > line || line + 1 + gestatorDef.size.z > height - 2) return Refuse("The clearing is too small for the generator and the gestator.");
                for (var x = 0; x < width; x++) Spawn(conduitDef, x, line);
                var generator = Spawn(generatorDef, 2, line - generatorDef.size.z);
                var gestator = Spawn(gestatorDef, 8, line + 1);
                if (8 + gestatorDef.size.x + chargerDef.size.x > width || line + 1 + chargerDef.size.z > height - 2) return Refuse("The clearing is too small for the charger.");
                var charger = Spawn(chargerDef, 8 + gestatorDef.size.x, line + 1);
                generator.TryGetComp<CompRefuelable>().Refuel(generator.TryGetComp<CompRefuelable>().Props.fuelCapacity);
                // The game is paused, so the power nets are not rebuilt until a tick.
                map.powerNetManager.UpdatePowerNetsAndConnections_First();
                var power = gestator.TryGetComp<CompPowerTrader>();
                if (power == null || power.PowerNet == null || !power.PowerNet.powerComps.Any(c => c.parent == generator))
                    return Refuse("The gestator is not on the generator's power network.");
                var chargerPower = charger.TryGetComp<CompPowerTrader>();
                if (chargerPower == null || chargerPower.PowerNet != power.PowerNet)
                    return Refuse("The charger is not on the generator's power network.");

                // The stock: for each affordable recipe, its
                // ingredients for every gestation cycle, each ingredient the
                // cheapest item its filter allows.
                var totals = new Dictionary<ThingDef, int>();
                foreach (var r in affordable) foreach (var ingredient in r.ingredients) {
                    var def = ingredient.filter.AllowedThingDefs.Where(d => d.category == ThingCategory.Item && d.stackLimit > 1 && !d.IsCorpse)
                        .OrderBy(d => d.BaseMarketValue).ThenBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
                    if (def == null) return Refuse("No item satisfies an ingredient of " + r.defName + ".");
                    totals[def] = totals.TryGetValue(def, out var have) ? have : 0;
                    totals[def] += (int)Math.Ceiling(ingredient.GetBaseCount()) * Math.Max(1, r.gestationCycles);
                }
                var wood = ThingDefOf.WoodLog;
                totals[wood] = totals.TryGetValue(wood, out var haveWood) ? haveWood + 150 : 150;
                var cell = 0; var seeded = new List<object>();
                foreach (var pair in totals.OrderBy(p => p.Key.defName, StringComparer.Ordinal)) {
                    for (var remaining = pair.Value; remaining > 0;) {
                        if (cell >= width * 6) return Refuse("No room left in the clearing for the stock (" + totals.Count + " kinds, " + totals.Values.Sum() + " items).");
                        var stack = ThingMaker.MakeThing(pair.Key);
                        stack.stackCount = Math.Min(pair.Key.stackLimit, remaining); remaining -= stack.stackCount;
                        var row = cell / width; // rows 0-3 below the generator, then the two top rows
                        GenSpawn.Spawn(stack, At(cell % width, row < 4 ? row : height - 6 + row), map);
                        stack.SetForbidden(false, false);
                        cell++;
                    }
                    seeded.Add(new { def = pair.Key.defName, count = pair.Value });
                }

                // The park mode: a vanilla mode the controller never writes.
                tracker.controlGroups[0].SetWorkMode(MechWorkModeDefOf.SelfShutdown);

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    mechanitorId = overseer.GetUniqueLoadID(), gestatorId = gestator.GetUniqueLoadID(), chargerId = charger.GetUniqueLoadID(), generatorId = generator.GetUniqueLoadID(),
                    totalBandwidth = tracker.TotalBandwidth, usedBandwidth = tracker.UsedBandwidth, freeBandwidth = free,
                    parkedMode = tracker.controlGroups[0].WorkMode.defName,
                    recipes = affordable.Select(r => (object)new { recipe = r.defName, mechKind = NativeMechBills.Kind(r), bandwidth = Cost(r), gestationCycles = r.gestationCycles }).ToList(),
                    stock = seeded,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/mech_gestation_advance", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#1692): once the gestator's bill is forming (a pawn has started it and hauled its ingredients), complete all of its gestation cycles so the mech forms within game minutes instead of the recipe's game days. Idempotent: advanced is false until a bill is forming.")]
        public async Task<object> Advance(IRimBridgeContext ctx, CancellationToken cancellationToken, string gestatorId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !ModsConfig.BiotechActive) return Refuse("A Biotech map is required.");
                var gestator = map.listerBuildings.allBuildingsColonist.OfType<Building_MechGestator>().FirstOrDefault(b => b.GetUniqueLoadID() == gestatorId);
                if (gestator == null) return Refuse("The gestator is not on the map.");
                var bill = gestator.ActiveMechBill;
                if (bill == null) return new { success = true, advanced = false, tick = Find.TickManager.TicksGame };
                bill.ForceCompleteAllCycles();
                return new { success = true, advanced = true, tick = Find.TickManager.TicksGame, recipe = bill.recipe.defName, cycles = bill.GestationCyclesCompleted };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/mech_gestation_inspect", Description = "Read-only gestation postcondition for the mech gestation fixture: the mechanitor's bandwidth, the mechs it controls (kind, work mech, control group, work mode, bandwidth cost) and the gestation bills on the gestator.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string mechanitorId, string gestatorId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !ModsConfig.BiotechActive) return Refuse("A Biotech map is required.");
                var overseer = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == mechanitorId);
                if (overseer == null || overseer.mechanitor == null) return Refuse("The mechanitor is not on the map.");
                var gestator = map.listerBuildings.allBuildingsColonist.OfType<Building_MechGestator>().FirstOrDefault(b => b.GetUniqueLoadID() == gestatorId);
                var tracker = overseer.mechanitor;
                var mechs = tracker.ControlledPawns.OrderBy(m => m.thingIDNumber).Select(m => (object)new {
                    id = m.GetUniqueLoadID(), kind = m.kindDef.defName, workMech = m.RaceProps.IsWorkMech, dead = m.Dead, spawned = m.Spawned,
                    overseerId = MechanitorUtility.GetOverseer(m)?.GetUniqueLoadID(),
                    controlGroup = MechanitorUtility.GetMechControlGroup(m)?.Index,
                    mode = MechanitorUtility.GetMechWorkMode(m)?.defName,
                    bandwidthCost = m.GetStatValue(StatDefOf.BandwidthCost),
                }).ToList();
                var bills = gestator == null ? new List<object>() : gestator.BillStack.Bills.OfType<Bill_Mech>()
                    .Select(b => (object)new { recipe = b.recipe.defName, mechKind = NativeMechBills.Kind(b.recipe), suspended = b.suspended, finished = BillCommon.IsFinished(b) }).ToList();
                return new {
                    success = true, tick = Find.TickManager.TicksGame, mechanitorDead = overseer.Dead,
                    totalBandwidth = tracker.TotalBandwidth, usedBandwidth = tracker.UsedBandwidth, usedBandwidthFromGestation = tracker.UsedBandwidthFromGestation,
                    mechs, bills, gestatorStanding = gestator != null,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
