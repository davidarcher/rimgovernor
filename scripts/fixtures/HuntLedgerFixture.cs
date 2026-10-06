using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // A disposable hunt: a ranger with a bow, one wild deer and a butcher table.
    // Only the initial state is staged; the shot, the haul and the butchering are
    // native. The fixture tallies the same events through other game methods than
    // the delivery ledger hooks (a corpse spawning, Corpse.ButcherProducts), so the
    // ledger is compared with an independent tally.
    public sealed class HuntLedgerFixture
    {
        private static Map tracked;
        private static readonly HashSet<string> corpses = new HashSet<string>();
        private static double meatUnits, leatherUnits;
        private static bool patched;
        private static Thing table;
        private static WorkTypeDef CookingWork => DefDatabase<WorkTypeDef>.GetNamed("Cooking");

        [Tool("test/ledger_hunt_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage a ranger with a bow, a wild deer designated for hunting and an empty butcher table, and start tallying deer corpses and butchered products.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting) && !p.WorkTypeIsDisabled(CookingWork)).Take(2).ToList();
                if (people.Count != 2) throw new InvalidOperationException("Need two capable colonists.");
                foreach (var zone in map.zoneManager.AllZones.ToList()) zone.Delete();
                foreach (var thing in map.listerThings.AllThings.ToList())
                    if (!(thing is Pawn p && people.Contains(p))) thing.Destroy(DestroyMode.Vanish);
                int mid = map.Size.z / 2;
                var ranger = people[0];
                var cook = people[1];
                foreach (var pawn in people)
                {
                    pawn.jobs.StopAll(); pawn.drafter.Drafted = false;
                    pawn.inventory?.innerContainer.ClearAndDestroyContents(); pawn.carryTracker?.innerContainer.ClearAndDestroyContents();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, 0);
                    for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                }
                ranger.workSettings.SetPriority(WorkTypeDefOf.Hunting, 1);
                cook.workSettings.SetPriority(CookingWork, 1);
                if (!cook.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) cook.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                foreach (var cell in map.AllCells) { map.roofGrid.SetRoof(cell, null); map.fogGrid.Unfog(cell); }
                ranger.Position = new IntVec3(20, 0, mid); ranger.pather.StopDead();
                cook.Position = new IntVec3(24, 0, mid + 6); cook.pather.StopDead();
                ranger.equipment.DestroyAllEquipment();
                var bow = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Bow_Short"), GenStuff.DefaultStuffFor(DefDatabase<ThingDef>.GetNamed("Bow_Short")));
                ranger.equipment.AddEquipment((ThingWithComps)bow);
                var food = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack); food.stackCount = 8;
                GenSpawn.Spawn(food, new IntVec3(22, 0, mid + 2), map); food.SetForbidden(false, false);

                var tableDef = DefDatabase<ThingDef>.GetNamed("TableButcher");
                table = ThingMaker.MakeThing(tableDef, GenStuff.DefaultStuffFor(tableDef));
                table.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(table, new IntVec3(28, 0, mid + 6), map, Rot4.North);

                var deer = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Deer"), null, fixedBiologicalAge: 6f));
                GenSpawn.Spawn(deer, new IntVec3(32, 0, mid), map);
                map.designationManager.AddDesignation(new Designation(deer, DesignationDefOf.Hunt));

                tracked = map; corpses.Clear(); meatUnits = 0; leatherUnits = 0;
                if (!patched)
                {
                    var harmony = new Harmony("rimgovernor.test.hunt-ledger-tally");
                    harmony.Patch(AccessTools.Method(typeof(Corpse), nameof(Corpse.SpawnSetup)), postfix: new HarmonyMethod(typeof(HuntLedgerFixture), nameof(CorpseSpawned)));
                    harmony.Patch(AccessTools.Method(typeof(Corpse), nameof(Corpse.ButcherProducts)), postfix: new HarmonyMethod(typeof(HuntLedgerFixture), nameof(Products)));
                    patched = true;
                }
                return Audit(map);
            }, cancellationToken);
        }

        private static void CorpseSpawned(Corpse __instance)
        {
            if (tracked != null && __instance.Map == tracked && __instance.InnerPawn.RaceProps.Animal) corpses.Add(__instance.GetUniqueLoadID());
        }

        private static IEnumerable<Thing> Products(IEnumerable<Thing> __result, Corpse __instance)
        {
            var counted = tracked != null && __instance.InnerPawn.RaceProps.Animal;
            foreach (var product in __result)
            {
                if (counted && product.def.IsMeat) meatUnits += product.stackCount;
                else if (counted && product.def.IsLeather) leatherUnits += product.stackCount;
                yield return product;
            }
        }

        [Tool("test/ledger_hunt_butcher", Description = "UNSAFE FOR MODEL EXECUTION. Haul the hunted corpse out and back in twice (re-spawn it), then add a forever butcher bill for it; the corpse id stays the same.")]
        public async Task<object> Butcher(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = tracked ?? throw new InvalidOperationException("Prepare the hunt first.");
                var corpse = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().FirstOrDefault(c => c.InnerPawn.RaceProps.Animal && !c.Destroyed && c.Spawned);
                if (corpse == null) throw new InvalidOperationException("No spawned animal corpse to butcher.");
                var id = corpse.GetUniqueLoadID();
                for (int i = 0; i < 2; i++)
                {
                    var cell = corpse.Position;
                    corpse.DeSpawn();
                    GenPlace.TryPlaceThing(corpse, cell, map, ThingPlaceMode.Near);
                    if (!corpse.Spawned) throw new InvalidOperationException("Corpse was not re-placed.");
                }
                var bill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("ButcherCorpseFlesh").MakeNewBill();
                bill.repeatMode = BillRepeatModeDefOf.Forever;
                ((IBillGiver)table).BillStack.AddBill(bill);
                return new { success = true, corpse = id, hauls = 2 };
            }, cancellationToken);
        }

        [Tool("test/ledger_hunt_observe", Description = "Read the fixture's independent deer corpse and butchered product tallies; no mutations.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => Audit(tracked ?? Find.CurrentMap), cancellationToken);
        }

        private static object Audit(Map map)
        {
            var remaining = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Count(c => c.InnerPawn.RaceProps.Animal);
            return new { success = true, tick = Find.TickManager.TicksGame, kills = corpses.Count, corpseIds = corpses.OrderBy(c => c, StringComparer.Ordinal).ToList(),
                butcherMeat = meatUnits, butcherLeather = leatherUnits, corpsesLeft = remaining };
        }
    }
}
