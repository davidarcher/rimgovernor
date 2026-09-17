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
    // Test-build-only setup; subsequent recovery must use ordinary native ticks.
    public sealed class MoodFixture
    {
        [Tool("test/mood_setup", Description = "Seed deficient needs in one disposable pawn; test builds only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "food, rest, joy, forced, schedule or mental.")] string scenario)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var p = map.mapPawns.FreeColonistsSpawned.First(x => !x.Dead && !x.Downed && x.needs?.mood != null && x.needs.joy != null);
                if (p.InMentalState) p.MentalState.RecoverFromState();
                p.drafter.Drafted = false;
                for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, scenario == "schedule" ? TimeAssignmentDefOf.Work : TimeAssignmentDefOf.Anything);
                p.needs.food.CurLevelPercentage = scenario == "food" ? 0.1f : 0.9f;
                p.needs.rest.CurLevelPercentage = scenario == "rest" ? 0.1f : 0.9f;
                p.needs.joy.CurLevelPercentage = scenario == "rest" || scenario == "food" ? 0.9f : 0.1f;
                p.needs.mood.CurLevelPercentage = p.mindState.mentalBreaker.BreakThresholdMinor - 0.01f;
                var wait = JobMaker.MakeJob(JobDefOf.Wait, 25000);
                wait.playerForced = scenario == "forced";
                p.jobs.StartJob(wait, JobCondition.InterruptForced);
                if (scenario == "food")
                {
                    var meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                    meal.stackCount = 5;
                    GenPlace.TryPlaceThing(meal, p.Position, map, ThingPlaceMode.Near);
                    meal.SetForbidden(false, false);
                }
                else if (scenario != "rest")
                    EnsureJoySource(map, p);
                if (scenario == "mental")
                    p.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forceWake: true);
                return new { success = true, pawn = p.GetUniqueLoadID(), scenario,
                    setup = "Test-only need/timetable/job setup; no production game tools mutate needs or mental states." };
            }, cancellationToken);
        }

        // The harnesses run against a fresh debug-start colony (#91/#92) that
        // owns no recreation building, and the only building-free joy giver
        // (skygazing) depends on daylight and clear weather, so JobGiver_GetJoy
        // would have nothing deterministic to issue for the joy scenarios. A
        // horseshoes pin (Core, JoyGiver_WatchBuilding: the pawn stands five
        // cells away in a three-wide rect) on cleared unroofed ground near the
        // pawn gives native relief a real job, the same way the food scenario
        // drops a meal beside them.
        private static void EnsureJoySource(Map map, Pawn p)
        {
            var pinDef = DefDatabase<ThingDef>.GetNamed("HorseshoesPin");
            if (map.listerThings.ThingsOfDef(pinDef).Any(t => t.Faction == Faction.OfPlayerSilentFail && !t.IsForbidden(p))) return;
            var center = GenRadial.RadialCellsAround(p.Position, 30, true).First(c =>
                CellRect.CenteredOn(c, 7).Cells.All(v => v.InBounds(map) && !v.Fogged(map) && !v.Roofed(map)
                    && v.Standable(map) && v.GetEdifice(map) == null && map.zoneManager.ZoneAt(v) == null));
            foreach (var c in CellRect.CenteredOn(center, 7))
                foreach (var t in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) t.Destroy();
            var pin = ThingMaker.MakeThing(pinDef, ThingDefOf.WoodLog);
            pin.SetFaction(Faction.OfPlayerSilentFail);
            GenSpawn.Spawn(pin, center, map);
            pin.SetForbidden(false, false);
        }
    }
}
