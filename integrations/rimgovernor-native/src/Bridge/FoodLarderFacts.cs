#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Read-only native facts. No bill.ShouldDoNow calls (that mutates paused).
    internal static class FoodLarderFacts
    {
        internal sealed class Snapshot {
            internal double RawMeatNutrition, CookDemandNutrition;
            internal readonly List<Handling> Corpses = new List<Handling>();
            internal readonly List<IntVec3> ColdSites = new List<IntVec3>();
        }
        internal sealed class Handling {
            internal string ID = "";
            internal IntVec3 Cell;
            internal string? Hauler;
            internal bool FrozenDestination;
        }
        internal static bool Frozen(IntVec3 cell, Map map) => cell.Roofed(map)
            && cell.GetRoom(map)?.ProperRoom == true && cell.GetTemperature(map) <= 0;

        internal static Snapshot? Read(List<Pawn> people, List<Thing> shared, List<FoodSupplyFacts.StockFacts> stocks)
        {
            var map = people.FirstOrDefault()?.Map;
            if (map == null) return null;
            var result = new Snapshot();
            var ids = new HashSet<string>(stocks.Where(s => s.corpse).Select(s => s.id!));
            if (ids.Count == 0) return result;
            var meatDefs = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsMeat && d.IsNutritionGivingIngestible).ToList();
            result.RawMeatNutrition = shared.Where(t => !(t is Corpse) && t.def.IsMeat && !t.IsForbidden(Faction.OfPlayer))
                .Sum(t => (double)t.stackCount * t.def.GetStatValueAbstract(StatDefOf.Nutrition));
            foreach (var bench in map.listerThings.AllThings.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer))
            foreach (var bill in bench.BillStack.Bills.OfType<Bill_Production>())
            {
                if (bill.suspended || bill.paused || bill.recipe.products == null
                    || !bill.recipe.products.Any(p => p.thingDef.IsNutritionGivingIngestible && !p.thingDef.IsMeat)
                    || bill.repeatMode == BillRepeatModeDefOf.RepeatCount && bill.repeatCount <= 0) continue;
                foreach (var ingredient in bill.recipe.ingredients)
                {
                    var amounts = meatDefs.Where(d => ingredient.filter.Allows(d) && bill.ingredientFilter.Allows(d))
                        .Select(d => (double)ingredient.CountRequiredOfFor(d, bill.recipe, bill) * d.GetStatValueAbstract(StatDefOf.Nutrition)).ToList();
                    if (amounts.Count > 0) result.CookDemandNutrition += amounts.Max();
                }
            }
            var haulers = people.Where(p => p.IsColonist && !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hauling) == true).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            foreach (var corpse in shared.OfType<Corpse>().Where(c => ids.Contains(c.GetUniqueLoadID())).OrderBy(c => c.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                if (EventLootFacts.Safe(corpse) != true) continue;
                var row = new Handling { ID = corpse.GetUniqueLoadID(), Cell = corpse.Position };
                foreach (var pawn in haulers)
                {
                    if (!pawn.CanReach(corpse, PathEndMode.Touch, Danger.None)) continue;
                    if (StoreUtility.TryFindBestBetterStoreCellFor(corpse, pawn, map, StoragePriority.Unstored, Faction.OfPlayer, out var dest)
                        && Frozen(dest, map)) { row.Hauler = pawn.GetUniqueLoadID(); row.FrozenDestination = true; break; }
                }
                result.Corpses.Add(row);
            }
            if (result.Corpses.Count > 0)
                foreach (var cell in map.AllCells.Where(c => !c.Fogged(map) && c.Standable(map) && Frozen(c, map)
                    && map.zoneManager.ZoneAt(c) == null && c.GetEdifice(map) == null && c.GetThingList(map).All(t => t.def.category != ThingCategory.Item)).Take(16))
                    result.ColdSites.Add(cell);
            return result;
        }
    }
}
