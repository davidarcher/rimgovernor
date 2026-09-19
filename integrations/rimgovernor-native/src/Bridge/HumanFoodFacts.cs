#nullable enable
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Obs=RimGovernor.Protocol.Observations;
using Common=RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    // Disposition is a read of the pawn's active precepts and native thought
    // nullifiers. Butchery never removes another pawn's history memories.
    internal static class HumanFoodFacts
    {
        internal static bool IsHumanMeat(ThingDef def) => FoodUtility.GetMeatSourceCategory(def) == MeatSourceCategory.Humanlike;
        internal static bool ContainsHumanMeat(Thing thing) => IsHumanMeat(thing.def)
            || thing.TryGetComp<CompIngredients>()?.ingredients.Any(IsHumanMeat) == true;

        private static bool Accepts(Pawn pawn, params HistoryEventDef[] events)
        {
            if (!pawn.RaceProps.Humanlike) return true;
            if (pawn.Ideo == null) return false;
            return !pawn.Ideo.PreceptsListForReading.SelectMany(p => p.def.comps ?? Enumerable.Empty<PreceptComp>())
                .OfType<PreceptComp_SelfTookMemoryThought>()
                .Any(c => events.Contains(c.eventDef) && c.thought != null
                    && c.thought.stages.Any(s => s.baseMoodEffect < 0)
                    && ThoughtUtility.CanGetThought(pawn, c.thought, true));
        }

        internal static bool AcceptsMeat(Pawn pawn) => pawn.story?.traits?.allTraits.Any(t => t.def.defName == "Cannibal") == true || Accepts(pawn, HistoryEventDefOf.AteHumanMeat,
            HistoryEventDefOf.AteHumanMeatDirect, HistoryEventDefOf.AteHumanMeatAsIngredient);

        internal static bool AcceptsButchery(Pawn pawn) => pawn.RaceProps.Humanlike
            && IdeoUtility.DoerWillingToDo(HistoryEventDefOf.ButcheredHuman, pawn)
            && (pawn.story?.traits?.HasTrait(TraitDefOf.Psychopath) == true
                || pawn.story?.traits?.HasTrait(TraitDefOf.Bloodlust) == true
                || pawn.story?.traits?.allTraits.Any(t => t.def.defName == "Cannibal") == true
                || Accepts(pawn, HistoryEventDefOf.ButcheredHuman));

        internal static bool CanButcher(Pawn pawn, Thing bench) => CanWorkButcher(pawn,bench) && AcceptsButchery(pawn);
        internal static bool CanWorkButcher(Pawn pawn, Thing bench) => pawn.IsFreeColonist
            && !pawn.Dead && !pawn.Downed && !pawn.Drafted && !pawn.InMentalState
            && pawn.workSettings?.Initialized == true && pawn.workSettings.GetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking")) > 0
            && !pawn.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Cooking"))
            && !bench.IsForbidden(pawn) && pawn.Position.DistanceTo(bench.Position) <= 40
            && pawn.CanReach(bench, PathEndMode.InteractionCell, Danger.None);

        internal static double CorpseNutrition(Thing bench) => bench.Map.listerThings.AllThings.OfType<Corpse>()
            .Where(c => c.InnerPawn.RaceProps.Humanlike && c.GetRotStage() == RotStage.Fresh
                && c.InnerPawn.RaceProps.meatDef != null && c.Position.DistanceTo(bench.Position) <= 40
                && !c.IsForbidden(Faction.OfPlayer))
            .Sum(c => (double)c.InnerPawn.GetStatValue(StatDefOf.MeatAmount)
                * c.InnerPawn.RaceProps.meatDef.GetStatValueAbstract(StatDefOf.Nutrition));

        internal static void Fill(Obs.ButcheringFacts row, Thing bench)
        {
            var map=bench.Map;
            var people=map.mapPawns.FreeColonistsSpawned;
            foreach(var pawn in people.OrderBy(p=>p.GetUniqueLoadID(),System.StringComparer.Ordinal)) {
                var candidate=new Obs.HumanButcherCandidate{PawnId=pawn.GetUniqueLoadID(),
                    PreceptAcceptable=pawn.Ideo!=null && !pawn.Ideo.PreceptsListForReading.SelectMany(p=>p.def.comps??Enumerable.Empty<PreceptComp>()).OfType<PreceptComp_SelfTookMemoryThought>().Any(c=>c.eventDef==HistoryEventDefOf.ButcheredHuman && c.thought != null && c.thought.stages.Any(s=>s.baseMoodEffect<0)), CanWork=CanWorkButcher(pawn,bench) && IdeoUtility.DoerWillingToDo(HistoryEventDefOf.ButcheredHuman,pawn)};
                if(pawn.story?.traits!=null)candidate.Traits.Add(pawn.story.traits.allTraits.Select(t=>t.def.defName));
                row.HumanButchers.Add(candidate);
            }
            row.HumanCorpseNutrition=CorpseNutrition(bench);
            var corpse=map.listerThings.AllThings.OfType<Corpse>().FirstOrDefault(c=>c.InnerPawn.RaceProps.Humanlike&&c.GetRotStage()==RotStage.Fresh);
            row.HumanStorageReady=false;
            if(corpse==null)return;
            row.HumanCorpseDef=corpse.def.defName;
            bool Hidden(IntVec3 c)=>c.Roofed(map)&&c.GetRoom(map)?.ProperRoom==true
                && people.All(p=>!GenSight.LineOfSight(p.Position,c,map));
            row.HumanStorageReady=map.zoneManager.AllZones.OfType<Zone_Stockpile>()
                .Any(z=>z.GetStoreSettings().AllowedToAccept(corpse)&&z.Cells.Count>=6&&z.Cells.All(Hidden));
            if(row.HumanStorageReady || !people.Any(p=>CanButcher(p,bench)))return;
            foreach(var cell in GenRadial.RadialCellsAround(bench.Position,35,true)) {
                var cells=CellRect.CenteredOn(cell,3,2).Cells.ToList();
                if(cells.Count!=6||!cells.All(c=>c.InBounds(map)&&!c.Fogged(map)&&c.Standable(map)&&c.GetEdifice(map)==null
                    &&map.zoneManager.ZoneAt(c)==null&&c.GetThingList(map).All(t=>t.def.category!=ThingCategory.Item)
                    &&Hidden(c)&&people.Any(p=>CanButcher(p,bench)&&p.CanReach(c,PathEndMode.OnCell,Danger.None))))continue;
                row.HumanStorageCells.Add(cells.Select(c=>new Common.Cell{X=c.x,Z=c.z}));break;
            }
        }
    }
}
