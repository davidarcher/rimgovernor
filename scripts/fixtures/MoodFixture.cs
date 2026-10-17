using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse.AI.Group;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Test-build-only setup; subsequent recovery must use ordinary native ticks.
    public sealed class MoodFixture
    {
        private static IEnumerable<Pawn> MoodColonists(Map map, string id) =>
            map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && p.needs?.mood != null && (string.IsNullOrEmpty(id) || p.GetUniqueLoadID() == id))
                .OrderBy(p => p.thingIDNumber);

        private static object[] Memories(Pawn pawn) =>
            pawn.needs.mood.thoughts.memories.Memories.Select(m => (object)new { def = m.def.defName, offset = m.MoodOffset() }).ToArray();

        // Mood is pinned natively with no clock: the unfrozen Mood need seeks the instantaneous level (base plus every thought), so a
        // calibrating memory whose moodPowerFactor makes that level land exactly on the target keeps CurLevel there through ordinary ticks (#2549).
        [Tool("test/mood_headroom", Description = "UNSAFE FOR MODEL EXECUTION. Hold one disposable colonist's mood exactly `headroom` above its native minor-break threshold with a calibrating memory (MyOrganHarvested to lower, Catharsis to raise), replacing any earlier calibration (#2549).")]
        public async Task<object> Headroom(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Pawn load id; empty takes the lowest-id colonist.")] string pawn,
            [ToolParameter(Description = "Mood level above the minor-break threshold, e.g. 0.07.")] double headroom)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded colony is required.");
                var p = MoodColonists(map, pawn).FirstOrDefault(x => !x.Downed && !x.InMentalState) ?? throw new InvalidOperationException("No eligible colonist.");
                var mood = p.needs.mood;
                var lower = ThoughtDef.Named("MyOrganHarvested");
                var raise = ThoughtDef.Named("Catharsis");
                mood.thoughts.memories.RemoveMemoriesOfDef(lower);
                mood.thoughts.memories.RemoveMemoriesOfDef(raise);
                mood.thoughts.situational.Notify_SituationalThoughtsDirty();
                var threshold = p.mindState.mentalBreaker.BreakThresholdMinor;
                var difficulty = Find.Storyteller.difficulty.colonistMoodOffset;
                var baseLevel = p.health.hediffSet.OverrideMoodBase ?? mood.def.baseLevel;
                var target = threshold + (float)headroom;
                var extra = (target - baseLevel) * 100f - mood.thoughts.TotalMoodOffset() - difficulty;
                var def = extra < 0f ? lower : raise;
                var memory = (Thought_Memory)ThoughtMaker.MakeThought(def);
                memory.moodPowerFactor = extra / def.stages[0].baseMoodEffect;
                mood.thoughts.memories.TryGainMemory(memory);
                mood.CurLevel = target;
                var instant = UnityEngine.Mathf.Clamp01(baseLevel + (mood.thoughts.TotalMoodOffset() + difficulty) / 100f);
                if (UnityEngine.Mathf.Abs(instant - target) > 0.005f)
                    throw new InvalidOperationException($"Mood did not calibrate: instant {instant} target {target}.");
                return new { success = true, pawn = p.GetUniqueLoadID(), threshold, mood = mood.CurLevel, instant, headroom = instant - threshold, calibrator = def.defName };
            }, cancellationToken);
        }

        [Tool("test/mood_thoughts", Description = "UNSAFE FOR MODEL EXECUTION. Give one colonist (or every colonist when pawn is empty) a named memory thought scaled to exactly `loss` mood points, or clear that def; replies each colonist's memories (#2549).")]
        public async Task<object> Thoughts(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Pawn load id, or first for the lowest-id colonist; empty means every free colonist.")] string pawn,
            [ToolParameter(Description = "Memory ThoughtDef defName, e.g. SleptOutside.")] string thought,
            [ToolParameter(Description = "Mood points the memory should carry (positive magnitude; the def's own sign is kept).")] double loss,
            [ToolParameter(Description = "Remove the def's memories instead of adding.")] bool clear = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded colony is required.");
                var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(thought);
                if (def == null || !def.IsMemory) throw new InvalidOperationException("Unknown memory thought.");
                var pawns = MoodColonists(map, pawn == "first" ? "" : pawn).Take(pawn == "first" ? 1 : int.MaxValue).ToList();
                if (pawns.Count == 0) throw new InvalidOperationException("No colonist.");
                foreach (var p in pawns)
                {
                    p.needs.mood.thoughts.memories.RemoveMemoriesOfDef(def);
                    if (clear) continue;
                    var memory = (Thought_Memory)ThoughtMaker.MakeThought(def);
                    memory.moodPowerFactor = (float)loss / Math.Abs(def.stages[0].baseMoodEffect);
                    p.needs.mood.thoughts.memories.TryGainMemory(memory);
                    if (!p.needs.mood.thoughts.memories.Memories.Any(m => m.def == def))
                        throw new InvalidOperationException($"{thought} was not kept by {p.LabelShort} (nullified).");
                }
                return new { success = true, pawns = pawns.Select(p => new { pawn = p.GetUniqueLoadID(), memories = Memories(p) }).ToArray() };
            }, cancellationToken);
        }

        [Tool("test/party_spot", Description = "UNSAFE FOR MODEL EXECUTION. Build one colony PartySpot on a standable cell beside the lowest-id colonist (unless one stands) and reply the cell (#2549).")]
        public async Task<object> PartySpot(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded colony is required.");
                var def = ThingDef.Named("PartySpot");
                var existing = map.listerBuildings.AllBuildingsColonistOfDef(def).FirstOrDefault();
                if (existing == null)
                {
                    var colonist = MoodColonists(map, "").First();
                    var cell = CellFinder.StandableCellNear(colonist.Position, map, 8);
                    var spot = ThingMaker.MakeThing(def);
                    spot.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(spot, cell, map);
                    existing = spot as Building ?? throw new InvalidOperationException("The PartySpot is not a building.");
                }
                return new { success = true, x = existing.Position.x, z = existing.Position.z, spots = map.listerBuildings.AllBuildingsColonistOfDef(def).Count() };
            }, cancellationToken);
        }

        // Native read of the claim #2549 (c) makes: how many lord jobs of one GatheringDef run, plus the game's own gate readings so a
        // refused start names which gate failed.
        [Tool("test/gathering_lords", Description = "UNSAFE FOR MODEL EXECUTION. Count running lord jobs of one GatheringDef (organizer and spot of the first) and read the game's gathering gates (#2549).")]
        public async Task<object> GatheringLords(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "GatheringDef defName, e.g. Party.")] string def)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded colony is required.");
                var gathering = DefDatabase<GatheringDef>.GetNamedSilentFail(def) ?? throw new InvalidOperationException("Unknown gathering def.");
                var jobs = Find.Maps.SelectMany(m => m.lordManager.lords).Select(l => l.LordJob as LordJob_Joinable_Gathering)
                    .Where(j => j != null && Traverse.Create(j).Field("gatheringDef").GetValue<GatheringDef>() == gathering).ToList();
                var colonists = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                var organizer = colonists.FirstOrDefault();
                var spotDef = gathering.gatherSpotDefs?.FirstOrDefault();
                var memory = ThoughtDef.Named("AttendedParty");
                return new {
                    success = true,
                    lords = jobs.Count,
                    organizer = jobs.Count > 0 ? jobs[0].Organizer?.GetUniqueLoadID() : null,
                    eligibleOrganizer = colonists.FirstOrDefault(p => GatheringsUtility.PawnCanStartOrContinueGathering(p) && gathering.CanExecute(map,p))?.GetUniqueLoadID(),
                    spotX = jobs.Count > 0 ? jobs[0].Spot.x : -1,
                    spotZ = jobs.Count > 0 ? jobs[0].Spot.z : -1,
                    colonists = colonists.Count,
                    spots = spotDef == null ? 0 : map.listerBuildings.AllBuildingsColonistOfDef(spotDef).Count(),
                    hour = GenLocalDate.HourInteger(map),
                    acceptable = GatheringsUtility.AcceptableGameConditionsToStartGathering(map, gathering),
                    canExecute = organizer != null && gathering.CanExecute(map, organizer),
                    attendedMemories = colonists.Sum(p => p.needs?.mood?.thoughts.memories.Memories.Count(m => m.def == memory) ?? 0),
                    ideology = ModsConfig.IdeologyActive,
                };
            }, cancellationToken);
        }

        [Tool("test/mood_setup",Description = "Seed deficient needs in one disposable pawn; test builds only.")]
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
                    // Removable environment pressure: a SleptOutside
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

        // The harnesses run against a fresh debug-start colony  that
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
        // impassable things do. The fresh debug-start map has no
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
