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
        [Tool("test/mental_state_berserk", Description = "UNSAFE FOR MODEL EXECUTION. Induce Berserk in one paused disposable colonist for mental-state readback.")]
        public async Task<object> Berserk(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && !p.InMentalState);
                pawn.drafter.Drafted = false;
                if (!pawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, forceWake: true)
                    || pawn.MentalStateDef != MentalStateDefOf.Berserk)
                    throw new InvalidOperationException("Berserk did not start.");
                return new { success = true, pawn = pawn.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/inspire_creativity", Description = "UNSAFE FOR MODEL EXECUTION. Give one paused disposable colonist Inspired_Creativity for inspiration readback.")]
        public async Task<object> InspireCreativity(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var def = DefDatabase<InspirationDef>.GetNamed("Inspired_Creativity");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && !p.InMentalState && p.mindState?.inspirationHandler != null && !p.Inspired);
                if (!pawn.mindState.inspirationHandler.TryStartInspiration(def) || pawn.InspirationDef != def)
                    throw new InvalidOperationException("Inspired_Creativity did not start.");
                return new { success = true, pawn = pawn.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/pacifist_ignore", Description = "UNSAFE FOR MODEL EXECUTION. Give one paused disposable colonist an adulthood backstory that disables violence and set its hostility response to Ignore (#1299).")]
        public async Task<object> PacifistIgnore(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && p.story != null && p.playerSettings != null);
                var backstory = DefDatabase<BackstoryDef>.AllDefsListForReading
                    .Where(b => b.slot == BackstorySlot.Adulthood && (b.workDisables & WorkTags.Violent) != 0)
                    .OrderBy(b => b.workDisables.ToString().Length).ThenBy(b => b.defName).First();
                pawn.story.Adulthood = backstory;
                pawn.Notify_DisabledWorkTypesChanged();
                pawn.equipment?.DestroyAllEquipment();
                pawn.playerSettings.hostilityResponse = HostilityResponseMode.Ignore;
                if (!pawn.WorkTagIsDisabled(WorkTags.Violent))
                    throw new InvalidOperationException("Violence is still enabled.");
                return new { success = true, pawn = pawn.GetUniqueLoadID(), backstory = backstory.defName };
            }, cancellationToken);
        }
        [Tool("test/lone_self_tend_off", Description = "UNSAFE FOR MODEL EXECUTION. Vanish every free colonist but one paused disposable doctor-capable colonist and turn its self-tend off (#1305).")]
        public async Task<object> LoneSelfTendOff(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .First(p => !p.Dead && !p.Downed && p.playerSettings != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                foreach (var other in map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn).ToList())
                    other.Destroy(DestroyMode.Vanish);
                pawn.playerSettings.selfTend = false;
                if (map.mapPawns.FreeColonistsSpawned.Count != 1)
                    throw new InvalidOperationException("More than one colonist remains.");
                return new { success = true, pawn = pawn.GetUniqueLoadID() };
            }, cancellationToken);
        }
        [Tool("test/duplicate_nickname", Description = "UNSAFE FOR MODEL EXECUTION. Give the newest of two paused disposable colonists the oldest one's nickname (#1310).")]
        public async Task<object> DuplicateNickname(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A paused disposable colony is required.");
                var named = map.mapPawns.FreeColonistsSpawned.Where(p => p.Name is NameTriple).OrderBy(p => p.thingIDNumber).ToList();
                if (named.Count < 2) throw new InvalidOperationException("Two named colonists are required.");
                var older = named[0]; var newer = named[named.Count - 1];
                var nick = older.Name.ToStringShort;
                var triple = (NameTriple)newer.Name;
                newer.Name = new NameTriple(triple.First, nick, triple.Last);
                return new { success = true, pawn = newer.GetUniqueLoadID(), keeper = older.GetUniqueLoadID(), name = nick };
            }, cancellationToken);
        }
        [Tool("test/mood_setup", Description = "Seed deficient needs in one disposable pawn; test builds only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "food, rest, joy, forced, schedule, mental or environment.")] string scenario)
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
                // The environment scenario leaves the pawn free to recover on
                // its own (a long Wait would keep it from playing); the others
                // pin a known current job for the relief fencing.
                var wait = JobMaker.MakeJob(JobDefOf.Wait, scenario == "environment" ? 60 : 25000);
                wait.playerForced = scenario == "forced";
                p.jobs.StartJob(wait, JobCondition.InterruptForced);
                if (scenario == "environment")
                {
                    // Removable environment pressure (#255): a SleptOutside
                    // memory plus the NeedJoy situational thought the joy
                    // level above triggers, recalculated now so the first
                    // social read already carries it.
                    p.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.SleptOutside);
                    p.needs.mood.thoughts.situational.Notify_SituationalThoughtsDirty();
                    p.needs.mood.thoughts.TotalMoodOffset();
                }
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
            var center = FindPinCell(map, p);
            foreach (var c in CellRect.CenteredOn(center, 7).ClipInsideMap(map))
                foreach (var t in c.GetThingList(map).Where(Clearable).ToList()) t.Destroy();
            var pin = ThingMaker.MakeThing(pinDef, ThingDefOf.WoodLog);
            pin.SetFaction(Faction.OfPlayerSilentFail);
            GenSpawn.Spawn(pin, center, map);
            pin.SetForbidden(false, false);
        }

        // Plants, items and filth get cleared before the pin spawns, so they
        // must not disqualify a cell; only terrain, edifices and non-clearable
        // impassable things do. The fresh debug-start map (#92) has no
        // guaranteed 7x7 patch of bare standable ground within 30 cells of the
        // pawn, so the search widens in radius and shrinks the rect before
        // falling back to the pawn's own cell rather than throwing.
        private static IntVec3 FindPinCell(Map map, Pawn p)
        {
            foreach (var size in new[] { 7, 5, 3 })
                foreach (var radius in new[] { 30f, 55f })
                {
                    var found = GenRadial.RadialCellsAround(p.Position, radius, true)
                        .FirstOrDefault(c => CellRect.CenteredOn(c, size).Cells.All(v => PinFriendly(map, v)));
                    if (found.IsValid) return found;
                }
            var any = GenRadial.RadialCellsAround(p.Position, 55f, true).FirstOrDefault(c => PinFriendly(map, c));
            return any.IsValid ? any : p.Position;
        }

        private static bool PinFriendly(Map map, IntVec3 v)
        {
            if (!v.InBounds(map) || v.Fogged(map) || v.Roofed(map)) return false;
            if (v.GetTerrain(map).passability == Traversability.Impassable) return false;
            if (v.GetEdifice(map) != null || map.zoneManager.ZoneAt(v) != null) return false;
            return v.GetThingList(map).All(t => Clearable(t) || t.def.passability != Traversability.Impassable);
        }

        private static bool Clearable(Thing t) =>
            t is Plant || t is Filth || t.def.category == ThingCategory.Item;
    }
}
